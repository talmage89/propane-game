# Upright pyramids

R3's square and triangular pyramids stand upright. Rows remain side-laid and gathering still rebuilds upright
clusters. The pyramid lattice, bottom-first order and two-support offsets are unchanged: each upper foot spans
two lower collars in the same row. The existing convex hull has a flat 0.104 m foot radius and a flat 0.09 m collar
radius, 0.462 m above the foot. At the default 0.36 m across pitch those surfaces overlap on both sides of the foot.
`PileUprightLayerHeight` sets that rise independently of the side-laid rows' `PileLayerHeight`.

Suburb tanks already start asleep. They retain ordinary rigid-body contacts, gravity and automatic waking; there
are no joints, frozen tiers, replacement collision shapes or support props. Awake rendered trials also stand on
these contacts. The collar contact is narrow, so a displaced support or a venting upper tank can tip its neighbors.
A local hit can leave some tiers standing; a blast throws the pile apart. A collar hit can vent mostly upward and
leave the stack standing until a later disturbance. This follows the existing shot/jet physics.

The vision's rules remain: **“The first punctures a tank: it vents fire from the hole, forever”**, **“The second shot
blows it up”**, and **“A tank close to a blast takes it as a shot”** ([VISION](../design/VISION.md)). Hulls, impulses,
vent thrust, chain range/fuses, scoring, gathering and drops are unchanged. This changes shared suburb construction
and adds a shared tuner, so the next release must be minor. The unreleased `Protocol.Version` remains 2.

## Reproduce

After `make build`, use the Godot .NET executable on PATH:

```sh
godot-mono --headless --fixed-fps 60 res://scenes/dev/upright_pile_tests.tscn
godot-mono --headless --fixed-fps 60 res://scenes/dev/upright_pile_tests.tscn -- --deep --awake
for shape in Square Triangular; do
  godot-mono --fixed-fps 60 --resolution 1280x720 res://scenes/dev/tank_range.tscn -- \
    --formation="$shape" --hit=shot --capture-dir="$PWD/build/upr-$shape-shot"
done
```

The headless probe uses forced square/triangular four-player plans at seeds 1, 23 and 47, plus full and partial
bases at counts 4, 5, 6, 10, 14 and 30. Every physics tick for at least 120 simulated seconds must keep every tank
within 6 mm of its placed origin and 0.5 degrees of vertical. A real scripted player walks past every pile;
two loose neighbors must gather beside a built pile without recruiting its members. Shell shots, base detonations
and intact shoves then must tip a tank in every multi-tier fixture beyond 30 degrees within ten simulated seconds.
The probe measures peak tilt during that interval, since a tumbling tank can finish upright again.
It then keeps the intact shove fixtures, shoves another upper tank if needed, and clears blast fragments and remaining
standing supports to provide clear departure paths. By 180 seconds at least two original members of each shape must
have gathered and rested intact. No gather delay or motion values are overridden. Unchanged blocked-path behavior
is covered by `gather_range`; a partial pile can keep a loose tank waiting until its path clears.
`--deep` adds counts 64 and 128; `--awake` starts the tanks awake to test contact settling.
`make test` runs the normal probe; `make test-full` adds the deep awake trial.

The range's camera is at 1.8 m, near a standing player's height. Captures show placement, four-second rest, the
shell hit, two falling frames and aftermath. `--hit=blast` or `--hit=shove` checks those disturbances;
`--awake` tests contacts without initial sleep. Existing `--chain=fresh` and `--chain=primed` still assert that a
blast acts as one shot and that primed neighbors wait for their fuses. Default spacing is the verified configuration;
live spacing/rise changes can remove the narrow contacts and are not covered by the stability proof.

## Linux review, 2026-10-11

The normal probe held 49 piles / 354 tanks for 120 simulated seconds with 60 real player passes: maximum drift
0.451 mm and tilt 0.086 degrees. The deep awake probe held 53 piles / 738 tanks, including both 128-tank shapes:
1.669 mm drift and 0.151 degrees. Both exceeded the 30-degree toppling threshold in every disturbed multi-tier
fixture. After site clearance, nine original tanks regathered in the normal run and eight in the deep awake run.
Headless `tank_range` runs with `--chain=fresh` and `--chain=primed` passed for both shapes at count 14:
all 13 neighbors kept venting after one blast, or detonated only after their fuses when already primed.

Rendered trials at 1280×720 used the range's 1.8 m camera. Awake square (14 tanks) and triangular (14 and 20 tanks)
contact trials remained still; shell hits, an intact shove and a triangular base blast produced physical falls.
The full triangular shell-hit sequence is particularly clear: four tiers stand, the top vents sideways, then tanks
roll down the front and off the side. Captures are retained locally under `build/upr-*`.

The four-bot 90-second `--melee-start` soak at 120 ms lag / 30 ms jitter / 3% loss recorded 14 accepted swings and
seven player contacts. `check_scores.py` passed: 29 map gains + 13 drop gains - 23 spills = 19 banked, agreed by all
four clients. `compare_tanks.py` completed 424 comparisons: final ids/states agreed and final drift was at most
0.340 m; active-chain differences peaked at ten states and 6.523 m. This is a comparison diagnostic, not a drift
threshold assertion. These results do not change body synchronization. Windows and macOS were not tested locally.
