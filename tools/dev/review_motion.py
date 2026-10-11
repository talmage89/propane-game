#!/usr/bin/env python3
"""Compare 60 Hz player_range motion captures (Pillow, review only; not required by CI)."""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageOps


def frame(directories, name):
    result = Image.new("RGB", (480 * len(directories), 300), "#ddd")
    draw = ImageDraw.Draw(result)
    for column, (label, directory) in enumerate(directories):
        with Image.open(directory / f"{name}.png") as source:
            # Preserve the complete camera projection so viewport clipping remains visible.
            result.paste(ImageOps.contain(source.convert("RGB"), (480, 270)), (column * 480, 0))
        draw.text((column * 480 + 5, 276), f"{label}: {name}", fill="black")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--old-recoil", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    for group in ("walk", "run", "sprint", "creep", "hip", "aim", "reload", "getup"):
        names = sorted(p.stem for p in args.before.glob(f"{group}_*.png"))
        if not names:
            raise ValueError(f"missing {group} captures")
        directories = [("25mm/2deg", args.before), ("spring + sway", args.after)]
        if args.old_recoil and group in ("hip", "aim"):
            directories.insert(0, ("45mm/5deg", args.old_recoil))
        frames = [frame(directories, name) for name in names]
        frames[0].save(args.output / f"{group}.gif", save_all=True, append_images=frames[1:], duration=[(20, 20, 10)[i % 3] for i in range(len(frames))], loop=0)
        # Eight evenly spaced moments, including both ends of reload/get-up handoffs.
        indices = [i * (len(frames) - 1) // 7 for i in range(8)]
        sheet = Image.new("RGB", (frames[0].width, 300 * len(indices)), "#ddd")
        for row, index in enumerate(indices):
            sheet.paste(frames[index], (0, row * 300))
        sheet.save(args.output / f"{group}.jpg", quality=92)
        print(f"{group}: {len(frames)} frames")


if __name__ == "__main__":
    main()
