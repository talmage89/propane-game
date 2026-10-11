#!/usr/bin/env python3
"""Pair player_range captures into contact sheets and animated comparisons (requires Pillow)."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageOps


EVENTS = {
    "start-stop": (6, 8, 11, 24, 26, 28, 31, 34),
    "reverse-strafe": (28, 32, 36, 40, 52, 55, 58, 62),
    "turn": (78, 86, 90, 91, 93, 94, 98, 102),
    "gaits": (108, 111, 115, 119, 123, 127, 130, 134),
    "jump-aim": (139, 140, 143, 146, 150, 152, 154, 158),
    "jump-free": (173, 175, 178, 181, 185, 187, 188, 189),
    "terrain": (195, 200, 205, 210, 215, 220, 225, 230),
    "split-uphill": (242, 244, 246, 260, 266, 272, 284, 290),
}
# Sample a stride rather than aliasing the faster cycles with the creep's wider frame spacing.
STRIDE_FRAMES = {"Creep": (0, 3, 6, 9), "Default": (0, 1, 3, 5), "Sprint": (0, 2, 4, 6)}


def tile(path, view, events):
    with Image.open(path) as source:
        image = source.convert("RGB")
    # The harness uses a fixed review camera. Keep its world grid: a moving crop must not hide drift.
    if view == "side":
        box = (650, 100, 1350, 1040) if events else (650, 180, 1350, 1010)
    else:
        box = (510, 240, 1300, 1040) if events else (420, 330, 1300, 1042)
    scale_x, scale_y = image.width / 1920, image.height / 1042
    box = tuple(round(value * (scale_x if i % 2 == 0 else scale_y)) for i, value in enumerate(box))
    return ImageOps.contain(image.crop(box), (300, 390))


def pair(before, after, name, view, events):
    frame = Image.new("RGB", (600, 420), "#ddd")
    draw = ImageDraw.Draw(frame)
    for column, (version, directory) in enumerate((("before", before), ("after", after))):
        frame.paste(tile(directory / f"{name}.png", view, events), (column * 300, 0))
        draw.text((column * 300 + 4, 396), f"{version} {name}", fill="black")
    return frame


def sheet(before, after, names, destination, view, events):
    image = Image.new("RGB", (1200, 420 * ((len(names) + 1) // 2)), "#ddd")
    for i, name in enumerate(names):
        image.paste(pair(before, after, name, view, events), ((i % 2) * 600, (i // 2) * 420))
    image.save(destination, quality=94)


def animation(before, after, names, destination, view, events, duration):
    frames = [pair(before, after, name, view, events) for name in names]
    frames[0].save(destination, save_all=True, append_images=frames[1:], duration=duration, loop=0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--scenario", choices=("feet", "feet-events"), default="feet")
    parser.add_argument("--view", choices=("side", "player"), default="side")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    if args.scenario == "feet":
        for gait in ("Default", "Sprint", "Creep"):
            for group in range(2):
                names = [f"{gait}_{angle:03}_{frame:02}" for angle in range(group * 180, group * 180 + 180, 45)
                         for frame in STRIDE_FRAMES[gait]]
                sheet(args.before, args.after, names, args.output / f"{args.view}-{gait}-{group}.jpg", args.view, False)
        for gait, angle in (("Creep", 90), ("Creep", 180), ("Default", 90), ("Sprint", 0)):
            animation(args.before, args.after, [f"{gait}_{angle:03}_{frame:02}" for frame in range(25)],
                      args.output / f"{args.view}-{gait}-{angle:03}.gif", args.view, False, 67)
    else:
        for label, frames in EVENTS.items():
            sheet(args.before, args.after, [f"event_{frame:03}" for frame in frames],
                  args.output / f"{args.view}-event-{label}.jpg", args.view, True)
        for label, first, last in (("turn", 78, 106), ("jump-aim", 137, 160), ("terrain", 195, 230)):
            animation(args.before, args.after, [f"event_{frame:03}" for frame in range(first, last + 1)],
                      args.output / f"{args.view}-event-{label}.gif", args.view, True, 83)
    print(args.output)


if __name__ == "__main__":
    main()
