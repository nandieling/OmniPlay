#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""OmniPlay 字幕转档（瘦身版）。

只做一件事：把媒体库里视频内封的字幕抽成贴着视频的 sidecar
（``片名.zh.srt`` / ``片名.zh.ass`` / ``片名.zh.sup``）写回原目录。
安卓 TV 端播放时直接读这个小文件，走现成的字幕覆盖层，于是高性能直出
模式下也能看到字幕。

对比 docker 全功能版：没有刮削、没有海报、没有用户体系、没有 Web 前端，
只有「扫库 -> 抽字幕 -> 写回」这一条链路，所以镜像里只装 python3 + ffmpeg。

只依赖 Python 3 标准库和 ffmpeg / ffprobe。
"""

import argparse
import io
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

VERSION = "1.0"
USER_AGENT = "OmniPlay-Sidecar/" + VERSION

# 主动扫库只碰这些容器。蓝光分卷（.m2ts/.ts）和 ISO 由 TV 端点名指定——
# 一盘蓝光里哪条是正片只有 TV 端的扫描逻辑知道，NAS 不该把整盘都抽一遍。
WALK_SUFFIXES = {".mkv", ".webm", ".mp4", ".m4v"}
PUSH_SUFFIXES = {".m2ts", ".mts", ".ts", ".iso"}

# 字幕轨 -> 目标格式。文本轨统一收成 srt/ass，PGS 原样封成 .sup。
TEXT_CODECS = {
    "subrip": "srt",
    "srt": "srt",
    "webvtt": "srt",
    "mov_text": "srt",
    "ass": "ass",
    "ssa": "ass",
}
PGS_CODECS = {"hdmv_pgs_subtitle"}

# 语言码归一，跟安卓 TV 端 MatroskaSubtitleExtractor.normalizeLanguage 保持一致，
# 两边写出来的文件名才能对上。
LANGUAGE_ALIASES = {
    "chi": "zh", "zho": "zh", "cmn": "zh", "yue": "zh",
    "eng": "en", "jpn": "ja", "kor": "ko", "deu": "de", "ger": "de",
    "fra": "fr", "fre": "fr", "spa": "es", "ita": "it", "rus": "ru",
    "por": "pt", "tha": "th", "vie": "vi", "ind": "id", "hin": "hi",
}

SKIPPED_DIRS = {"lost+found", "lost & found", "@eadir"}

# ffmpeg -progress 吐的那些字段，认得出来的才当进度看，其余的行留着当报错用。
PROGRESS_KEYS = {"frame", "fps", "q", "size", "size_lbr", "bitrate", "time",
                 "out_time_ms", "out_time_us", "out_time", "dup_frames",
                 "drop_frames", "speed", "progress", "total_size", "encoded_frame"}
TERMINAL_STATUSES = {"done", "skipped", "unsupported", "readonly"}


def normalize_language(tag):
    """``chi``/``zh-CN``/``zho`` 都收成 ``zh``；没有语言标就记 ``und``。"""
    raw = (tag or "").strip().lower().replace("_", "-")
    if not raw:
        return "und"
    base = raw.split("-")[0]
    if len(base) == 3:
        return LANGUAGE_ALIASES.get(base, base)
    return base


def sidecar_path(video_path, language, fmt):
    """``/media/Movies/A.mkv`` + zh + srt -> ``/media/Movies/A.zh.srt``"""
    stem = os.path.splitext(video_path)[0]
    return stem + "." + language + "." + fmt


WEB_ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "web")
_page_cache = {"mtime": 0.0, "body": ""}


def index_page():
    """读 GUI 页面。文件不存在时退回一行纯文本，服务不能因为缺页面起不来。"""
    index = os.path.join(WEB_ROOT, "index.html")
    try:
        mtime = os.path.getmtime(index)
    except OSError:
        return ("text/html; charset=utf-8",
                "<meta charset=utf-8>OmniPlay 字幕转档：找不到 web/index.html。"
                "<p>接口还能用：<a href=/health>/health</a>")
    if mtime != _page_cache["mtime"]:
        with io.open(index, encoding="utf-8") as handle:
            _page_cache["body"] = handle.read()
        _page_cache["mtime"] = mtime
    return "text/html; charset=utf-8", _page_cache["body"]


def split_list(raw):
    """配置里的列表既能用半角逗号也能用全角标点分隔。"""
    return [item for item in re.split(r"[,\uff0c;\uff1b\s]+", raw or "") if item]


def log(message):
    print(time.strftime("[%H:%M:%S] ") + message, flush=True)


# --------------------------------------------------------------------------- #
# 状态表
# --------------------------------------------------------------------------- #

SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    path        TEXT PRIMARY KEY,  -- 相对媒体库根目录的路径
    abs_path    TEXT,
    out_path    TEXT,
    video_size  INTEGER,
    video_mtime INTEGER,
    track_index INTEGER,
    codec       TEXT,
    language    TEXT,
    format      TEXT,
    status      TEXT,
    detail      TEXT,
    covered     REAL,
    out_size    INTEGER,
    attempts    INTEGER,
    updated_at  REAL,
    width       INTEGER,
    height      INTEGER
);
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);
"""

COLUMNS = ("abs_path", "out_path", "video_size", "video_mtime", "track_index",
           "codec", "language", "format", "status", "detail", "covered",
           "out_size", "attempts", "updated_at", "width", "height")

# 列表页按分辨率分类：没有视频轨或没探出来的归进 unknown。
RES_GROUP = ("CASE WHEN height IS NULL OR height = 0 THEN 'unknown' "
             "WHEN height >= 2160 THEN 'uhd' "
             "WHEN height >= 1080 THEN 'fhd' ELSE 'sdp' END")

# GUI 上那几个状态标签对应的真实状态。
# 顶部每一格计数都对应一个筛选项，所以一格一状态，不再把「转不了」和「跳过」混在一起。
STATUS_GROUPS = {"all": None, "pending": ["pending"], "running": ["running"],
                 "done": ["done"], "failed": ["failed"], "readonly": ["readonly"],
                 "unsupported": ["unsupported"], "skipped": ["skipped"],
                 "problem": ["failed", "readonly", "unsupported"]}
RES_GROUPS = ("", "uhd", "fhd", "sdp", "unknown")


class Store:
    """一个 sqlite 文件记全部进度：重启不重做，失败可重试。"""

    def __init__(self, db_path):
        parent = os.path.dirname(os.path.abspath(db_path))
        if parent:
            os.makedirs(parent, exist_ok=True)
        self.lock = threading.Lock()
        self.conn = sqlite3.connect(db_path, check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        self.conn.executescript(SCHEMA)
        self.migrate()
        self.conn.commit()

    def migrate(self):
        """老的状态表没有分辨率两列，就地补上，别逼用户删库重扫。"""
        have = {row[1] for row in self.conn.execute("PRAGMA table_info(jobs)")}
        for column in ("width", "height"):
            if column not in have:
                self.conn.execute("ALTER TABLE jobs ADD COLUMN %s INTEGER" % column)

    def row(self, path):
        with self.lock:
            cursor = self.conn.execute("SELECT * FROM jobs WHERE path = ?", (path,))
            found = cursor.fetchone()
        return dict(found) if found else None

    def upsert(self, path, **fields):
        fields.setdefault("attempts", 0)
        fields["updated_at"] = time.time()
        names = ["path"] + [name for name in COLUMNS if name in fields]
        values = [path] + [fields[name] for name in names[1:]]
        assignments = ", ".join(name + " = excluded." + name for name in names[1:])
        with self.lock:
            self.conn.execute(
                "INSERT INTO jobs (%s) VALUES (%s) ON CONFLICT(path) DO UPDATE SET %s"
                % (", ".join(names), ", ".join("?" * len(names)), assignments),
                values)
            self.conn.commit()

    def patch(self, path, **fields):
        fields["updated_at"] = time.time()
        assignments = ", ".join(name + " = ?" for name in fields)
        with self.lock:
            self.conn.execute(
                "UPDATE jobs SET %s WHERE path = ?" % assignments,
                list(fields.values()) + [path])
            self.conn.commit()

    def forget(self, path):
        with self.lock:
            self.conn.execute("DELETE FROM jobs WHERE path = ?", (path,))
            self.conn.commit()

    def pending(self, limit=1):
        with self.lock:
            cursor = self.conn.execute(
                "SELECT path FROM jobs WHERE status = 'pending' ORDER BY updated_at LIMIT ?",
                (limit,))
            return [item[0] for item in cursor.fetchall()]

    def recent(self, limit):
        with self.lock:
            cursor = self.conn.execute(
                "SELECT * FROM jobs ORDER BY updated_at DESC LIMIT ?", (limit,))
            return [dict(item) for item in cursor.fetchall()]

    def summary(self):
        with self.lock:
            cursor = self.conn.execute(
                "SELECT status, COUNT(*) AS n FROM jobs GROUP BY status")
            counts = {item[0]: item[1] for item in cursor.fetchall()}
        return counts

    def page(self, statuses=None, needle="", res="", offset=0, limit=50):
        """列表页：状态分组 + 文件名 + 分辨率三样都能筛，按更新时间倒序翻页。"""
        where, args = [], []
        if statuses:
            where.append("status IN (%s)" % ", ".join(["?"] * len(statuses)))
            args.extend(statuses)
        if needle:
            where.append("lower(path) LIKE ?")
            args.append("%" + needle.lower() + "%")
        if res:
            where.append(RES_GROUP + " = ?")
            args.append(res)
        clause = (" WHERE " + " AND ".join(where)) if where else ""
        with self.lock:
            total = self.conn.execute(
                "SELECT COUNT(*) FROM jobs" + clause, args).fetchone()[0]
            cursor = self.conn.execute(
                "SELECT * FROM jobs%s ORDER BY updated_at DESC LIMIT ? OFFSET ?" % clause,
                list(args) + [limit, offset])
            return [dict(item) for item in cursor.fetchall()], total

    def setting(self, key, default=None):
        with self.lock:
            row = self.conn.execute(
                "SELECT value FROM settings WHERE key = ?", (key,)).fetchone()
        return row[0] if row else default

    def set_setting(self, key, value):
        with self.lock:
            self.conn.execute("INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)",
                              (key, str(value)))
            self.conn.commit()

    def forget_resolution_skips(self, floor=0):
        """改了「只转多大分辨率」之后，旧判定要能重新探一遍。

        按策略跳过的那批全部抹掉（阈值可能调低了）；还排在队列里、探出来确实低于
        新阈值的也抹掉，下次扫描会给出「按策略不转」这个正经结论，而不是继续转完。
        已经转好的不动——sidecar 都写出来了，没必要倒回去。
        """
        with self.lock:
            cursor = self.conn.execute(
                "DELETE FROM jobs WHERE (status = 'skipped' AND detail LIKE '%按策略不转%') "
                "OR (status = 'pending' AND height > 0 AND height < ?)", (int(floor),))
            self.conn.commit()
            return cursor.rowcount


# --------------------------------------------------------------------------- #
# 转档服务
# --------------------------------------------------------------------------- #

class Service:
    def __init__(self, config):
        self.cfg = config
        self.store = Store(config.db)
        self.priority = []
        self.priority_lock = threading.Lock()
        self.wake = threading.Event()
        self.bluray = False
        self.stats = {"scans": 0, "converted": 0, "failed": 0}
        self.scanning = False
        self.started_at = time.time()
        # 页面上改过的策略盖在配置默认值之上，重启也不丢。
        self.policy_cache = {}
        for key in ("min_height", "min_free_gb"):
            saved = self.store.setting(key)
            if saved not in (None, ""):
                self.policy_cache[key] = saved
        self.disk_logged_at = 0.0
        # 运行闸门：容器起来先不动手，页面上点「开始转档」才扫才转；--once 不受管。
        self.running = bool(getattr(self.cfg, "autostart", False))
        self.gate = threading.Event()
        self.scan_now = threading.Event()
        if self.running:
            self.gate.set()
        self.detect_capabilities()

    # -- 开始 / 暂停 ------------------------------------------------------- #

    def set_running(self, wanted):
        """开闸会顺手催一次扫描，不用等下一个间隔。"""
        if self.running == bool(wanted):
            return self.running
        self.running = bool(wanted)
        if self.running:
            self.gate.set()
            self.scan_now.set()
            self.wake.set()
            log("开始转档。")
        else:
            self.gate.clear()
            log("已暂停：不再扫描也不再转档，队列留着。")
        return self.running

    # -- 策略：只转多大分辨率、盘还剩多少才继续 ------------------------ #

    def policy(self):
        raw = dict(min_height=self.cfg.min_height, min_free_gb=self.cfg.min_free_gb)
        raw.update(self.policy_cache)
        try:
            height = int(raw.get("min_height") or 0)
        except (TypeError, ValueError):
            height = 0
        try:
            free = float(raw.get("min_free_gb") or 0)
        except (TypeError, ValueError):
            free = 0.0
        return {"min_height": max(0, height), "min_free_gb": max(0.0, free)}

    def min_free_bytes(self):
        return int(self.policy()["min_free_gb"] * (1024 ** 3))

    def set_policy(self, min_height=None, min_free_gb=None):
        """页面改策略：存下来，并把之前按分辨率跳过的那批抹掉等着重扫。"""
        current = self.policy()
        if min_height is not None:
            try:
                value = max(0, int(float(min_height)))
            except (TypeError, ValueError):
                raise ValueError("min_height 得是个数字")
            self.policy_cache["min_height"] = str(value)
            self.store.set_setting("min_height", value)
        if min_free_gb is not None:
            try:
                value = max(0.0, float(min_free_gb))
            except (TypeError, ValueError):
                raise ValueError("min_free_gb 得是个数字")
            self.policy_cache["min_free_gb"] = str(value)
            self.store.set_setting("min_free_gb", value)
        cleared = 0
        if min_height is not None and int(min_height or 0) != current["min_height"]:
            cleared = self.store.forget_resolution_skips(int(min_height or 0))
        return self.policy(), cleared

    # -- 磁盘 ------------------------------------------------------------ #

    def disk(self):
        floor = self.min_free_bytes()
        out = []
        for root in self.cfg.roots:
            item = {"root": root, "mounted": os.path.isdir(root),
                    "writable": os.path.isdir(root) and self.writable(root),
                    "free": 0, "total": 0, "min_free": floor, "low": False}
            try:
                usage = shutil.disk_usage(root)
                item["free"], item["total"] = usage.free, usage.total
                item["low"] = bool(floor) and item["writable"] and item["free"] < floor
            except OSError:
                pass
            out.append(item)
        return out

    def disk_blocked(self):
        return any(item["low"] for item in self.disk())

    def disk_room_for(self, relative):
        """任务所在的盘还剩多少够不够。没设阈值就一律够。"""
        floor = self.min_free_bytes()
        if not floor:
            return True
        row = self.store.row(relative) or {}
        target = os.path.dirname(row.get("abs_path") or "")
        if not target or not os.path.isdir(target):
            return True
        try:
            return shutil.disk_usage(target).free >= floor
        except OSError:
            return True

    def log_disk_blocked(self):
        """这条提示一分钟最多刷一次，别把日志刷屏。"""
        now = time.time()
        if now - self.disk_logged_at < 60:
            return
        self.disk_logged_at = now
        low = [item for item in self.disk() if item["low"]]
        log("暂停转档：%s" % "，".join(
            "%s 只剩 %.1f GB（阈值 %.1f GB）" % (item["root"], item["free"] / 1073741824.0,
                                            self.policy()["min_free_gb"]) for item in low))

    # -- 能力探测 ---------------------------------------------------------- #

    def detect_capabilities(self):
        for tool in ("ffmpeg", "ffprobe"):
            if shutil.which(self.tool(tool)) is None:
                raise SystemExit("找不到 %s，这个服务离不开它。" % self.tool(tool))
        probe = self.run([self.tool("ffmpeg"), "-hide_banner", "-demuxers"])
        self.bluray = " bluray " in (" " + probe[1].replace("\n", " ") + " ")

    def tool(self, name):
        return getattr(self.cfg, name)

    def writable(self, directory):
        return os.access(directory, os.W_OK)

    def capabilities(self):
        roots = []
        for root in self.cfg.roots:
            roots.append({
                "root": root,
                "mounted": os.path.isdir(root),
                "writable": os.path.isdir(root) and self.writable(root),
            })
        return {
            "ok": True,
            "version": VERSION,
            "ffmpeg": self.tool("ffmpeg"),
            "ffprobe": self.tool("ffprobe"),
            "bluray": self.bluray,
            "roots": roots,
            "languages": self.cfg.langs,
            "counts": self.store.summary(),
            "policy": self.policy(),
            "state": "running" if self.running else "paused",
            "scanning": self.scanning,
            "disk": self.disk(),
            "disk_blocked": self.disk_blocked(),
            "stats": dict(self.stats, uptime=round(time.time() - self.started_at)),
        }

    # -- 相对路径 ---------------------------------------------------------- #

    def to_relative(self, abs_path):
        for root in self.cfg.roots:
            normalized = os.path.abspath(root)
            target = os.path.abspath(abs_path)
            if target.startswith(normalized.rstrip("/") + os.sep):
                return os.path.relpath(target, normalized).replace(os.sep, "/")
        return None

    def to_absolute(self, path):
        """接受绝对路径，也接受相对某个根的写法；跳出媒体库就返回 None。"""
        candidate = path.lstrip("/") if os.path.isabs(path) else path
        if os.path.isabs(path):
            targets = [os.path.abspath(path)]
        else:
            targets = [os.path.join(os.path.abspath(root), candidate)
                       for root in self.cfg.roots]
            targets.append(os.path.abspath(candidate))
        for target in targets:
            for root in self.cfg.roots:
                normalized = os.path.abspath(root)
                if target.startswith(normalized.rstrip("/") + os.sep):
                    return target
        return None

    # -- 扫描 -------------------------------------------------------------- #

    def walk(self):
        """走一遍媒体库，把该转的排进队列。已有的 sidecar 一眼跳过，不碰文件内容。"""
        self.stats["scans"] += 1
        self.scanning = True
        enqueued = 0
        for root in self.cfg.roots:
            if not os.path.isdir(root):
                log("跳过没有挂载的媒体库：%s" % root)
                continue
            for directory, subdirs, names in os.walk(root):
                subdirs[:] = [name for name in subdirs
                              if not name.startswith(".") and not name.startswith("_")
                              and name.lower() not in SKIPPED_DIRS]
                lower = {name.lower() for name in names}
                for name in names:
                    if not name.lower().endswith(tuple(WALK_SUFFIXES)):
                        continue
                    abs_path = os.path.join(directory, name)
                    if self.consider(abs_path, lower):
                        enqueued += 1
        self.scanning = False
        log("扫描完成：这一轮排进队列 %d 条。" % enqueued)
        return enqueued

    def consider(self, abs_path, sibling_names=None):
        """决定一个视频要不要转，并把结论写进状态表。返回是否新排了任务。"""
        relative = self.to_relative(abs_path)
        if relative is None:
            return False
        try:
            stat = os.stat(abs_path)
        except OSError:
            self.store.upsert(path=relative, abs_path=abs_path, status="failed",
                              detail="文件读不到了。")
            return False
        known = self.store.row(relative)
        if (known and known["status"] in TERMINAL_STATUSES
                and known["video_size"] == stat.st_size
                and known["video_mtime"] == int(stat.st_mtime)):
            return False
        if sibling_names is None:
            sibling_names = {name.lower() for name in os.listdir(os.path.dirname(abs_path))}
        if self.sidecar_already_there(abs_path, sibling_names):
            self.store.upsert(path=relative, abs_path=abs_path, status="skipped",
                              detail="已经有可用的外挂字幕了。",
                              video_size=stat.st_size, video_mtime=int(stat.st_mtime))
            return False
        if os.path.splitext(abs_path)[1].lower() == ".iso" and not self.bluray:
            self.store.upsert(path=relative, abs_path=abs_path, status="unsupported",
                              detail="ISO 需要 ffmpeg 带 libbluray 才读得了，这个镜像没有。",
                              video_size=stat.st_size, video_mtime=int(stat.st_mtime))
            return False
        plan = self.plan(abs_path)
        if plan is None:
            self.store.upsert(path=relative, abs_path=abs_path, status="failed",
                              detail="ffprobe 读不了这个文件。",
                              video_size=stat.st_size, video_mtime=int(stat.st_mtime))
            return False
        if not plan["tracks"]:
            self.store.upsert(path=relative, abs_path=abs_path, status="unsupported",
                              detail=plan["reason"],
                              video_size=stat.st_size, video_mtime=int(stat.st_mtime))
            return False
        height = int(plan.get("height") or 0)
        width = int(plan.get("width") or 0)
        floor = self.policy()["min_height"]
        if floor and height and height < floor:
            self.store.upsert(path=relative, abs_path=abs_path, status="skipped",
                              detail="只有 %dp，兼容模式够用，按策略不转。" % height,
                              video_size=stat.st_size, video_mtime=int(stat.st_mtime),
                              width=width, height=height)
            return False
        track = plan["tracks"][0]
        self.store.upsert(path=relative, abs_path=abs_path,
                          out_path=sidecar_path(abs_path, track["language"], track["format"]),
                          video_size=stat.st_size, video_mtime=int(stat.st_mtime),
                          track_index=track["index"], codec=track["codec"],
                          language=track["language"], format=track["format"],
                          status="pending", detail="", covered=0.0, out_size=0,
                          width=width, height=height)
        return True

    def sidecar_already_there(self, abs_path, sibling_names):
        """同目录下已经躺着 ``片名.语言.srt`` 这类文件？那就不用再转了。

        扫描时用目录里现成的文件名列表判断，一次目录列举就能判完整个目录。
        """
        stem = os.path.splitext(os.path.basename(abs_path))[0].lower() + "."
        for name in sibling_names:
            if not name.startswith(stem):
                continue
            suffix = name[len(stem):]
            parts = suffix.split(".")
            if len(parts) == 2 and parts[1] in ("srt", "ass", "sup") and parts[0]:
                return True
        return False

    # -- 探测 -------------------------------------------------------------- #

    def plan(self, abs_path):
        """问 ffprobe 这个文件里有哪些能转的字幕轨，按语言偏好排好序。"""
        code, out, err = self.run([self.tool("ffprobe"), "-v", "error",
                                   "-print_format", "json", "-show_streams", abs_path])
        if code != 0:
            return {"tracks": [], "reason": "ffprobe 读不了：" + tail(err)}
        try:
            streams = json.loads(out or "{}").get("streams", [])
        except ValueError:
            return {"tracks": [], "reason": "ffprobe 的输出看不懂。"}
        subtitle_index = 0
        candidates = []
        height = width = 0
        for stream in streams:
            if stream.get("codec_type") == "video":
                try:
                    height = max(height, int(stream.get("height") or 0))
                    width = max(width, int(stream.get("width") or 0))
                except (TypeError, ValueError):
                    pass
                continue
            if stream.get("codec_type") != "subtitle":
                continue
            codec = (stream.get("codec_name") or "").lower()
            ordinal = subtitle_index
            subtitle_index += 1
            tags = stream.get("tags") or {}
            language = normalize_language(tags.get("language"))
            flags = stream.get("disposition") or {}
            if codec in TEXT_CODECS:
                fmt = TEXT_CODECS[codec]
            elif codec in PGS_CODECS:
                fmt = "sup"
            else:
                continue
            candidates.append({
                "index": ordinal, "codec": codec, "language": language, "format": fmt,
                "name": tags.get("title") or "", "default": bool(flags.get("default")),
            })
        if not candidates:
            reason = "没有能转的字幕轨。"
            if any((stream.get("codec_type") == "subtitle") for stream in streams):
                reason = "内封字幕是位图（DVB/DVD 字幕）或容器不支持的格式，这一条先跳过。"
            return {"tracks": [], "reason": reason, "width": width, "height": height}
        wanted = [want for want in self.cfg.langs if want in {item["language"] for item in candidates}]
        if wanted:
            picked = [item for item in candidates if item["language"] in wanted]
        elif self.cfg.fallback == "first":
            default_track = [item for item in candidates if item["default"]]
            picked = default_track or candidates[:1]
        else:
            return {"tracks": [], "reason": "字幕语言是 %s，不在偏好语言 %s 里。"
                    % ("/".join(item["language"] for item in candidates), "/".join(self.cfg.langs))}
        # 同一条语言只留第一条：国/粤都归一成 zh，输出路径会撞车。
        unique = []
        seen = set()
        for item in picked:
            key = (item["language"], item["format"])
            if key in seen:
                continue
            seen.add(key)
            unique.append(item)
        return {"tracks": unique, "reason": "", "width": width, "height": height}

    # -- 执行 -------------------------------------------------------------- #

    def push(self, paths):
        """TV 端点名要转的文件：立刻入库、优先执行。"""
        accepted, rejected = [], []
        for raw in paths:
            abs_path = self.to_absolute(raw)
            if abs_path is None or not os.path.isfile(abs_path):
                rejected.append(raw)
                continue
            relative = self.to_relative(abs_path)
            self.store.forget(relative)
            if self.consider(abs_path):
                accepted.append(relative)
            else:
                rejected.append(relative)
        with self.priority_lock:
            self.priority.extend(accepted)
        if accepted:
            self.wake.set()
        return accepted, rejected

    def run_job(self, relative):
        row = self.store.row(relative)
        if row is None or not row.get("abs_path"):
            return False
        abs_path = row["abs_path"]
        out_path = row["out_path"] or sidecar_path(abs_path, row["language"], row["format"])
        directory = os.path.dirname(abs_path)
        if not os.path.isfile(abs_path):
            self.store.patch(relative, status="failed", detail="文件不见了。")
            return False
        if not self.writable(directory):
            self.store.patch(relative, status="readonly", out_path=out_path,
                             detail="媒体源是只读挂载，写不进去。")
            self.stats["failed"] += 1
            return False
        tmp = os.path.join(directory,
                           "." + os.path.basename(out_path) + ".part")
        cmd = [self.tool("ffmpeg"), "-hide_banner", "-loglevel", "error", "-nostdin",
               "-y", "-progress", "pipe:1", "-i", abs_path,
               "-map", "0:s:%d" % int(row["track_index"] or 0)]
        if row["format"] == "sup":
            cmd += ["-c:s", "copy", "-f", "sup", tmp]
        else:
            cmd += ["-c:s", row["format"], "-f", row["format"], tmp]
        self.store.patch(relative, status="running", detail="", covered=0.0, out_size=0)
        started = time.time()
        try:
            outcome = self.spawn(cmd, tmp, relative)
        except OSError as error:
            outcome = (False, "起不了 ffmpeg：%s" % error)
        if not outcome[0]:
            remove_quiet(tmp)
            self.store.patch(relative, status="failed", out_path=out_path,
                             detail=tail(outcome[1]),
                             attempts=int(row.get("attempts") or 0) + 1)
            self.stats["failed"] += 1
            return False
        try:
            os.replace(tmp, out_path)
        except OSError as error:
            remove_quiet(tmp)
            self.store.patch(relative, status="failed", detail="写不进去：%s" % error)
            self.stats["failed"] += 1
            return False
        size = os.path.getsize(out_path)
        if size <= 0:
            self.store.patch(relative, status="unsupported", out_path=out_path,
                             detail="抽出来是空的，这条字幕大概是空的。", out_size=0)
            return False
        self.store.patch(relative, status="done", out_path=out_path, out_size=size,
                         detail="", covered=max(0.0, self.last_covered))
        self.stats["converted"] += 1
        log("%s -> %s（%.1f MB，%.1f 秒）"
            % (relative, os.path.basename(out_path), size / 1048576.0,
               time.time() - started))
        return True

    last_covered = 0.0

    def spawn(self, cmd, tmp, relative):
        """跑 ffmpeg，顺手把进度和「产物太大」两件事盯着。"""
        process = subprocess.Popen(cmd, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True,
                                   preexec_fn=drop_priority(self.cfg.nice))
        notes = []
        covered = 0.0
        last_report = 0.0
        assert process.stdout is not None
        for line in process.stdout:
            line = line.strip()
            key, sep, value = line.partition("=")
            if sep and key in PROGRESS_KEYS:
                if key == "out_time_us":
                    try:
                        covered = max(0.0, int(value) / 1000000.0)
                    except ValueError:
                        pass
            elif line:
                notes.append(line)
            now = time.time()
            if now - last_report <= 2.0:
                continue
            last_report = now
            self.last_covered = covered
            try:
                size = os.path.getsize(tmp)
            except OSError:
                size = 0
            self.store.patch(relative, covered=covered, out_size=size)
            if size > self.cfg.max_out_bytes:
                process.kill()
                process.wait()
                return (False, "转出来的字幕超过 %d MB，这条先不用它。"
                        % (self.cfg.max_out_bytes >> 20))
        code = process.wait()
        self.last_covered = covered
        if code != 0:
            return (False, " ".join(notes[-3:]) or ("ffmpeg 退出码 %d" % code))
        return (True, "")

    def run(self, cmd):
        try:
            process = subprocess.run(cmd, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True)
        except OSError as error:
            return 127, "", str(error)
        return process.returncode, process.stdout, process.stderr

    # -- 后台线程 ---------------------------------------------------------- #

    def pick(self):
        """挑一条能开工的：所在媒体库盘已经见底的那条先留在队列里。"""
        for relative in self.store.pending(8):
            if self.disk_room_for(relative):
                return relative
            self.log_disk_blocked()
        return None

    def worker(self):
        while True:
            if not self.gate.is_set():
                self.wake.wait(1.0)
                self.wake.clear()
                continue
            with self.priority_lock:
                next_path = self.priority.pop(0) if self.priority else None
            if next_path is not None and not self.disk_room_for(next_path):
                with self.priority_lock:
                    self.priority.append(next_path)
                next_path = None
                self.log_disk_blocked()
            if next_path is None:
                next_path = self.pick()
            if next_path is None:
                self.wake.wait(10.0 if self.disk_blocked() else 2.0)
                self.wake.clear()
                continue
            try:
                self.run_job(next_path)
            except Exception as error:  # 单条任务再怎么也不能把服务带崩
                log("任务出错了：%s" % error)
            if self.cfg.sleep:
                time.sleep(self.cfg.sleep)

    def ticker(self):
        """开始后就扫一遍，之后按间隔重扫。

        暂停时是阻塞等唤醒（不是空转），也不去清 `scan_now`——之前每轮都清一次，
        「开始转档」刚点下去的唤醒标记会被自己抹掉，结果白等一整个间隔。
        `--interval 0` 就是只在开始和手动扫描时走。
        """
        interval = max(30, self.cfg.interval)
        while True:
            if not self.running:
                self.scan_now.wait(2)
                continue
            if not (self.cfg.idle_only and self.store.pending(1)):
                try:
                    self.walk()
                except Exception as error:
                    log("扫描出错了：%s" % error)
            self.scan_now.clear()
            # 等到下一轮；期间「开始」或页面点「扫描媒体库」都会立刻把它叫醒
            self.scan_now.wait(interval if self.cfg.interval > 0 else None)


def drop_priority(niceness):
    if niceness <= 0:
        return None

    def action():
        try:
            os.nice(niceness)
        except OSError:
            pass
    return action


def remove_quiet(path):
    try:
        os.unlink(path)
    except OSError:
        pass


def tail(text, limit=200):
    text = (text or "").strip().replace("\n", " ")
    return text[-limit:]


# --------------------------------------------------------------------------- #
# HTTP 接口
# --------------------------------------------------------------------------- #

class Handler(BaseHTTPRequestHandler):
    service = None  # 由 main 注入
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        if self.service.cfg.verbose:
            log("%s %s" % (self.address_string(), fmt % args))

    # -- 工具 -------------------------------------------------------------- #

    def authorized(self, query):
        token = self.service.cfg.token
        if not token:
            return True
        given = self.headers.get("Authorization") or ""
        if given.startswith("Bearer ") and given[7:] == token:
            return True
        return query.get("token", [""])[0] == token

    def reply(self, code, payload, kind="application/json"):
        if kind == "application/json":
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        else:
            body = payload.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", kind + "; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def read_body(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            return {}
        raw = self.rfile.read(length)
        try:
            return json.loads(raw.decode("utf-8"))
        except ValueError:
            return {}

    # -- 路由 -------------------------------------------------------------- #

    def do_GET(self):
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        service = self.service
        if not self.authorized(query):
            self.reply(401, {"error": "鉴权失败：缺 token。"})
            return
        route = parsed.path.rstrip("/") or "/"
        if route in ("", "/", "/index.html"):
            kind, body = index_page()
            self.reply(200, body, kind=kind)
        elif route in ("/health", "/api/health"):
            self.reply(200, service.capabilities())
        elif route in ("/jobs", "/api/jobs"):
            group = query.get("status", ["all"])[0]
            if group not in STATUS_GROUPS:
                group = "all"
            res = query.get("res", [""])[0]
            if res not in RES_GROUPS:
                res = ""
            per_page = clamp(query.get("per_page", [query.get("limit", ["50"])[0]])[0], 5, 200)
            number = clamp(query.get("page", ["1"])[0], 1, 100000)
            needle = query.get("q", [""])[0].strip()
            rows, total = service.store.page(STATUS_GROUPS[group], needle, res,
                                             (number - 1) * per_page, per_page)
            self.reply(200, {"counts": service.store.summary(), "total": total,
                             "page": number, "per_page": per_page,
                             "pages": max(1, (total + per_page - 1) // per_page),
                             "jobs": rows})
        elif route in ("/status", "/api/status"):
            wanted = query.get("file", [""])[0]
            abs_path = service.to_absolute(wanted) if wanted else None
            if abs_path is None:
                self.reply(404, {"error": "找不到这个文件。"})
                return
            relative = service.to_relative(abs_path)
            row = service.store.row(relative)
            self.reply(200, {"file": relative, "job": row,
                             "plan": service.plan(abs_path) if os.path.isfile(abs_path) else None})
        else:
            self.reply(404, {"error": "没有这个接口。"})

    def do_POST(self):
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        service = self.service
        if not self.authorized(query):
            self.reply(401, {"error": "鉴权失败：缺 token。"})
            return
        route = parsed.path.rstrip("/") or "/"
        if route in ("/jobs", "/api/jobs"):
            body = self.read_body()
            files = body.get("files") or body.get("paths") or []
            if isinstance(files, str):
                files = [files]
            accepted, rejected = service.push([str(item) for item in files])
            self.reply(200, {"accepted": accepted, "rejected": rejected})
        elif route in ("/scan", "/api/scan"):
            enqueued = service.walk()
            service.wake.set()
            self.reply(200, {"enqueued": enqueued,
                             "state": "running" if service.running else "paused"})
        elif route in ("/run", "/api/run"):
            action = str(self.read_body().get("action", "")).strip().lower()
            if action in ("start", "resume", "continue"):
                service.set_running(True)
            elif action in ("pause", "stop"):
                service.set_running(False)
            else:
                self.reply(400, {"error": "action 只认 start / resume / pause。"})
                return
            self.reply(200, {"state": "running" if service.running else "paused",
                             "counts": service.store.summary()})
        elif route in ("/policy", "/api/policy"):
            body = self.read_body()
            try:
                policy, cleared = service.set_policy(body.get("min_height"),
                                                     body.get("min_free_gb"))
            except ValueError as error:
                self.reply(400, {"error": str(error)})
                return
            self.reply(200, {"policy": policy, "cleared": cleared})
        else:
            self.reply(404, {"error": "没有这个接口。"})


def clamp(raw, low, high):
    try:
        value = int(raw)
    except ValueError:
        value = low
    return max(low, min(high, value))


# --------------------------------------------------------------------------- #
# 入口
# --------------------------------------------------------------------------- #

def parse_args(argv=None):
    parser = argparse.ArgumentParser(
        description="OmniPlay 字幕转档：把内封字幕抽成 sidecar 写回原目录。")
    parser.add_argument("--roots", default=os.environ.get("SIDECAR_ROOTS", "/media"),
                        help="媒体库根目录，多个用逗号或分号分隔（默认 /media）")
    parser.add_argument("--db", default=os.environ.get("SIDECAR_DB", "/var/lib/sidecar/state.db"),
                        help="状态表文件（默认 /var/lib/sidecar/state.db）")
    parser.add_argument("--langs", default=os.environ.get("SIDECAR_LANGS", "zh"),
                        help="希望转的字幕语言，按优先级排序，逗号分隔（默认 zh）")
    parser.add_argument("--fallback", default=os.environ.get("SIDECAR_FALLBACK", "first"),
                        choices=["first", "none"],
                        help="没命中偏好语言时怎么办：first=转默认轨/第一条，none=不转")
    parser.add_argument("--port", type=int, default=int(os.environ.get("SIDECAR_PORT", "45733")))
    parser.add_argument("--host", default=os.environ.get("SIDECAR_HOST", "0.0.0.0"))
    parser.add_argument("--token", default=os.environ.get("SIDECAR_TOKEN", ""),
                        help="设了之后所有接口都要带 Authorization: Bearer")
    parser.add_argument("--interval", type=int, default=int(os.environ.get("SIDECAR_INTERVAL", "900")),
                        help="空闲扫库间隔秒数，0 表示只被动等 TV 点名")
    parser.add_argument("--sleep", type=int, default=int(os.environ.get("SIDECAR_SLEEP", "2")),
                        help="两个任务之间的间隙，别把 NAS 撑满")
    parser.add_argument("--nice", type=int, default=int(os.environ.get("SIDECAR_NICE", "10")))
    parser.add_argument("--max-out-mb", type=int, default=int(os.environ.get("SIDECAR_MAX_OUT_MB", "512")))
    parser.add_argument("--min-height", type=int, default=int(os.environ.get("SIDECAR_MIN_HEIGHT", "0")),
                        help="低于这个高度（像素）的视频不转，0 = 全都转")
    parser.add_argument("--min-free-gb", type=float,
                        default=float(os.environ.get("SIDECAR_MIN_FREE_GB", "500")),
                        help="媒体库所在盘剩余低于这个容量就暂停转档，0 = 不限制（默认 500）")
    parser.add_argument("--autostart", action="store_true",
                        default=os.environ.get("SIDECAR_AUTOSTART", "") == "1",
                        help="容器一起来就开始扫描与转档，默认是先暂停等页面点开始")
    parser.add_argument("--idle-only", action="store_true",
                        default=os.environ.get("SIDECAR_IDLE_ONLY", "") == "1",
                        help="队列还没排空就不扫库")
    parser.add_argument("--verbose", action="store_true",
                        default=os.environ.get("SIDECAR_VERBOSE", "") == "1")
    parser.add_argument("--once", action="store_true",
                        help="扫一遍、把队列跑干净就退出，适合定时任务")
    parser.add_argument("--no-serve", action="store_true", help="不开 HTTP，只在后台跑队列")
    parser.add_argument("--ffmpeg", default=os.environ.get("SIDECAR_FFMPEG", "ffmpeg"))
    parser.add_argument("--ffprobe", default=os.environ.get("SIDECAR_FFPROBE", "ffprobe"))
    args = parser.parse_args(argv)
    args.roots = [os.path.abspath(item) for item in split_list(args.roots)]
    args.langs = [normalize_language(item) for item in split_list(args.langs)] or ["zh"]
    args.max_out_bytes = args.max_out_mb << 20
    return args


def drain(service):
    """--once 用的：把队列里的东西全部跑完。盘见底就停在这儿，等下次再跑。"""
    while True:
        with service.priority_lock:
            next_path = service.priority.pop(0) if service.priority else None
        if next_path is None:
            next_path = service.pick()
        if next_path is None:
            break
        service.run_job(next_path)


def main(argv=None):
    config = parse_args(argv)
    service = Service(config)
    info = service.capabilities()
    log("字幕转档 v%s 启动：媒体库 %s，语言偏好 %s，已完成 %s 条。"
        % (VERSION, ", ".join(item["root"] + ("可写" if item["writable"] else "只读")
                              for item in info["roots"]),
           "/".join(config.langs), info["counts"].get("done", 0)))
    if not any(item["writable"] for item in info["roots"]):
        log("提示：没有一个媒体库可写，转出来的字幕没地方写。")
    if info["state"] == "paused":
        log("现在是暂停状态：等页面上点「开始转档」，或者起服务时带 --autostart。")

    if config.once:
        service.walk()
        drain(service)
        log("跑完了：" + json.dumps(service.store.summary(), ensure_ascii=False))
        return 0

    threading.Thread(target=service.worker, daemon=True).start()
    # 扫库线程一直起：第一遍由它在闸门打开后跑，`--interval 0` 也照样有第一遍
    threading.Thread(target=service.ticker, daemon=True).start()
    if config.interval > 0:
        log("每 %d 秒自己扫一遍库。" % config.interval)

    if config.no_serve:
        while True:
            time.sleep(3600)
    server = ThreadingHTTPServer((config.host, config.port), Handler)
    Handler.service = service
    log("HTTP 在 %s:%d。" % (config.host, config.port))
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
