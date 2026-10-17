#!/usr/bin/env python3
"""Combine the R19 burst CSVs and plot pitch/yaw curves (optional matplotlib required)."""
import argparse
import csv
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baselines", type=Path, required=True)
    parser.add_argument("--branch", type=Path, required=True, help="directory containing rcl-branch-{stance}/recoil.csv")
    parser.add_argument("--output", type=Path, default=Path("docs/evidence/recoil"))
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    versions = ["v0.2.0", "dev (8eafd3c)", "R19"]
    stances = ["aim", "hip", "creep"]
    series = {}
    fields = ["version", "stance", "time", "pitch", "yaw", "shots", "angle", "render_angle", "ready_angle", "muzzle_error"]
    with (args.output / "curves.csv").open("w", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=fields)
        writer.writeheader()
        for version, label in zip(versions, ["v020", "dev", "branch"]):
            for stance in stances:
                source = (args.branch / f"rcl-branch-{stance}" / "recoil.csv" if label == "branch"
                          else args.baselines / f"{label}-{stance}.csv")
                with source.open() as data:
                    rows = list(csv.DictReader(data))
                shots = [row for row in rows if row["angle"]]
                assert len(shots) == 30, f"{source}: incomplete magazine"
                frames = [row for row in rows if not row["angle"]]
                start_pitch, start_yaw = float(frames[0]["pitch"]), float(frames[0]["yaw"])
                series[version, stance] = frames
                last_shot = float(shots[-1]["time"])
                settled = min(frames, key=lambda row: abs(float(row["time"]) - last_shot - 5))
                peak = max(float(row["pitch"]) for row in frames) - start_pitch
                retained = float(settled["pitch"]) - start_pitch
                yaw_peak = max((float(row["yaw"]) - start_yaw for row in frames), key=abs)
                yaw_after = float(settled["yaw"]) - start_yaw
                angle = max(float(row["angle"]) for row in shots)
                print(f"| {version} | {stance} | {peak:.3f} | {retained:.3f} | {peak - retained:.3f} | {yaw_peak:.3f} | {yaw_after:.3f} | {angle:.3f} |")
                for row in rows:
                    writer.writerow({"version": version, "stance": stance, **row})

    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    figure, axes = plt.subplots(2, 3, figsize=(13, 7), sharex=True, constrained_layout=True)
    colors = ["#4169a1", "#b94a48", "#21855d"]
    for column, stance in enumerate(stances):
        for version, color in zip(versions, colors):
            frames = series[version, stance]
            times = [float(row["time"]) for row in frames]
            for index, value in enumerate(["pitch", "yaw"]):
                initial = float(frames[0][value])
                axes[index, column].plot(times, [float(row[value]) - initial for row in frames],
                                         label=version, color=color, linewidth=1.3)
        axes[0, column].set_title({"aim": "Aimed, stationary", "hip": "Hip fire, stationary", "creep": "Aimed creep"}[stance])
        for row in range(2):
            axes[row, column].axvspan(1, 3.4, color="#bbbbbb", alpha=0.2)
            axes[row, column].grid(alpha=0.25)
            axes[row, column].set_xlim(0, 9.7)
        axes[1, column].set_xlabel("Time (seconds)")
    axes[0, 0].set_ylabel("Aim pitch climb (degrees)")
    axes[1, 0].set_ylabel("Aim yaw change (degrees)")
    axes[0, 0].legend(loc="upper left")
    figure.suptitle("R19: 30 rounds, 13 shots/s, no look input; gray interval contains the burst")
    figure.savefig(args.output / "climb.png", dpi=160)
    plt.close(figure)


if __name__ == "__main__":
    main()
