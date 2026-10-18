#!/usr/bin/env python3
"""Turn a numbered frame sequence into the README's looping GIF.

Needs ffmpeg on PATH and Pillow (python3 -m pip install pillow). Frames are the PNGs the game test saves
(`frame_0000.png`, ...). Each one is cropped and scaled, then pixels that changed by less than --gate levels keep the
previous frame's value, which is what lets a GIF of a still camera shrink to a few megabytes. The last frames fade to
white (the void's color), softening the cut back to the start. The first frame is never faded: GitHub shows it until the
GIF has loaded, and keeps showing it when a viewer has animation autoplay off. ffmpeg builds one palette for the clip.
The full recipe, with the capture commands, is in docs/development.md.
"""
import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageChops

# GitHub loads large README images slowly or not at all (a 5.5 MB GIF once failed to appear); 3 MB loads reliably.
MAX_BYTES = 4_000_000


def gated(frame, previous, gate):
    """Keeps every pixel of `previous` that `frame` changed by less than `gate` in each channel."""
    if previous is None or gate <= 0:
        return frame
    r, g, b = ImageChops.difference(frame, previous).split()
    change = ImageChops.lighter(ImageChops.lighter(r, g), b)
    return Image.composite(frame, previous, change.point(lambda level: 255 if level >= gate else 0))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("frames", type=Path, help="directory holding frame_NNNN.png")
    parser.add_argument("output", type=Path, help="the GIF to write")
    parser.add_argument("--first", type=int, default=0, help="number of the first frame to use (default 0)")
    parser.add_argument("--last", type=int, help="number of the last frame to use (default: the last one)")
    parser.add_argument("--fps", type=int, default=25, help="playback rate; use a divisor of 100 (default 25)")
    parser.add_argument("--width", type=int, default=640, help="width in pixels (default 640)")
    parser.add_argument("--crop", help="x,y,w,h of the source to keep, in source pixels (default: all of it)")
    parser.add_argument("--gate", type=int, default=8, help="changes below this many levels are dropped (default 8; 0 keeps all)")
    parser.add_argument("--colors", type=int, default=128, help="palette size (default 128)")
    parser.add_argument("--fade", type=int, default=5, help="frames at the end that fade to white (default 5)")
    args = parser.parse_args()

    names = sorted(args.frames.glob("frame_*.png"))
    numbered = {int(path.stem.split("_")[1]): path for path in names}
    if not numbered:
        sys.exit(f"no frame_NNNN.png in {args.frames}")
    last = args.last if args.last is not None else max(numbered)
    wanted = [number for number in range(args.first, last + 1) if number in numbered]
    if len(wanted) != last - args.first + 1 or len(wanted) <= args.fade + 1:
        sys.exit(f"frames {args.first}..{last} are missing or too few for a {args.fade}-frame fade")
    box = tuple(int(v) for v in args.crop.split(",")) if args.crop else None
    if box is not None:
        box = (box[0], box[1], box[0] + box[2], box[1] + box[3])

    with tempfile.TemporaryDirectory(prefix="readme-gif-") as scratch:
        previous = None
        for index, number in enumerate(wanted):
            with Image.open(numbered[number]) as source:
                frame = source.convert("RGB")
            if box is not None:
                frame = frame.crop(box)
            frame = frame.resize((args.width, round(frame.height * args.width / frame.width)), Image.LANCZOS)
            frame = previous = gated(frame, previous, args.gate)
            weight = min(len(wanted) - index, args.fade + 1) / (args.fade + 1)
            if weight < 1:
                frame = Image.blend(Image.new("RGB", frame.size, "white"), frame, weight)
            frame.save(Path(scratch) / f"f_{index:04d}.png")

        graph = (f"split[a][b];[a]palettegen=stats_mode=diff:max_colors={args.colors}[p];"
                 "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        command = ["ffmpeg", "-v", "error", "-y", "-framerate", str(args.fps), "-i", str(Path(scratch) / "f_%04d.png"),
                   "-vf", graph, "-loop", "0", str(args.output)]
        subprocess.run(command, check=True)

    size = args.output.stat().st_size
    print(f"{args.output}: {len(wanted)} frames at {args.fps} fps ({len(wanted) / args.fps:.1f} s), {size / 1e6:.2f} MB")
    if size > MAX_BYTES:
        sys.exit(f"{args.output} is over {MAX_BYTES / 1e6:.0f} MB: lower --width, --colors or the frame count, or raise --gate")


if __name__ == "__main__":
    main()
