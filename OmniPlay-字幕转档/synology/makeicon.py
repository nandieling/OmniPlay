#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""画套件图标：深绿圆角方块 + 底下几条字幕条。纯标准库，不引图像库。

    python3 makeicon.py 输出.png 边长
"""

import struct
import sys
import zlib

BG = (31, 111, 92, 255)     # 跟管理页面同一个强调色
BAR = (240, 245, 243, 235)


def rounded(x, y, box, radius):
    """点是否落在圆角方框里（含边界附近的圆角弧）。"""
    left, top, right, bottom = box
    if not (left <= x <= right and top <= y <= bottom):
        return False
    cx = min(max(x, left + radius), right - radius)
    cy = min(max(y, top + radius), bottom - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius * radius


def draw(size):
    edge = int(size * 0.06)
    plate = (edge, edge, size - edge - 1, size - edge - 1)
    radius = int(size * 0.2)
    bar_h = max(2, int(size * 0.09))
    # 底下两条字幕条，上面留空：远看就是一块字幕
    bars = [(0.20, 0.80, 0.60), (0.20, 0.62, 0.80)]
    pixels = bytearray(size * size * 4)
    for y in range(size):
        for x in range(size):
            color = (0, 0, 0, 0)
            if rounded(x, y, plate, radius):
                color = BG
                for left_ratio, right_ratio, center_ratio in bars:
                    box = (int(size * left_ratio), int(size * center_ratio) - bar_h // 2,
                           int(size * right_ratio), int(size * center_ratio) + bar_h // 2)
                    if rounded(x, y, box, bar_h // 2):
                        color = BAR
                        break
            offset = (y * size + x) * 4
            pixels[offset:offset + 4] = bytes(color)
    return pixels


def png_bytes(size, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[row * size * 4:(row + 1) * size * 4])
                   for row in range(size))

    def chunk(tag, payload):
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


if __name__ == "__main__":
    target = sys.argv[1]
    side = int(sys.argv[2]) if len(sys.argv) > 2 else 64
    with open(target, "wb") as handle:
        handle.write(png_bytes(side, draw(side)))
    print("图标：%s（%dx%d）" % (target, side, side))
