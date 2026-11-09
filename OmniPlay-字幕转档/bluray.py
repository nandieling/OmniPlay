#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""蓝光盘的发现与取流：BDMV 目录、蓝光 ISO。

安卓 TV 端把一张盘看成 ``BDMV/STREAM/*.m2ts`` 里的分卷，按体积挑主片；NAS 这边要做的
是同一件事：先认出「这个目录是一张盘」，再从播放列表里挑出正片，最后把正片的内封字幕
抽成贴着视频的 ``片名.语言.sup``，让电视播放时能直接读到。

只读蓝光自己的结构，不依赖 libbluray：

* ``BDMV/PLAYLISTS/*.mpls``（也有rip 把它改名成 ``PLAYLIST``）——播放列表，给出时长、
  由哪些 clip 拼成、以及每条字幕轨的语言；
* ``BDMV/CLIPINF/*.clpi`` 与 ``BDMV/STREAM/*.m2ts``——分卷本体。

ISO 这边：libbluray 在就用 ffmpeg 的 ``bluray://``；不在就退到外部工具（isoinfo /
xorriso / 7z）把需要的分卷抽到临时目录；两样都没有就给出一句说得清的提示。
"""

import os
import re
import shutil
import struct

BDMV_DIRS = ("BDMV",)
PLAYLIST_DIRS = ("PLAYLISTS", "PLAYLIST")
CERT_DIRS = ("CERTIFICATE",)
# 扫描时不该再往这些目录里钻：里面全是索引和分卷，逐文件走一遍纯属浪费。
DISC_INNER_DIRS = {"bdmv", "certificate", "auxdata", "ssif", "jp", "sl", "3dl", "3dg", "meta"}
ISO_SUFFIXES = (".iso", ".bin")
MPLS_SUFFIX = ".mpls"
# STN 里出现的流类型（HDMV 私有码）：0x90 PGS、0x91 交互图形、0x92 二级字幕。
PGS_TYPES = {0x90: "pgs", 0x91: "igs", 0x92: "textst"}
STN_LANG = re.compile(rb"[\x90\x91\x92](?=[a-z]{3})")
TIMESCALE = 90000.0
# 播放列表短于这个时长的多半是花絮、宣传片、菜单，不当正片。
MIN_FEATURE_SECONDS = 240.0


def _u16(buf, at):
    return int.from_bytes(buf[at:at + 2], "big")


def _be16(value):
    """两个大端字节。"""
    return bytes([(value >> 8) & 0xFF, value & 0xFF])


def _u32(buf, at):
    return int.from_bytes(buf[at:at + 4], "big")


def _looks_like_language(raw):
    """蓝光里语言标是三个小写字母；rip 常塞 ``***`` 或乱码，认不出来就别用。"""
    if not raw or len(raw) != 3:
        return ""
    text = raw.decode("latin-1").lower()
    return text if re.match(r"^[a-z]{3}$", text) else ""


def _tlvs(buf, at):
    """走 TLV 结构（标签 2 字节 + 长度 4 字节 + 载荷），产出 (标签, 载荷)。"""
    while at + 6 <= len(buf):
        tag = _u16(buf, at)
        size = _u32(buf, at + 2)
        if tag == 0 or size <= 0 or at + 6 + size > len(buf):
            break
        yield tag, buf[at + 6:at + 6 + size]
        at += 6 + size


def _child(directory, *candidates):
    """按大小写不敏感找子目录：rip 出来的盘目录名千奇百怪。"""
    try:
        names = os.listdir(directory)
    except OSError:
        return None
    wanted = {name.lower(): name for name in names}
    for candidate in candidates:
        hit = wanted.get(candidate.lower())
        if hit:
            return os.path.join(directory, hit)
    return None


def disc_at(path):
    """``path`` 自己是一张蓝光盘吗？是就返回盘的描述，不是返回 None。

    认「目录里有没有 BDMV/ 加 index.bdmv」，不认「路径里穿过 BDMV」——后者会把
    BDMV 里面的每个分卷都当成一张盘。
    """
    if not path:
        return None
    if os.path.isfile(path) and os.path.splitext(path)[1].lower() in ISO_SUFFIXES:
        return {"path": path, "kind": "iso", "name": os.path.splitext(os.path.basename(path))[0]}
    if not os.path.isdir(path):
        return None
    bdmv = _child(path, *BDMV_DIRS)
    if not bdmv:
        return None
    has_index = bool(_child(bdmv, "index.bdmv", "INDEX.BDM"))
    has_stream = bool(_child(bdmv, "STREAM", "stream"))
    if not (has_index or has_stream):
        return None
    return {"path": path, "kind": "folder", "name": os.path.basename(path.rstrip(os.sep)),
            "bdmv": bdmv}


def is_disc_directory(directory):
    return disc_at(directory) is not None


def skip_subdirectories(subdirs):
    """os.walk 用的剪枝：盘内结构与系统垃圾目录都不再往下走。"""
    keep = []
    for name in subdirs:
        if name.startswith(".") or name.startswith("_"):
            continue
        if name.lower() in DISC_INNER_DIRS:
            continue
        keep.append(name)
    return keep


def playlists(disc):
    """解析播放列表，按时长从长到短返回。

    每项：``{id, file, seconds, clips, subtitles}``；``subtitles`` 里是
    ``{kind, lang}``，kind 目前只有 pgs 有意义（其它位图格式转不了）。
    """
    if disc["kind"] != "folder":
        return []
    bdmv = disc["bdmv"]
    directory = _child(bdmv, *PLAYLIST_DIRS)
    if not directory:
        return []
    out = []
    try:
        names = sorted(os.listdir(directory))
    except OSError:
        return []
    for name in names:
        if not name.lower().endswith(MPLS_SUFFIX):
            continue
        parsed = parse_mpls_file(os.path.join(directory, name))
        if parsed:
            parsed["file"] = os.path.join(directory, name)
            parsed["id"] = name_from_playlist_path(name)
            out.append(parsed)
    out.sort(key=lambda item: -item["seconds"])
    return out


def parse_mpls_file(path):
    try:
        with open(path, "rb") as handle:
            data = handle.read(1 << 20)
    except OSError:
        return None
    return parse_mpls(data)


CLIP_NAME = re.compile(rb"(\d{5})M2TS")


def parse_mpls(data):
    """解析一个 .mpls 的内容；读不懂返回 None，交给上一层跳过。

    标准 .mpls 里有 ``PLAY`` 与 ``STN`` 两个标记，按 TLV 走最准。但 rip 出来的盘里
    见过整块标记被抹掉的（Ao Ashi 那批就是），所以两件事分开做、都能兜底：

    * 分卷清单：TLV 里的 PlayItem 优先，退化成直接扫 ``NNNNNM2TS`` 这个 ClipIDInfo；
    * 字幕轨：TLV 里的 STN 优先，读不出来就留空，交给上层用 ffprobe 探分卷——
      ffprobe 报的语言码才是播放时真正能对上的那份。
    """
    if not data or not data.startswith(b"MPLS"):
        return None
    clips = []
    seconds = 0.0
    play_at = data.find(b"PLAY")
    if play_at >= 0:
        # PlayItem 的 in/out 时间是 90kHz 计数，累加起来就是这条播放列表的时长
        for tag, body in _tlvs(data, play_at + 4):
            if tag == 0x1404 and len(body) >= 16:
                clip = _u16(body, 0)
                in_time = _u32(body, 8)
                out_time = _u32(body, 12)
                if out_time >= in_time:
                    seconds += (out_time - in_time + 1) / TIMESCALE
                if clip not in clips:
                    clips.append(clip)
    if not clips:
        for match in CLIP_NAME.finditer(data):
            clip = int(match.group(1))
            if clip not in clips:
                clips.append(clip)
    subtitles = []
    stn_at = data.find(b"STN")
    if stn_at >= 0:
        for tag, body in _tlvs(data, stn_at + 4):
            if tag == 0x1410 and len(body) >= 9:        # StreamEntry
                kind = PGS_TYPES.get(body[2])
                lang = _looks_like_language(body[4:7]) or _looks_like_language(body[6:9])
                if kind:
                    subtitles.append({"kind": kind, "lang": lang or "und",
                                      "pids": [_u16(body, 3)]})
            elif tag == 0x1411 and len(body) >= 9:      # StreamEntryExtension
                kind = PGS_TYPES.get(body[4])
                lang = _looks_like_language(body[6:9])
                if kind:
                    subtitles.append({"kind": kind, "lang": lang or "und",
                                      "pids": [_u16(body, 2)]})
    if not subtitles:
        subtitles = scan_stn_entries(data)
    if not clips:
        return None
    return {"id": None, "seconds": seconds, "clips": clips, "subtitles": subtitles}


def scan_stn_entries(data):
    """rip 抹掉 ``STN`` 标记时的兜底：直接扫「字幕流类型 + 三位语言码」。

    真盘样本（5082 个 .mpls 全都没有 PLAY/STN 标记）里的排法是
    ``00 00 05 90 65 6e 67 00 09 01 12 a1``——语言码紧跟流类型，PID 在语言码后面
    3~7 字节之间。这里只给候选 PID，到底哪个算数交给 PMT 求交集来定。
    """
    found = []
    seen = set()
    for match in STN_LANG.finditer(data):
        at = match.start()
        kind = PGS_TYPES.get(data[at])
        lang = _looks_like_language(data[at + 1:at + 4])
        if not kind or not lang:
            continue
        tail = data[at + 4:at + 12]
        pids = []
        for off in range(0, len(tail) - 1):
            pid = _u16(tail, off)
            if 0x1000 <= pid <= 0x1FFF and pid not in pids:
                pids.append(pid)
        key = (kind, lang, tuple(pids))
        if key in seen:
            continue
        seen.add(key)
        found.append({"kind": kind, "lang": lang, "pids": pids})
    return found


def choose_subtitle(playlist, pmt_entries, wanted_languages, normalize=None):
    """给这张盘挑一条位图字幕：MPLS 声明的语言 ∩ PMT 实际有的 PID，再按偏好语言排。

    两边各有一半信息：MPLS 有语言但 rip 后 PID 位置不稳，PMT 有准确 PID 但没有语言。
    返回 ``{"pid": int 或 None, "lang": str, "note": str, "index": int 或 None}``。
    ``index`` 是这条字幕在分卷里的位次，抽别的分卷时按同一位次找对应的 PID。
    """
    pgs_pids = [pid for pid, kind in pmt_entries if kind == "pgs"]
    if not pgs_pids:
        return {"pid": None, "lang": "und", "note": "分卷里没有位图字幕。", "index": None}
    named = {}
    for item in (playlist or {}).get("subtitles") or []:
        if item.get("kind") != "pgs":
            continue
        for pid in item.get("pids") or []:
            if pid in pgs_pids and pid not in named:
                named[pid] = item["lang"]
    normalizer = normalize or normalize_tag
    wanted = set(wanted_languages or [])
    preferred = [pid for pid in pgs_pids
                 if normalizer(named.get(pid, "und")) in wanted]
    pick = (preferred or pgs_pids)[0]
    note = ""
    if len(pgs_pids) > 1:
        note = "盘里有 %d 条位图字幕，取了第 %d 条" % (len(pgs_pids), pgs_pids.index(pick) + 1)
    return {"pid": pick, "lang": named.get(pick) or "und", "note": note,
            "index": pgs_pids.index(pick)}


def name_from_playlist_path(path):
    """``00003.mpls`` -> ``00003``，给任务当身份用。"""
    return os.path.splitext(os.path.basename(path))[0]


def stream_files(disc, clips):
    """播放列表用到的分卷绝对路径，按播放顺序。"""
    if disc["kind"] != "folder":
        return []
    stream_dir = _child(disc["bdmv"], "STREAM", "stream")
    if not stream_dir:
        return []
    out = []
    for clip in clips:
        for suffix in (".m2ts", ".M2TS", ".mts", ".m2t"):
            candidate = os.path.join(stream_dir, "%05d%s" % (clip, suffix))
            if os.path.isfile(candidate):
                out.append(candidate)
                break
        else:
            # 少数 rip 的分卷名和 clip 号对不上，退而求其次按序号找同名文件
            fallback = os.path.join(stream_dir, "%05d.m2ts" % clip)
            if os.path.isfile(fallback):
                out.append(fallback)
    return out


def normalize_tag(raw):
    """够用的语言码归一：``zho``/``chi`` -> ``zh``。sidecar 那边有更全的表，可以传进来。"""
    base = (raw or "").strip().lower().replace("_", "-").split("-")[0]
    if len(base) == 3:
        return {"zho": "zh", "chi": "zh", "cmn": "zh", "eng": "en", "jpn": "ja",
                "kor": "ko", "fra": "fr", "fre": "fr", "deu": "de", "ger": "de"}.get(base, base)
    return base or "und"


def pick_main(disc, wanted_languages, normalize=None):
    """挑正片：先看有没有带偏好语言字幕的长播放列表，再退到最长的那个。

    返回 ``(playlist, segments)``；认不出来返回 ``(None, [])``。
    """
    lists = playlists(disc)
    if not lists:
        # 没有可用的播放列表（rip 把 PLAYLISTS 删了很常见）：直接按体积挑分卷。
        stream_dir = _child(disc.get("bdmv") or "", "STREAM", "stream") if disc else None
        if not stream_dir:
            return None, []
        try:
            names = [name for name in os.listdir(stream_dir) if name.lower().endswith(".m2ts")]
        except OSError:
            return None, []
        names.sort(key=lambda name: -os.path.getsize(os.path.join(stream_dir, name)))
        biggest = names[:1]
        return ({"id": None, "file": None, "seconds": 0.0, "clips": [], "subtitles": []},
                [os.path.join(stream_dir, name) for name in biggest])
    wanted = set(wanted_languages or [])
    # rip 抹掉 TLV 的盘时长全是 0，这时改用「分卷合计体积」当排名依据
    def size_of(item):
        return sum(os.path.getsize(path) for path in stream_files(disc, item["clips"]))

    def rank(item):
        return (item["seconds"] or 0.0, size_of(item))

    ordered = sorted(lists, key=rank, reverse=True)
    for item in ordered:
        if item["seconds"] and item["seconds"] < MIN_FEATURE_SECONDS:
            continue
        if wanted and any(sub["kind"] == "pgs" and _lang_matches(sub["lang"], wanted, normalize)
                          for sub in item["subtitles"]):
            segments = stream_files(disc, item["clips"])
            if segments:
                return item, segments
    for item in ordered:
        if item["seconds"] and item["seconds"] < MIN_FEATURE_SECONDS:
            continue
        segments = stream_files(disc, item["clips"])
        if segments:
            return item, segments
    item = ordered[0]
    return item, stream_files(disc, item["clips"])


def _lang_matches(tag, wanted, normalize=None):
    normalizer = normalize or normalize_tag
    return normalizer(tag) in wanted


# --------------------------------------------------------------------------- #
# ISO：libbluray 优先，其次外部工具，都不行就把话说清楚
# --------------------------------------------------------------------------- #

ISO_TOOLS = ("isoinfo", "xorriso", "7z", "7za", "7zz", "bsdtar")


def iso_tool():
    """找到能列 ISO 内容的外部工具就用它，找不到返回 None。"""
    for name in ISO_TOOLS:
        if shutil.which(name):
            return name
    return None


def iso_members_with_isoinfo(iso_path):
    """``isoinfo -l -i x.iso`` 的输出解析成 ``{盘内路径: None}``（只想知道有没有）。"""
    import subprocess
    try:
        out = subprocess.run(["isoinfo", "-l", "-i", iso_path], stdout=subprocess.PIPE,
                             stderr=subprocess.DEVNULL, text=True, timeout=120).stdout
    except (OSError, ValueError):
        return []
    found = []
    for line in out.splitlines():
        for match in re.finditer(r"\s(\S+\.(?:m2ts|mpls|clpi|bdmv|bdj|jar))\s*;\d+\s*$", line):
            found.append(match.group(1))
    return found


def iso_members(iso_path):
    """列出 ISO 里 BDMV 的分卷；认不出来返回空列表。"""
    tool = iso_tool()
    if tool == "isoinfo":
        return iso_members_with_isoinfo(iso_path)
    if tool in ("7z", "7za", "7zz"):
        import subprocess
        try:
            out = subprocess.run([tool, "l", "-slt", iso_path], stdout=subprocess.PIPE,
                                 stderr=subprocess.DEVNULL, text=True, timeout=120).stdout
        except (OSError, ValueError):
            return []
        return [line.split("=", 1)[1].strip() for line in out.splitlines()
                if line.startswith("Path = ") and line.lower().endswith((".m2ts", ".mpls"))]
    return []


def extract_member(iso_path, member, destination, work_root=None):
    """把 ISO 里的一个文件抽到临时目录，成功返回落地路径。

    ``work_root`` 给定时按「盘内路径照原样落在 work_root 下面」核对落地点，
    7z / bsdtar 这类只能按目录解压的工具要用它，否则会把 BDMV 套两层。
    """
    import subprocess
    tool = iso_tool()
    if not tool:
        return None
    target = destination          # 调用方按 work_root + 盘内路径算好落地位置
    try:
        os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
    except OSError:
        return None
    if tool == "isoinfo":
        # isoinfo -x'/BDMV/STREAM/00001.m2ts' -i x.iso > 落地文件
        with open(target, "wb") as sink:
            ok = subprocess.call(["isoinfo", "-x/%s" % member.lstrip("/"), "-i", iso_path],
                                 stdout=sink, stderr=subprocess.DEVNULL) == 0
        return target if ok and os.path.getsize(target) > 0 else None
    if tool in ("7z", "7za", "7zz"):
        root = work_root or os.path.dirname(target)
        ok = subprocess.call([tool, "x", "-y", "-o%s" % root, iso_path, member],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0
        return target if ok and os.path.isfile(target) else None
    if tool == "xorriso":
        ok = subprocess.call([tool, "-indev", iso_path, "-map",
                              "/%s" % member.lstrip("/"), target],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0
        return target if ok and os.path.isfile(target) else None
    if tool == "bsdtar":
        root = work_root or os.path.dirname(target)
        ok = subprocess.call([tool, "-x", "-f", iso_path, "-C", root,
                              member.lstrip("/")],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) == 0
        return target if ok and os.path.isfile(target) else None
    return None


# --------------------------------------------------------------------------- #
# --------------------------------------------------------------------------- #
# 从 .m2ts 里抽 PGS
#
# ffprobe 对着 m2ts 经常只报视频和音频：字幕 PID 只写在 PMT 里，探测窗口内一个字幕包
# 都没出现时它就当没有。所以这里自己按 188 字节包拆——TS 的包结构很简单，PAT 找出 PMT，
# PMT 找出位图字幕的 PID 与类型，再把该 PID 的 PES 净荷顺序拼起来就是 .sup 段流。
# --------------------------------------------------------------------------- #

PACKET_SIZE = 188
SYNC_BYTE = 0x47
# 蓝光走 HDMV 私有码：0x90 是 PGS，0x91 是交互图形（菜单），0x92 是二级字幕。
# 认错代价很大：0x24 是 HEVC 视频、0x25 是可伸缩视频、0x80 是 DTS、0x81 是 AC-3、
# 0x82 是 DTS-HD，都不是字幕。真盘 PMT 里 15 条 0x90 连着排到 4768..4782 很常见。
PGS_STREAM_TYPES = {0x90: "pgs", 0x91: "igs", 0x92: "textst", 0x06: "dvb"}

# PMT 里出现过的合法 stream_type，用来判断「ES 循环是不是真的从这儿开始」。
KNOWN_STREAM_TYPES = (
    {0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x0A, 0x0B, 0x0D, 0x0E, 0x11}
    | set(range(0x0F, 0x1C))
    | set(range(0x24, 0x29))
    | {0x7F}
    | set(range(0x80, 0x94))
    | set(range(0xA0, 0xA6))
    | {0xAA, 0xC0, 0xEF, 0xFB}
)
BLOCK_PACKETS = 4096          # 一次读 4096 个包（约 768 KB），别把 NAS 的读盘打满


def _payload(packet):
    """取一个 TS 包的去指针、去 adaptation 之后的净荷；没有净荷返回 None。"""
    if len(packet) != PACKET_SIZE or packet[0] != SYNC_BYTE:
        return None
    afc = (packet[3] >> 4) & 0x03
    offset = 4
    # 规范顺序：先 adaptation field，再 payload_unit_start_indicator 的指针字段。
    # 反了会在真盘上错位——真盘的 afc 多半是 0x30，两样同时存在。
    if afc & 0x02:                             # 带 adaptation field
        if offset >= len(packet):
            return None
        offset += 1 + packet[offset]
    if afc & 0x01:                             # 带净荷
        if packet[1] & 0x40:                   # payload_unit_start_indicator
            if offset >= len(packet):
                return None
            pointer = packet[offset]
            start = offset + 1 + pointer if pointer != 0xFF else offset
            # 有些蓝光盘在 adaptation field 之后直接写 PES，不写指针字段：照规范读
            # 会整体错位一字节，PGS 的 PG 同步字就全丢了。两种写法都能对上的判据是
            # 「哪一边以 00 00 01 开头」。PAT/PMT 两边都不像 PES，仍按规范走。
            if not _starts_at(packet, start) and _starts_at(packet, offset):
                start = offset
            offset = start
    else:
        return None
    if offset >= len(packet):
        return None
    return packet[offset:] or None


def _starts_at(packet, at):
    """这个位置看起来像 PES 包的开头吗（packet_start_code_prefix + stream_id）。"""
    if at is None or at < 0 or at + 4 > len(packet):
        return False
    return packet[at] == 0x00 and packet[at + 1] == 0x00 and packet[at + 2] == 0x01 \
        and 0xB0 <= packet[at + 3] <= 0xEF


def _walk_packets(path, limit_bytes=None):
    """按 188 字节走一个文件，顺带在丢同步时重新对齐。"""
    read = 0
    with open(path, "rb") as handle:
        carry = b""
        while True:
            chunk = handle.read(PACKET_SIZE * BLOCK_PACKETS)
            if not chunk:
                return
            buffer = carry + chunk
            if len(buffer) < PACKET_SIZE:
                carry = buffer
                return
            usable = len(buffer) - (len(buffer) % PACKET_SIZE)
            carry = buffer[usable:]
            at = 0
            while at + PACKET_SIZE <= usable:
                if buffer[at] != SYNC_BYTE:
                    probe = buffer.find(bytes([SYNC_BYTE]), at, usable)
                    if probe < 0 or probe + PACKET_SIZE > usable:
                        break
                    at = probe
                yield buffer[at:at + PACKET_SIZE]
                at += PACKET_SIZE
            read += usable
            if limit_bytes and read >= limit_bytes:
                return


def _pmt_entries(payload):
    """从 PMT 段里抠出 ES 循环，返回 ``[(pid, stream_type)]``，解析不出来给 None。

    program_number 和 program_info_length 之间那几个保留字节，规范写 3 个，
    实际封装工具有塞 4~5 个的，真盘和自检用的盘正好差两位，硬编码一个偏移总
    会漏掉一种。这里逐个试候选位置，只认「每条流的保留位都对、类型是已知值、
    并且循环正好走到段尾」的那种。
    """
    section_length = _u16(payload, 1) & 0x0FFF
    if section_length < 9:
        return None
    end = min(len(payload), 3 + section_length - 4)
    best = None
    for base in range(4, min(16, end - 1)):
        info_length = _u16(payload, base) & 0x0FFF
        at = base + 2 + info_length
        if at + 5 > end:
            continue
        entries = []
        while at + 5 <= end:
            pid_raw = _u16(payload, at + 1)
            es_info = _u16(payload, at + 3)
            stream_type = payload[at]
            if pid_raw & 0xE000 != 0xE000 or es_info & 0xF000 != 0xF000:
                break
            pid = pid_raw & 0x1FFF
            if not pid or stream_type not in KNOWN_STREAM_TYPES:
                break
            entries.append((pid, stream_type))
            at += 5 + (es_info & 0x0FFF)
        if not entries or abs(at - end) > 2:
            continue
        score = (sum(1 for _, stream_type in entries if stream_type in PGS_STREAM_TYPES),
                 len(entries))
        if best is None or score > best[0]:
            best = (score, entries)
    return best[1] if best else None


def pmt_subtitle_pids(path, probe_bytes=4 * 1024 * 1024):
    """扫分卷开头的 PAT/PMT，返回 ``[(pid, 类型名)]``，只留位图字幕那几种。"""
    pmt_pids = set()
    found = []
    seen = set()
    try:
        for packet in _walk_packets(path, probe_bytes):
            if packet[1] & 0x80:               # transport_error_indicator
                continue
            tsi = ((packet[1] & 0x1F) << 8) | packet[2]
            payload = _payload(packet)
            if not payload or len(payload) < 9:
                continue
            if tsi == 0x0000 and payload[0] == 0x00:
                section_length = _u16(payload, 1) & 0x0FFF
                index = 8
                end = min(len(payload), 3 + section_length - 4)
                while index + 4 <= end:
                    program_number = _u16(payload, index)
                    pid = _u16(payload, index + 2) & 0x1FFF
                    if program_number and pid:
                        pmt_pids.add(pid)
                    index += 4
            elif tsi in pmt_pids and payload[0] == 0x02:
                for pid, stream_type in _pmt_entries(payload) or []:
                    kind = PGS_STREAM_TYPES.get(stream_type)
                    if kind and pid not in seen:
                        seen.add(pid)
                        found.append((pid, kind))
    except OSError:
        return []
    return found


def _is_pes_start(payload):
    return (len(payload) >= 6 and payload[0] == 0 and payload[1] == 0
            and payload[2] == 1 and 0xB0 <= payload[3] <= 0xEF)


def _pes_data(payload):
    """剥掉 PES 头，返回其中的数据。PES 头读不懂就当整包都是数据。"""
    if not _is_pes_start(payload) or len(payload) < 9:
        return payload
    pes_header_data_length = payload[8]
    offset = 9 + pes_header_data_length
    return payload[offset:] if offset < len(payload) else b""


# PGS 段类型：0x14 PDS 调色板、0x15 ODS 光栅、0x16 WDS 窗口、0x17 PCS 呈现、0x80 END。
PGS_SEGMENT_TYPES = {0x14, 0x15, 0x16, 0x17, 0x80}
MAX_PES_BYTES = 2 * 1024 * 1024      # 单条 PES 攒到这么大就当作一帧收尾，防跑飞


def _pes_pts(payload):
    """从 PES 头里取 PTS（90 kHz）；没有就返回 None。"""
    if not _is_pes_start(payload) or len(payload) < 14:
        return None
    flags = payload[7]
    if (flags & 0xC0) != 0x80:          # 只有 PTS；带 DTS 的这里取前 5 字节也一样是 PTS
        if (flags & 0xC0) != 0xC0:
            return None
    if payload[8] < 5:
        return None
    fields = payload[9:14]
    pts = ((fields[0] >> 1) & 0x07) << 30
    pts |= (fields[1] << 22) | ((fields[2] >> 1) << 15)
    pts |= (fields[3] << 7) | (fields[4] >> 1)
    return pts & 0x1FFFFFFFF


def frame_segments(buffer, pts=None):
    """把 PES 净荷整理成 ``.sup`` 的写法。

    盘里的 ``.m2ts`` 每条 PES 装一个 PGS 段，净荷只有「类型 + 段长度 + 段数据」，
    ``PG`` 同步字和 8 字节 PTS 是 ``.sup`` 容器补上去的（蓝光方言：``PG`` + 段长
    (2) + 类型(1) + PTS(8) + 段数据，段长 = 9 + 段数据）。ffmpeg 直接 dump 的
    ``.sup`` 已经带 ``PG``，照抄即可。认不出的数据宁可不写，也别混进 .sup。
    """
    if not buffer:
        return b""
    if buffer[:2] == b"PG":
        return bytes(buffer)           # 已经带 PG 的（ffmpeg dump 的 .sup），原样写就好
    stamp = int(pts) if pts is not None else 0
    out = bytearray()
    at = 0
    total = len(buffer)
    while at < total:
        if buffer[at] not in PGS_SEGMENT_TYPES:
            break
        if at + 3 > total:
            break
        length = _u16(buffer, at + 1)
        end = at + 3 + length
        if end > total:
            break                      # 段没收全，丢掉尾巴
        payload = buffer[at + 3:end]
        out += b"PG" + _be16(9 + len(payload)) + bytes([buffer[at]])
        out += stamp.to_bytes(8, "big") + payload
        at = end
    return bytes(out)


def demux_pgs(paths, pid, out_path, max_bytes=96 * 1024 * 1024):
    """把若干分卷里这个 PID 的 PGS 段按顺序写成 ``.sup``，返回写出的字节数。

    超过 ``max_bytes`` 就提前收手：一轨 PGS 正常也就几 MB 到几十 MB，再大是数据不对，
    别把盘写坏。
    """
    written = 0
    pending = bytearray()
    pending_pts = [None]

    def flush(sink):
        nonlocal written
        if not pending:
            return True
        data = frame_segments(bytes(pending), pending_pts[0])
        pending.clear()
        if not data:
            return True
        room = max_bytes - written
        if room <= 0:
            return False
        if len(data) > room:
            data = data[:room]
        sink.write(data)
        written += len(data)
        return written < max_bytes

    try:
        with open(out_path, "wb") as sink:
            for path in paths:
                if not os.path.isfile(path):
                    continue
                for packet in _walk_packets(path):
                    if packet[1] & 0x80:
                        continue
                    tsi = ((packet[1] & 0x1F) << 8) | packet[2]
                    if tsi != pid:
                        continue
                    payload = _payload(packet)
                    if not payload:
                        continue
                    if packet[1] & 0x40:                  # 新 PES 开始，先把上一条收口
                        if not flush(sink):
                            return written
                        pending_pts[:] = [_pes_pts(payload)]
                        payload = _pes_data(payload)
                    if not payload:
                        continue
                    pending += payload
                    if len(pending) >= MAX_PES_BYTES and not flush(sink):
                        return written
                flush(sink)                               # 换分卷，别把上一条 PES 带着
    except OSError:
        return written
    return written
