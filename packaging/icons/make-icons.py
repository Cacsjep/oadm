"""Draws the OADM app icon and writes oadm.png (512), oadm-256.png, oadm.ico and oadm.icns.

The icon files are committed; run this only to change the icon: python make-icons.py (needs Pillow).
Design: violet rounded square (accent #6C5CE7), white lens ring, teal center (#7FC8D0).
"""
import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ACCENT = (0x6C, 0x5C, 0xE7, 255)
TEAL = (0x7F, 0xC8, 0xD0, 255)
WHITE = (0xF2, 0xF2, 0xF2, 255)


def draw(size: int) -> Image.Image:
    scale = 4  # supersampling for smooth edges
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    margin = round(s * 0.06)
    d.rounded_rectangle([margin, margin, s - margin, s - margin], radius=round(s * 0.22), fill=ACCENT)
    c = s / 2
    r_out, r_in, r_dot = s * 0.29, s * 0.19, s * 0.09
    d.ellipse([c - r_out, c - r_out, c + r_out, c + r_out], fill=WHITE)
    d.ellipse([c - r_in, c - r_in, c + r_in, c + r_in], fill=ACCENT)
    d.ellipse([c - r_dot, c - r_dot, c + r_dot, c + r_dot], fill=TEAL)
    return img.resize((size, size), Image.LANCZOS)


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: str, sizes: list[int]) -> None:
    # ICO with PNG-compressed entries (Windows Vista and later).
    images = [png_bytes(draw(n)) for n in sizes]
    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    entries = b""
    for n, data in zip(sizes, images):
        dim = 0 if n >= 256 else n
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(header + entries + b"".join(images))


def write_icns(path: str) -> None:
    # PNG entries: icp4 16, icp5 32, icp6 64, ic07 128, ic08 256, ic09 512, ic10 1024 (512@2x),
    # ic11 32 (16@2x), ic12 64 (32@2x), ic13 256 (128@2x), ic14 512 (256@2x).
    types = [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512),
             ("ic10", 1024), ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)]
    body = b""
    for kind, n in types:
        data = png_bytes(draw(n))
        body += kind.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)


if __name__ == "__main__":
    draw(512).save(os.path.join(HERE, "oadm.png"), optimize=True)
    draw(256).save(os.path.join(HERE, "oadm-256.png"), optimize=True)
    write_ico(os.path.join(HERE, "oadm.ico"), [16, 24, 32, 48, 64, 128, 256])
    write_icns(os.path.join(HERE, "oadm.icns"))
