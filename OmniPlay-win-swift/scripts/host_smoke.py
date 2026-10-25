#!/usr/bin/env python3
"""跨语言宿主冒烟：用 ctypes 假扮 WPF，走一遍 C ABI + JSON 契约。

Windows 那边用 P/Invoke 做的是同一件事，所以这一步在 mac 上先过一遍，
能提前抓到导出符号、内存归属、UTF-8 这些边界问题。

用法：python3 scripts/host_smoke.py [libomniplay-core.dylib|omniplay-core.dll]
"""
import ctypes
import json
import pathlib
import re
import sys

def default_library() -> pathlib.Path:
    root = pathlib.Path(__file__).resolve().parent.parent
    for candidate in (".build/out/Products/Debug", ".build/debug", ".build/release"):
        for name in ("libomniplay-core.dylib", "omniplay-core.dll"):
            path = root / candidate / name
            if path.exists():
                return path
    raise SystemExit("找不到核心动态库，先跑 swift build 或把路径作为参数传进来")


failures: list[str] = []


def check(condition: bool, what: str, detail: str = "") -> None:
    if not condition:
        failures.append(f"{what}" + (f" —— {detail}" if detail else ""))


lib = ctypes.CDLL(str(default_library() if len(sys.argv) < 2 else sys.argv[1]))
lib.omniplay_create.restype = ctypes.c_void_p
lib.omniplay_destroy.argtypes = [ctypes.c_void_p]
lib.omniplay_version.restype = ctypes.c_char_p
lib.omniplay_invoke.restype = ctypes.c_void_p
lib.omniplay_invoke.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32,
                                ctypes.POINTER(ctypes.c_int32)]
lib.omniplay_free_bytes.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
callback_type = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int32)
lib.omniplay_set_event_callback.argtypes = [ctypes.c_void_p, ctypes.c_void_p, callback_type]

pushed: list[dict] = []


def on_event(context, payload, length):
    pushed.append(json.loads(ctypes.string_at(payload, length).decode("utf-8")))


handle = lib.omniplay_create()
check(bool(handle), "核心创建句柄")
keepalive = callback_type(on_event)   # 回调必须被宿主持有
lib.omniplay_set_event_callback(handle, None, keepalive)


def invoke(method: str, params: dict | None = None) -> dict:
    request = json.dumps({"method": method, "params": params or {}}, ensure_ascii=False).encode("utf-8")
    size = ctypes.c_int32()
    pointer = lib.omniplay_invoke(handle, request, len(request), ctypes.byref(size))
    if not pointer:
        raise AssertionError(f"{method} 返回空指针")
    try:
        return json.loads(ctypes.string_at(pointer, size.value).decode("utf-8"))
    finally:
        lib.omniplay_free_bytes(handle, pointer)


version_text = lib.omniplay_version().decode("utf-8")
check(version_text.startswith("0."), "版本号可读", version_text)

version = invoke("version")
check(version["ok"] is True, "version 调用成功")
check(version["result"]["core"] == version_text, "两条版本通道一致", version["result"]["core"])
check("theme.get" in version["result"]["features"], "能力清单含 theme.get")

theme = invoke("theme.get", {"color": "rose", "style": "liquidGlass"})["result"]
check(re.fullmatch(r"#[0-9A-F]{8}", theme["accent"]["argb"]) is not None,
      "配色输出 #AARRGGBB", theme["accent"]["argb"])
check(len(theme["backdrop"]) == 3, "玻璃背景三段")
check(theme["glass"] is not None, "玻璃配方存在")

sorted_ids = invoke("home.sort", {
    "key": "name", "ascending": True,
    "items": [{"id": 1, "title": "肖申克的救赎"}, {"id": 2, "title": "阿凡达"}, {"id": 3, "title": "流浪地球"}],
})["result"]["ids"]
check(sorted_ids == [2, 3, 1], "中文名按拼音排序", str(sorted_ids))

groups = invoke("category.groups", {
    "mode": "director", "ascending": True,
    "items": [{"id": 1, "title": "甲", "directors": ["宫崎骏"]},
              {"id": 2, "title": "乙", "directors": ["Nolan"]}],
})["result"]["groups"]
check([group["name"] for group in groups] == ["宫崎骏", "Nolan"], "外文名排在中文名之后",
      str([group["name"] for group in groups]))

unicode_probe = invoke("echo", {"text": "觅影 😀 反斜杠 \\ 换行\n"})
check(unicode_probe["result"]["text"] == "觅影 😀 反斜杠 \\ 换行\n", "非 ASCII 原样往返",
      unicode_probe["result"]["text"])

broken = invoke("没有这个方法")
check(broken["ok"] is False and broken["error"], "未知名回人话错误", str(broken))

cursor = invoke("events.push", {"topic": "probe", "payload": {"count": 42}})["result"]["cursor"]
check(len(pushed) == 1, "宿主回调被调用", f"收到 {len(pushed)} 条")
if pushed:
    check(pushed[0]["topic"] == "probe" and pushed[0]["payload"]["count"] == 42, "事件载荷完整")
    check(pushed[0]["cursor"] == cursor, "事件游标一致")

lib.omniplay_destroy(handle)

for line in failures:
    print("  FAIL", line)
print("host-smoke: OK" if not failures else f"host-smoke: FAIL {len(failures)} 项")
sys.exit(0 if not failures else 1)
