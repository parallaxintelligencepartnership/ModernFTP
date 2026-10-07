#!/usr/bin/env python3
"""Draws the ModernFTP tray icon (16 and 32 px) from scratch and writes ModernFTP.ico beside this script.

Design: a blue rounded tile with a white up arrow and down arrow (transfer). No third party artwork.
Usage: python3 make-icon.py
"""
import math
import struct
from pathlib import Path

SS = 8  # supersampling factor


def inside_round_rect(x, y, size, radius):
    cx = min(max(x, radius), size - radius)
    cy = min(max(y, radius), size - radius)
    return (x - cx) ** 2 + (y - cy) ** 2 <= radius ** 2


def in_arrow(x, y, size):
    # Coordinates in 0..1 space.
    u, v = x / size, y / size
    # The up arrow occupies u in [0.14, 0.48].
    if not (0.14 <= u <= 0.48):
        return False
    mid = 0.31
    head_top, head_bottom = 0.18, 0.46
    if head_top <= v <= head_bottom:
        half = (v - head_top) / (head_bottom - head_top) * 0.17
        return abs(u - mid) <= half
    if head_bottom < v <= 0.82:
        return abs(u - mid) <= 0.065
    return False


def pixel(px, py, size):
    total = [0.0, 0.0, 0.0, 0.0]
    for sy in range(SS):
        for sx in range(SS):
            x = px + (sx + 0.5) / SS
            y = py + (sy + 0.5) / SS
            if not inside_round_rect(x, y, size, size * 0.22):
                continue
            t = y / size
            color = (0x1F + 0x20 * t, 0x6F + 0x25 * t, 0xD6 + 0x10 * t)  # blue gradient
            # Up arrow on the left, the same arrow rotated half a turn (down) on the right.
            if in_arrow(x, y, size) or in_arrow(size - x, size - y, size):
                color = (255, 255, 255)
            total[0] += color[0]
            total[1] += color[1]
            total[2] += color[2]
            total[3] += 255
    n = SS * SS
    a = total[3] / n
    if a == 0:
        return (0, 0, 0, 0)
    scale = n * 255 / total[3] if total[3] else 1
    return (round(total[0] / n * scale), round(total[1] / n * scale), round(total[2] / n * scale), round(a))


def dib(size):
    rows = []
    for py in range(size - 1, -1, -1):  # bottom up
        row = bytearray()
        for px in range(size):
            r, g, b, a = pixel(px, py, size)
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    mask_stride = ((size + 31) // 32) * 4
    mask = bytes(mask_stride * size)  # all zero: alpha channel decides
    header = struct.pack('<IiiHHIIiiII', 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return header + b''.join(rows) + mask


def main():
    sizes = [16, 32]
    images = [dib(s) for s in sizes]
    out = bytearray(struct.pack('<HHH', 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for s, img in zip(sizes, images):
        out += struct.pack('<BBBBHHII', s, s, 0, 0, 1, 32, len(img), offset)
        offset += len(img)
    for img in images:
        out += img
    target = Path(__file__).with_name('ModernFTP.ico')
    target.write_bytes(bytes(out))
    print(f'wrote {target} ({len(out)} bytes)')


if __name__ == '__main__':
    main()
