"""Builds the MSI wizard assets: License.rtf (from /LICENSE), Banner.bmp (493x58) and Dialog.bmp (493x312).

The outputs are committed; run this after LICENSE or the app icon changed: python make-ui-assets.py (needs Pillow).
The WiX dialogs draw black text on the white part of the bitmaps, so the brand color stays at the edges.
"""
import os

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "..")
ICON = os.path.join(ROOT, "icon", "oadm-app-icon-128.png")
VIOLET = (0x5C, 0x2E, 0x91)


def license_rtf() -> None:
    text = open(os.path.join(ROOT, "LICENSE"), encoding="utf-8").read()
    body = text.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}")
    body = "".join(ch if ord(ch) < 128 else f"\\u{ord(ch)}?" for ch in body)
    body = body.replace("\r\n", "\n").replace("\n", "\\par\n")
    rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Segoe UI;}}\\fs16\n" + body + "}\n"
    open(os.path.join(HERE, "License.rtf"), "w", encoding="ascii", newline="\n").write(rtf)


def banner() -> None:
    # Top banner of the inner dialogs: white (title text area), icon on the right.
    img = Image.new("RGB", (493, 58), "white")
    icon = Image.open(ICON).convert("RGBA").resize((44, 44), Image.LANCZOS)
    img.paste(icon, (493 - 44 - 8, 7), icon)
    ImageDraw.Draw(img).line([(0, 57), (493, 57)], fill=(220, 220, 220))
    img.save(os.path.join(HERE, "Banner.bmp"))


def dialog() -> None:
    # Welcome and finish pages: violet panel with the icon on the left 164 px, white text area on the right.
    img = Image.new("RGB", (493, 312), "white")
    ImageDraw.Draw(img).rectangle([0, 0, 163, 311], fill=VIOLET)
    icon = Image.open(ICON).convert("RGBA").resize((112, 112), Image.LANCZOS)
    img.paste(icon, (26, 100), icon)
    img.save(os.path.join(HERE, "Dialog.bmp"))


if __name__ == "__main__":
    license_rtf()
    banner()
    dialog()
