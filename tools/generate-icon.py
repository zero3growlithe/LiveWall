#!/usr/bin/env python3
"""Generate src/LiveWall/app.ico: a monitor glyph with a sun and a mountain, wallpaper style.

Renders every size natively at 8x and downsamples, then assembles the ICO container by hand
(ICONDIR + 16-byte entries + PNG payloads) so each entry keeps its own quality.
"""

import os
import struct

from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 8

BACKGROUND_TOP = (32, 108, 168, 255)
BACKGROUND_BOTTOM = (18, 60, 104, 255)
SCREEN_FILL = (12, 20, 32, 255)
SUN_COLOR = (255, 198, 88, 255)
MOUNTAIN_BACK = (58, 128, 150, 255)
MOUNTAIN_FRONT = (30, 88, 112, 255)


def blend(color_a, color_b, factor):
    return tuple(int(round(color_a[index] + (color_b[index] - color_a[index]) * factor)) for index in range(4))


def render(size):
    canvas_size = size * SUPERSAMPLE
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))

    # Rounded background with a vertical gradient.
    gradient = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    gradient_draw = ImageDraw.Draw(gradient)
    for row in range(canvas_size):
        factor = row / max(canvas_size - 1, 1)
        gradient_draw.line([(0, row), (canvas_size, row)], fill=blend(BACKGROUND_TOP, BACKGROUND_BOTTOM, factor))

    mask = Image.new("L", (canvas_size, canvas_size), 0)
    mask_draw = ImageDraw.Draw(mask)
    margin = int(canvas_size * 0.045)
    mask_draw.rounded_rectangle(
        [margin, margin, canvas_size - margin - 1, canvas_size - margin - 1],
        radius=int(canvas_size * 0.22),
        fill=255,
    )
    image.paste(gradient, (0, 0), mask)

    # The "screen" inside the bezel.
    inner = int(canvas_size * 0.17)
    bezel = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    bezel_draw = ImageDraw.Draw(bezel)
    bezel_draw.rounded_rectangle(
        [inner, inner, canvas_size - inner - 1, canvas_size - inner - 1],
        radius=int(canvas_size * 0.10),
        fill=SCREEN_FILL,
    )

    # Sun.
    sun_radius = int(canvas_size * 0.085)
    sun_center = (int(canvas_size * 0.62), int(canvas_size * 0.37))
    bezel_draw.ellipse(
        [sun_center[0] - sun_radius, sun_center[1] - sun_radius, sun_center[0] + sun_radius, sun_center[1] + sun_radius],
        fill=SUN_COLOR,
    )

    # Two mountain ridges, clipped by the screen rectangle.
    screen_box = [inner, inner, canvas_size - inner - 1, canvas_size - inner - 1]
    ridge = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    ridge_draw = ImageDraw.Draw(ridge)
    ridge_draw.polygon(
        [
            (inner, canvas_size - inner - 1),
            (int(canvas_size * 0.28), int(canvas_size * 0.50)),
            (int(canvas_size * 0.48), int(canvas_size * 0.74)),
            (int(canvas_size * 0.66), int(canvas_size * 0.46)),
            (canvas_size - inner - 1, canvas_size - inner - 1),
        ],
        fill=MOUNTAIN_BACK,
    )
    ridge_draw.polygon(
        [
            (inner, canvas_size - inner - 1),
            (int(canvas_size * 0.36), int(canvas_size * 0.66)),
            (int(canvas_size * 0.56), int(canvas_size * 0.84)),
            (int(canvas_size * 0.80), int(canvas_size * 0.60)),
            (canvas_size - inner - 1, canvas_size - inner - 1),
        ],
        fill=MOUNTAIN_FRONT,
    )

    clipped_ridge = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    screen_mask = Image.new("L", (canvas_size, canvas_size), 0)
    ImageDraw.Draw(screen_mask).rounded_rectangle(screen_box, radius=int(canvas_size * 0.10), fill=255)
    clipped_ridge.paste(ridge, (0, 0), screen_mask)

    bezel.paste(clipped_ridge, (0, 0), clipped_ridge)
    image.paste(bezel, (0, 0), bezel)

    return image.resize((size, size), Image.LANCZOS)


def write_ico(images, path):
    payloads = []
    for image in images:
        buffer = []
        image.save(_BytesSink(buffer), format="PNG")
        payloads.append(b"".join(buffer))

    header = struct.pack("<HHH", 0, 1, len(images))
    entries = b""
    offset = 6 + 16 * len(images)
    for image, payload in zip(images, payloads):
        width = 0 if image.width >= 256 else image.width
        height = 0 if image.height >= 256 else image.height
        entries += struct.pack("<BBBBHHII", width, height, 0, 0, 1, 32, len(payload), offset)
        offset += len(payload)

    with open(path, "wb") as handle:
        handle.write(header + entries + b"".join(payloads))


class _BytesSink:
    def __init__(self, target):
        self._target = target

    def write(self, data):
        self._target.append(data)

    def flush(self):
        return None


def main():
    images = [render(size) for size in SIZES]
    output = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "LiveWall", "app.ico")
    write_ico(images, output)
    print("wrote", output, os.path.getsize(output), "bytes")


if __name__ == "__main__":
    main()
