"""
Icon build pipeline for Audio Integrity Checker.

For each Windows ICO size: prefer a hand-crafted PNG from sources/ when
present (pixel-perfect for small sizes), otherwise render the SVG via
ImageMagick. Pack the eight rasters into icon.ico at the project root.

Usage:
    uv run --with pillow python _icon_build/build_icon.py

Requires `magick` (ImageMagick 7+) on PATH for SVG rasterization.
"""

from __future__ import annotations

import io
import struct
import subprocess
import sys
from pathlib import Path

from PIL import Image

OUT_DIR = Path(__file__).resolve().parent
SOURCE_DIR = OUT_DIR / "sources"
PNG_DIR = OUT_DIR / "pngs"
PNG_DIR.mkdir(exist_ok=True)

ROOT = OUT_DIR.parent
SVG_PATH = SOURCE_DIR / "icon.svg"
ICO_PATH = ROOT / "icon.ico"

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]


def render_svg_to_png(svg: Path, size: int, out: Path) -> None:
    """Rasterize the SVG with ImageMagick at exactly size×size pixels.
    -background none preserves alpha; -density 384 oversamples the SVG
    rendering and -resize Box downsamples cleanly without smearing edges."""
    cmd = [
        "magick",
        "-density", "384",
        "-background", "none",
        str(svg),
        "-resize", f"{size}x{size}",
        "-define", f"png:color-type=6",
        str(out),
    ]
    subprocess.run(cmd, check=True, capture_output=True)


def build_ico(images: list[tuple[int, Image.Image]], out_path: Path) -> None:
    """Build a .ico with the given (size, PIL image) entries.
    256x256 is stored as PNG, smaller sizes as 32bpp BGRA BMP with AND mask."""
    entries_data: list[bytes] = []
    entries_sizes: list[int] = []

    for size, img in images:
        if img.size != (size, size):
            img = img.resize((size, size), Image.LANCZOS)
        if size >= 256:
            buf = io.BytesIO()
            img.save(buf, "PNG")
            data = buf.getvalue()
        else:
            data = encode_bmp_for_ico(img)
        entries_data.append(data)
        entries_sizes.append(size)

    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    dir_entries = b""
    for size, data in zip(entries_sizes, entries_data):
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size
        dir_entries += struct.pack(
            "<BBBBHHII",
            w, h, 0, 0, 1, 32, len(data), offset,
        )
        offset += len(data)

    with open(out_path, "wb") as f:
        f.write(header)
        f.write(dir_entries)
        for data in entries_data:
            f.write(data)


def encode_bmp_for_ico(img: Image.Image) -> bytes:
    """Encode a 32bpp BGRA DIB (BITMAPINFOHEADER) with AND mask appended.
    Height in header is doubled (image+mask), pixel data is bottom-up."""
    if img.mode != "RGBA":
        img = img.convert("RGBA")
    w, h = img.size
    pixels = img.load()

    xor_rows = []
    for y in range(h - 1, -1, -1):
        row = bytearray()
        for x in range(w):
            r, g, b, a = pixels[x, y]
            row.extend([b, g, r, a])
        xor_rows.append(bytes(row))
    xor_data = b"".join(xor_rows)

    row_bytes = ((w + 31) // 32) * 4
    and_rows = []
    for y in range(h - 1, -1, -1):
        bits = bytearray(row_bytes)
        for x in range(w):
            _, _, _, a = pixels[x, y]
            if a == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        and_rows.append(bytes(bits))
    and_data = b"".join(and_rows)

    header = struct.pack(
        "<IiiHHIIiiII",
        40,             # biSize
        w,              # biWidth
        h * 2,          # biHeight (xor + and)
        1,              # biPlanes
        32,             # biBitCount
        0,              # biCompression BI_RGB
        len(xor_data),  # biSizeImage
        0, 0, 0, 0,
    )
    return header + xor_data + and_data


def resolve_input(size: int) -> tuple[Path, str]:
    """Pick the per-size source: hand-crafted PNG if present, SVG otherwise."""
    handcrafted = SOURCE_DIR / f"icon_{size}.png"
    if handcrafted.exists():
        return handcrafted, "handcrafted"
    if not SVG_PATH.exists():
        sys.exit(f"no input for size {size}: {handcrafted} missing and {SVG_PATH} missing")
    return SVG_PATH, "svg"


def main() -> None:
    rendered: list[tuple[int, Image.Image]] = []
    for size in SIZES:
        out_png = PNG_DIR / f"icon_{size}.png"
        source, kind = resolve_input(size)
        if kind == "handcrafted":
            img = Image.open(source).convert("RGBA")
            if img.size != (size, size):
                sys.exit(
                    f"{source} is {img.size[0]}x{img.size[1]} but should be {size}x{size}"
                )
            img.save(out_png, "PNG")
        else:
            render_svg_to_png(source, size, out_png)
            img = Image.open(out_png).convert("RGBA")
        print(f"  {size:>3}px  <-  {kind:<11}  ({source.name})")
        rendered.append((size, img))

    build_ico(rendered, ICO_PATH)
    print(f"wrote {ICO_PATH} ({ICO_PATH.stat().st_size} bytes, {len(SIZES)} sizes)")
    print(f"PNGs in {PNG_DIR}")


if __name__ == "__main__":
    main()
