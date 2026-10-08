"""Builds the app and installer icon files from the OADM logo in /icon.

Writes oadm.png (512), oadm-256.png, oadm.ico (Windows exe, MSI, shortcuts) and oadm.icns (macOS app) from the
app icon PNGs `icon/oadm-app-icon-<size>.png` (16, 24, 32, 48, 64, 128, 256, 512, 1024). The
output files are committed; run this only after the logo changed: python make-icons.py (needs Pillow).
"""
import io
import os
import struct

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "..", "icon")


def logo(size: int) -> Image.Image:
    """The hand-made PNG of that size when there is one, else the next larger one scaled down."""
    path = os.path.join(SOURCE, f"oadm-app-icon-{size}.png")
    if os.path.exists(path):
        return Image.open(path).convert("RGBA")
    larger = min(n for n in (16, 24, 32, 48, 64, 128, 256, 512, 1024) if n > size)
    return logo(larger).resize((size, size), Image.LANCZOS)


def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: str, sizes: list[int]) -> None:
    # ICO with PNG-compressed entries (Windows Vista and later).
    images = [png_bytes(logo(n)) for n in sizes]
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
        data = png_bytes(logo(n))
        body += kind.encode("ascii") + struct.pack(">I", len(data) + 8) + data
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)


if __name__ == "__main__":
    logo(512).save(os.path.join(HERE, "oadm.png"), optimize=True)
    logo(256).save(os.path.join(HERE, "oadm-256.png"), optimize=True)
    # 20, 30, 36 and 40 px: taskbar and title bar at 125 % and 150 % display scaling.
    write_ico(os.path.join(HERE, "oadm.ico"), [16, 20, 24, 30, 32, 36, 40, 48, 64, 128, 256])
    write_icns(os.path.join(HERE, "oadm.icns"))
