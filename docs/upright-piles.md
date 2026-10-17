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
vent thrust, chain range/fuses, scoring, gathering and drops are unchanged. This changed shared suburb construction
and added a shared tuner, so it needed a minor release: it shipped in 0.3.0 within Protocol 2.

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

The dated Linux stability and soak review is in the [upright pile review](evidence/upright-piles-review.md).
