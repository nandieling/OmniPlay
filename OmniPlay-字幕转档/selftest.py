#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""瘦身版的离线自检：造几个真片源，跑一遍服务，核对产物和接口。

不用 docker，也不用 NAS，本机有 ffmpeg 就能跑：

    python3 selftest.py

覆盖：文本轨抽成 SRT、已有外挂字幕跳过、没字幕判成转不了、只读目录报只读、
m2ts 里的 PGS 抽成 .sup、HTTP 接口和令牌、扫两遍不重做。
"""

import io
import json
import os
import shutil
import socket
import sqlite3
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SIDECAR = os.path.join(HERE, "sidecar.py")
PORT = int(os.environ.get("SIDECAR_TEST_PORT", "45799"))
FFMPEG = os.environ.get("FFMPEG", "ffmpeg")

failures = []
# 本机常挂着系统代理（127.0.0.1:1082 之类），自检一律直连，别让代理插一手
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def check(name, ok, extra=""):
    print(("  成  " if ok else "  败  ") + name + (("  " + extra) if extra else ""))
    if not ok:
        failures.append(name)


def sh(args):
    process = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    if process.returncode != 0:
        raise RuntimeError(" ".join(args) + "\n" + process.stderr)
    return process.stdout


# --------------------------------------------------------------------------- #
# 夹具：真视频 + 两条文本字幕 / 已有字幕 / 没字幕 / 只读目录 / 手搓的 PGS m2ts
# --------------------------------------------------------------------------- #

SRT = """1
00:00:01,000 --> 00:00:03,000
第一句中文字幕

2
00:00:05,500 --> 00:00:08,000
第二句：Hello
"""

ASS = """[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Default,Arial,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,2,2,10,10,10,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
Dialogue: 0,0:00:02.00,0:00:04.00,Default,,0,0,0,,English line one
Dialogue: 0,0:00:06.00,0:00:09.00,Default,,0,0,0,,English line two
"""

# 塞进 PGS 调色板段里当记号用，好确认载荷原样写进了 .sup
PGS_MARK = b"\x01\x80\x00\x00"


def free_port():
    """先让系统挑一个空端口，别撞上本机已有的服务。"""
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
    sock.close()
    return port


def mpeg2_crc(payload):
    """MPEG-2 的 CRC-32：多项式 0x04C11DB7，不反转、末值不异或。"""
    crc = 0xFFFFFFFF
    for byte in payload:
        crc ^= byte << 24
        for _ in range(8):
            if crc & 0x80000000:
                crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF
            else:
                crc = (crc << 1) & 0xFFFFFFFF
    return crc


def pts_bytes(value, prefix):
    """把 33 位时间戳封成 5 个字节；prefix 是那 4 位前缀（PES 用 2，PGS 用 8）。"""
    return bytes([
        ((prefix & 0xF) << 4) | (((value >> 30) & 0x7) << 1) | 1,
        (value >> 22) & 0xFF,
        (((value >> 15) & 0x7F) << 1) | 1,
        (value >> 7) & 0xFF,
        (((value << 1) & 0xFE) | 1),
    ])


def pcr_bytes(base):
    return bytes([(base >> 25) & 0xFF, (base >> 17) & 0xFF, (base >> 9) & 0xFF,
                  (base >> 1) & 0xFF, ((base << 7) & 0x80) | 0x7E, 0x00])


def ts_packet(pid, payload, adaptation=b"", pusi=False, counter=0):
    """188 字节一个包：同步字节 + PID + 连续计数，多出来的地方填 0xFF。"""
    flags = 0x30 if adaptation else 0x10
    header = bytes([0x47, (0x40 if pusi else 0x00) | ((pid >> 8) & 0x1F), pid & 0xFF,
                    flags | (counter & 0x0F)])
    body = (bytes([len(adaptation)]) + adaptation) if adaptation else b""
    packet = header + body + payload
    if len(packet) < 188:
        packet += b"\xff" * (188 - len(packet))
    return packet[:188]


def table(table_id, inner):
    """拼一个带 section_syntax 的表段：长度字段现算，尾巴上 CRC。"""
    length = len(inner) + 4
    head = bytes([table_id, 0xB0 | ((length >> 8) & 0x0F), length & 0xFF])
    raw = head + inner
    return b"\x00" + raw + struct.pack(">I", mpeg2_crc(raw))


def pgs_segment(kind, pts_90k, payload):
    """一个 PGS 段：签名 PS + PTS + 类型 + 长度 + 数据。"""
    return b"PS" + pts_bytes(pts_90k, 8) + bytes([kind]) + struct.pack(">H", len(payload)) + payload


def build_pgs_m2ts(path):
    """手搓一个只有一条 PGS 字幕轨的 .m2ts，够 ffprobe 辨出来就行。"""
    out = bytearray()
    # PAT：一个节目，指向 PMT
    out += ts_packet(0x0000, table(0x00, struct.pack(">HBH", 0x0001, 0xC1, 0xF000)
                                 + struct.pack(">HH", 0x0001, 0xE000 | 0x1000)), pusi=True)
    # PMT：一条 0x90（PGS）轨，挂个语言描述符
    es_info = bytes([0x6A, 0x04]) + b"chi\x00"
    streams = (bytes([0x90]) + struct.pack(">H", 0xE000 | 0x1200)
               + struct.pack(">H", 0xF000 | len(es_info)) + es_info)
    out += ts_packet(0x1000, table(0x02, struct.pack(">HBHH", 0x0001, 0xC1,
                                                    0xE000 | 0x1200, 0xF000) + streams),
                     pusi=True)

    # 两个显示集：调色板 + 对象 + 结束段，各自一个 PES 包
    counter = 0
    for seconds in (3.0, 62.0):
        pts = int(seconds * 90000)
        display_set = (pgs_segment(0x14, pts, bytes([counter & 0xFF]) + b"\x00\x00"
                                   + PGS_MARK + b"\x01\x80\x00\x00")
                       + pgs_segment(0x15, pts, b"\x00\x00" + b"\x00" * 8)
                       + pgs_segment(0x17, pts, b""))
        header = b"\x84\x80\x05" + pts_bytes(pts, 2)
        pes = bytes([0x00, 0x00, 0x01, 0xBD]) + struct.pack(">H", len(header) + len(display_set))
        pes += header + display_set
        head_room = 184 - (4 + 1 + len(pcr_bytes(pts)) + 2)
        chunks = [pes[:head_room]]
        rest = pes[head_room:]
        while rest:
            chunks.append(rest[:184])
            rest = rest[184:]
        for position, chunk in enumerate(chunks):
            adaptation = b""
            if position == 0:
                flags = b"\x10" + pcr_bytes(pts)
                adaptation = flags + b"\xff" * max(0, 3 - len(flags))
            out += ts_packet(0x1200, chunk, adaptation=adaptation,
                             pusi=position == 0, counter=counter)
            counter += 1
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(bytes(out))


def build_library(root):
    srt = os.path.join(root, "a.srt")
    ass = os.path.join(root, "b.ass")
    io.open(srt, "w", encoding="utf-8").write(SRT)
    io.open(ass, "w", encoding="utf-8").write(ASS)

    def mkv(target, extra):
        os.makedirs(os.path.dirname(target), exist_ok=True)
        sh([FFMPEG, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=3:size=128x72:rate=10"] + extra
           + ["-map", "0:v", "-c:v", "mpeg4", target])

    chinese = ["-i", srt, "-map", "1:0", "-c:s", "copy", "-metadata:s:s:0", "language=chi"]
    mkv(os.path.join(root, "Movies", "大电影.2019.1080p.mkv"),
        ["-i", srt, "-i", ass, "-map", "1:0", "-map", "2:0", "-c:s", "copy",
         "-metadata:s:s:0", "language=chi", "-metadata:s:s:1", "language=eng"])
    mkv(os.path.join(root, "TV", "已有字幕.2020.1080p.mkv"), chinese)
    io.open(os.path.join(root, "TV", "已有字幕.2020.1080p.zh.srt"), "w",
            encoding="utf-8").write(SRT)
    mkv(os.path.join(root, "TV", "没字幕.2021.1080p.mkv"), [])
    mkv(os.path.join(root, "ReadOnly", "只读片.mkv"), chinese)
    build_pgs_m2ts(os.path.join(root, "BD", "mainframe.m2ts"))
    build_pgs_m2ts(os.path.join(root, "BD", "pushme.m2ts"))
    os.chmod(os.path.join(root, "ReadOnly"), 0o555)


# --------------------------------------------------------------------------- #
# 跑服务
# --------------------------------------------------------------------------- #

def run_once(root, db, extra=None):
    args = [sys.executable, SIDECAR, "--roots", root, "--db", db, "--once", "--sleep", "0",
            # --once 本来就绕开闸门；阈值也关掉，免得跑在空间小的临时目录上一致失败
            "--min-free-gb", "0"]
    args += extra or []
    process = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    for line in process.stdout.splitlines():
        print("   ", line)
    return process.returncode


def read_rows(db):
    conn = sqlite3.connect(db)
    conn.row_factory = sqlite3.Row
    rows = {row["path"]: dict(row) for row in conn.execute("SELECT * FROM jobs")}
    conn.close()
    return rows


def http(method, path, body=None, token=None, port=None):
    port = port or PORT        # 晚绑：main 里换过一次端口，默认参数会把它钉在旧值上
    request = urllib.request.Request("http://127.0.0.1:%d%s" % (port, path), method=method,
                                     data=json.dumps(body).encode("utf-8") if body else None)
    request.add_header("Content-Type", "application/json")
    if token:
        request.add_header("Authorization", "Bearer " + token)
    try:
        with OPENER.open(request, timeout=10) as response:
            raw = response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()
    try:
        return 200, json.loads(raw.decode("utf-8"))
    except ValueError:
        return 200, raw.decode("utf-8")


def serve(library, db, extra=None, port=None, autostart=True, min_free_gb="0"):
    port = port or PORT
    """把服务起起来，顺带把它的输出留在 server.log 里，起不来时好查。"""
    log = open(os.path.join(os.path.dirname(db), "server-%d.log" % port), "wb")
    args = [sys.executable, SIDECAR, "--roots", library, "--db", db,
            "--port", str(port), "--interval", "0", "--sleep", "0"]
    if min_free_gb is not None:
        args += ["--min-free-gb", str(min_free_gb)]
    if autostart:
        args.append("--autostart")
    args += extra or []
    process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT)
    process.log_path = log.name
    deadline = time.time() + 15
    while time.time() < deadline:
        if process.poll() is not None:
            break
        try:
            with OPENER.open("http://127.0.0.1:%d/health" % port, timeout=1):
                return process
        except urllib.error.HTTPError:
            return process          # 401 也算起来了
        except Exception:
            time.sleep(0.2)
    process.terminate()
    print("服务起不来，日志：")
    print(io.open(process.log_path, encoding="utf-8", errors="replace").read())
    raise RuntimeError("HTTP 没起来")


def main():
    global PORT
    PORT = free_port()
    root = tempfile.mkdtemp(prefix="sidecar-selftest-")
    library = os.path.join(root, "media")
    os.makedirs(library)
    db = os.path.join(root, "state.db")
    build_library(library)
    print("造好片源：%s" % library)

    run_once(library, db)
    rows = read_rows(db)
    movie = rows.get("Movies/大电影.2019.1080p.mkv", {})
    print("\n扫描判定")
    check("电影转成了", movie.get("status") == "done", str(movie.get("status")))
    check("语言归一成 zh", movie.get("language") == "zh", str(movie.get("language")))
    check("挑中的是中文那条轨", movie.get("track_index") == 0, str(movie.get("track_index")))
    srt_out = os.path.join(library, "Movies", "大电影.2019.1080p.zh.srt")
    check("写出了 zh.srt", os.path.isfile(srt_out))
    body = io.open(srt_out, encoding="utf-8", errors="replace").read() if os.path.isfile(srt_out) else ""
    check("SRT 里两句中文都在", "第一句中文字幕" in body and "Hello" in body)
    check("SRT 时间轴没跑偏", "00:00:05,500 --> 00:00:08,000" in body)
    check("没往视频旁边丢临时文件",
          not [name for name in os.listdir(os.path.join(library, "Movies")) if name.startswith(".")])
    check("已有中文字幕的直接跳过",
          rows.get("TV/已有字幕.2020.1080p.mkv", {}).get("status") == "skipped",
          str(rows.get("TV/已有字幕.2020.1080p.mkv", {}).get("detail")))
    naked = rows.get("TV/没字幕.2021.1080p.mkv", {})
    check("没字幕的判成转不了", naked.get("status") == "unsupported",
          "%s %s" % (naked.get("status"), naked.get("detail")))
    only = rows.get("ReadOnly/只读片.mkv", {})
    check("只读目录报只读", only.get("status") == "readonly",
          "%s %s" % (only.get("status"), only.get("detail")))
    check("蓝光分卷不主动扫", "BD/mainframe.m2ts" not in rows)

    print("\n重复扫描")
    run_once(library, db)
    check("第二遍不重做", read_rows(db)["Movies/大电影.2019.1080p.mkv"]["status"] == "done")

    print("\nPGS")
    process = serve(library, db)
    try:
        http("POST", "/api/jobs", {"files": ["BD/pushme.m2ts"]})
        deadline = time.time() + 25
        pgs = {}
        while time.time() < deadline:
            pgs = read_rows(db).get("BD/pushme.m2ts", {})
            if pgs.get("status") in ("done", "failed", "unsupported", "readonly"):
                break
            time.sleep(0.3)
        # 本机没有真蓝光片源，手搓的 TS 只能算参考：ffmpeg 认不出来就记一行，不算失败
        if pgs.get("format") == "sup":
            check("m2ts 认出 PGS 轨并转成 sup", pgs.get("status") == "done", str(pgssummary(pgs)))
            sup = os.path.join(library, "BD", "pushme.zh.sup")
            blob = open(sup, "rb").read() if os.path.isfile(sup) else b""
            check("两个显示集的载荷都还在", blob.count(PGS_MARK) >= 2,
                  "共 %d 字节，前 12 字节 %s" % (len(blob), blob[:12].hex()))
        else:
            print("  跳过  PGS 那一条要拿真蓝光的 m2ts 验（本机 ffmpeg 不认手搓的 TS：%s）"
                  % pgs.get("detail"))

        print("\nHTTP 接口")
        code, health = http("GET", "/health")
        check("/health 可用", code == 200 and health.get("ok")
              and health["roots"][0]["root"] == library,
              "端口上坐的是别人：" + str(health.get("roots"))[:110])
        code, page = http("GET", "/")
        check("GUI 页面能出来", code == 200 and "OmniPlay 字幕转档" in page)
        code, jobs = http("GET", "/api/jobs?limit=50")
        check("/api/jobs 有内容", code == 200 and len(jobs["jobs"]) >= 4
              and jobs["counts"].get("done", 0) >= 1, str(jobs["counts"]))
        code, answer = http("POST", "/api/jobs",
                           {"files": ["BD/mainframe.m2ts", "不存在.mkv", "../../etc/passwd"]})
        check("点名把路径不对的挡在外面", code == 200 and answer["accepted"] == []
              and len(answer["rejected"]) == 3, str(answer))
        deadline = time.time() + 25
        m2ts = {}
        while time.time() < deadline:
            found = [item for item in read_rows(db).values() if item["path"] == "BD/mainframe.m2ts"]
            m2ts = found[0] if found else {}
            if m2ts.get("status") in ("done", "failed", "unsupported"):
                break
            time.sleep(0.3)
        check("点名的 m2ts 有结论", m2ts.get("status") in ("done", "unsupported"),
              "%s %s" % (m2ts.get("status"), m2ts.get("detail")))
        code, answer = http("POST", "/api/scan")
        check("/api/scan 跑得动", code == 200 and "enqueued" in answer)

        print("\n分页、分辨率与磁盘")
        check("/health 报了磁盘容量", health.get("disk")
              and health["disk"][0]["total"] > 0 and "low" in health["disk"][0],
              str(health.get("disk"))[:90])
        check("/health 带了策略", isinstance(health.get("policy"), dict)
              and health["policy"].get("min_height") == 0, str(health.get("policy")))
        check("autostart 起来就在跑", health.get("state") == "running", str(health.get("state")))
        code, answer = http("POST", "/api/run", {"action": "pause"})
        check("能暂停", code == 200 and answer["state"] == "paused", str(answer)[:80])
        code, scanned = http("POST", "/api/scan")
        check("暂停时扫描只排队不开工", code == 200 and scanned["state"] == "paused", str(scanned))
        code, health = http("GET", "/health")
        check("暂停状态从接口读得出来", health.get("state") == "paused"
              and health["state"] != "running", str(health.get("state")))
        code, answer = http("POST", "/api/run", {"action": "start"})
        check("能继续", code == 200 and answer["state"] == "running", str(answer)[:80])
        code, answer = http("POST", "/api/run", {"action": "nonsense"})
        check("不认识的 action 被拒", code == 400, str(code))
        code, paged = http("GET", "/api/jobs?page=1&per_page=5")
        check("列表能翻页", code == 200 and len(paged["jobs"]) == 5 and paged["total"] >= 6
              and paged["pages"] >= 2, str({k: paged.get(k) for k in ("total", "page", "pages")}))
        code, second = http("GET", "/api/jobs?page=2&per_page=5")
        check("翻到第二页拿到剩下的", code == 200 and second["page"] == 2
              and len(second["jobs"]) == paged["total"] - 5
              and not ({item["path"] for item in second["jobs"]}
                       & {item["path"] for item in paged["jobs"]}), str(second.get("total")))
        code, filtered = http("GET", "/api/jobs?res=sdp&q=%E5%A4%A7%E7%94%B5%E5%BD%B1")
        check("能按分辨率和文件名筛", code == 200 and len(filtered["jobs"]) == 1
              and filtered["jobs"][0]["height"] == 72, str(filtered.get("total")))
        code, filtered = http("GET", "/api/jobs?res=uhd")
        check("4K 筛出来是空的", code == 200 and filtered["total"] == 0, str(filtered.get("total")))
        code, answer = http("POST", "/api/policy", {"min_height": 1080})
        check("改策略只改判定不毁产物", code == 200
              and answer["policy"]["min_height"] == 1080 and answer["cleared"] == 0, str(answer))
        code, answer = http("POST", "/api/policy", {"min_height": "abc"})
        check("策略里的乱数字被拒", code == 400, str(code))
        http("POST", "/api/policy", {"min_height": 0})
        code, answer = http("POST", "/api/policy", {"min_free_gb": 999999})
        code, health = http("GET", "/health")
        check("阈值调到天上就报空间不够", code == 200 and health.get("disk_blocked") is True
              and health["disk"][0]["low"] is True, str(health["disk"][0].get("low")))
        http("POST", "/api/policy", {"min_free_gb": 0})

        # 顶部计数格就是一排筛选项：一格一状态，各自的数量对得上，加起来等于全量。
        http("POST", "/api/run", {"action": "pause"})
        code, allpage = http("GET", "/api/jobs?status=all")
        singles = ("pending", "running", "done", "failed", "readonly",
                   "unsupported", "skipped")
        pieces, clean = {}, True
        for name in singles:
            one_code, one = http("GET", "/api/jobs?status=" + name)
            pieces[name] = int(one.get("total", -1))
            clean = clean and one_code == 200 and all(
                item["status"] == name for item in one.get("jobs", []))
        check("顶部每一格都筛得出对应的行", clean and code == 200
              and all(pieces[name] == allpage["counts"].get(name, 0) for name in singles)
              and sum(pieces.values()) == int(allpage["total"]),
              "%s vs 全量 %s" % (pieces, allpage.get("total")))
        http("POST", "/api/run", {"action": "start"})

        # 分辨率阈值只在「还没干过活」的片子上生效，所以拿一份干净的库来验。
        gated_root = os.path.join(root, "gated")
        gated_lib = os.path.join(gated_root, "media")
        os.makedirs(os.path.join(gated_lib, "Movies"))
        shutil.copy2(os.path.join(library, "Movies", "大电影.2019.1080p.mkv"),
                     os.path.join(gated_lib, "Movies", "大电影.2019.1080p.mkv"))
        run_once(gated_lib, os.path.join(gated_root, "state.db"), ["--min-height", "1080"])
        gated = read_rows(os.path.join(gated_root, "state.db")).get(
            "Movies/大电影.2019.1080p.mkv", {})
        check("72p 的按策略跳过", gated.get("status") == "skipped"
              and "按策略不转" in str(gated.get("detail")), str(gated.get("detail")))
        check("跳过的没落地产物",
              not os.path.isfile(os.path.join(gated_lib, "Movies", "大电影.2019.1080p.zh.srt")))
        run_once(gated_lib, os.path.join(gated_root, "state2.db"))
        freed = read_rows(os.path.join(gated_root, "state2.db")).get(
            "Movies/大电影.2019.1080p.mkv", {})
        check("不设阈值就照常转", freed.get("status") == "done", str(freed.get("status")))

    finally:
        process.terminate()
        process.wait()

    print("\n默认不自动开工")
    boot_port = free_port()
    # 用全新的状态表：上一轮在页面上存过的策略会盖掉默认值，那是应有的行为，
    # 但要验默认值就得拿一份没被写过的库。
    boot_db = os.path.join(root, "boot", "state.db")
    os.makedirs(os.path.dirname(boot_db))
    process = serve(library, boot_db, extra=["--interval", "3000"], port=boot_port,
                  autostart=False, min_free_gb=None)
    try:
        code, health = http("GET", "/health", port=boot_port)
        check("容器起来先停着", code == 200 and health.get("state") == "paused",
              str(health.get("state")))
        check("默认阈值是 500 GB", health["policy"]["min_free_gb"] == 500,
              str(health["policy"]))
        check("空间不够会另外标出来", health.get("disk_blocked") is True
              or health["disk"][0]["low"] is False, str(health["disk"][0]))
        # 间隔设成 3000 秒：点了开始要是没马上扫，就会一直空等，这条就是抓那个 bug 的
        scans_before = health.get("stats", {}).get("scans", 0)
        http("POST", "/api/run", {"action": "start"}, port=boot_port)
        deadline = time.time() + 10
        while time.time() < deadline:
            code, health = http("GET", "/health", port=boot_port)
            if health["stats"]["scans"] > scans_before:
                break
            time.sleep(0.3)
        check("点了开始立刻扫，不等下一个间隔", health["stats"]["scans"] > scans_before,
              "scans %s -> %s" % (scans_before, health["stats"]["scans"]))
        check("扫描中会在接口里说", "scanning" in health, str(sorted(health))[:90])
    finally:
        process.terminate()
        process.wait()

    print("\n鉴权")
    auth_port = free_port()
    process = serve(library, db, ["--token", "s3cr3t"], port=auth_port)
    try:
        code, plain = http("GET", "/api/health", port=auth_port)
        check("没带令牌被拒", code == 401, str(code))

        code, health = http("GET", "/api/health", token="s3cr3t", port=auth_port)
        check("带了令牌能用", code == 200 and health.get("ok"))
    finally:
        process.terminate()
        process.wait()

    os.chmod(os.path.join(library, "ReadOnly"), 0o755)
    shutil.rmtree(root, ignore_errors=True)
    print("\n%d 项没通过" % len(failures))
    for name in failures:
        print("  - " + name)
    return 1 if failures else 0


def pgssummary(row):
    return {key: row.get(key) for key in ("status", "format", "language", "detail")}


if __name__ == "__main__":
    sys.exit(main())
