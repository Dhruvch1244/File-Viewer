"""Regenerates src/FileViewer.App/Assets/AppIcon.ico with a bolder mark and a full size set.

The old icon read fine at 256x256 but collapsed into an indistinct blob at 16x16 — the two
bars and the gap between them were too thin relative to the icon to survive being small. It
also only embedded 16/32/48/256, so Windows had to scale for every taskbar/title-bar size in
between (commonly 20, 24, 40, 64, 96 at various DPI settings), which is its own source of
blur regardless of how sharp the source frames are.

Each size is drawn independently at 8x supersample and downsampled with LANCZOS, rather than
resizing one 256px master down — a mark this simple (a rounded square, two bars) loses far
less fidelity redrawn at each target size than shrunk repeatedly from one master. The ICO
container is written by hand (PNG-compressed frames, which Vista+ and every version of
Windows this app targets supports — the previous icon used the same scheme) because
Pillow's own ICO writer resizes a single source image internally and silently drops any
`sizes` entry larger than whatever image you called .save() on, discarding hand-tuned frames
in the process.

Run from the repository root:

    python tools/generate_app_icon.py

Requires Pillow (`pip install Pillow`).
"""
import struct

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
SUPERSAMPLE = 8

BG = (16, 16, 16, 255)      # near-black squircle, matches the app's dark accent
FG = (255, 255, 255, 255)   # white bars


def draw_mark(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    radius = int(s * 0.24)
    draw.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=BG)

    margin_x = int(s * 0.24)
    bar_height = int(s * 0.16)
    gap = int(s * 0.12)
    total_h = bar_height * 2 + gap
    top = (s - total_h) // 2

    draw.rectangle([margin_x, top, s - margin_x, top + bar_height], fill=FG)
    bottom_width = int((s - 2 * margin_x) * 0.62)
    draw.rectangle(
        [margin_x, top + bar_height + gap, margin_x + bottom_width, top + bar_height + gap + bar_height],
        fill=FG,
    )

    return img.resize((size, size), Image.LANCZOS)


def write_ico(path: str, frames: list[Image.Image]) -> None:
    import io

    entries = []
    image_data = []
    offset = 6 + 16 * len(frames)
    for frame in frames:
        buf = io.BytesIO()
        frame.save(buf, format="PNG")
        png_bytes = buf.getvalue()
        w = frame.width if frame.width < 256 else 0
        h = frame.height if frame.height < 256 else 0
        entries.append(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(png_bytes), offset))
        image_data.append(png_bytes)
        offset += len(png_bytes)

    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(frames)))
        for entry in entries:
            f.write(entry)
        for data in image_data:
            f.write(data)


if __name__ == "__main__":
    frames = [draw_mark(sz) for sz in SIZES]
    write_ico("src/FileViewer.App/Assets/AppIcon.ico", frames)
    print("wrote AppIcon.ico with sizes:", [f.size for f in frames])
