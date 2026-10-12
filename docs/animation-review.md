# Animation review

This is the acceptance harness for the authored directional rifle redo.
The fixture attaches a local view for A4's separated camera ownership. The strict gate is enabled in CI; numerical checks support the owner's rendered review, not replace it.

## Controller review

```sh
make build
mkdir -p build/animation-review
godot-mono --headless --fixed-fps 60 --path . res://scenes/dev/animation_review.tscn -- --cases=walk_000,jog_000,creep_000,creep_180,creep_090,sprint,aim_idle --metrics=$PWD/build/animation-review/metrics.csv
python3 -B tools/dev/animation_metrics.py build/animation-review/metrics.csv --output build/animation-review/results.json
python3 -B tools/dev/animation_review.py --build release=v0.2.0 --build dev=dev --build candidate=wp/anim-1 --output build/animation-review
```

The last command archives each commit into ignored scratch projects, overlays only the maintained review scene,
probe, capture helper and dev argument helper, imports and builds, then renders both views through `godot-mono`. Labels identify runs;
commit and harness hashes are recorded. No checkout switches or asset replacements occur. Outputs stay under this
checkout's `build/`. Pillow and ffmpeg are optional review dependencies; headless tests need only Python's standard
library and Godot. No downloads or recordings are required by CI.

Each three-second case starts at the origin. Scripted controller intent moves between 0.25 and 2.65 seconds, including acceleration
and stopping. Walk uses 0.25 analog input (1.5 m/s); jog uses full input (6 m/s); aimed creep is 1.6 m/s. Directions
are 0 forward, 45 forward/right, 90 right, 135 back/right, 180 back, 225 back/left, 270 left, 315 forward/left.
The original 34-case matrix includes idle, those 24 combinations, sprint, start/stop, a continuous aimed reversal,
stationary half-turn and return, jumping while holding aim, a burst, tactical reload, melee and stationary aim.
Two additional cases cover the first carry-to-fire transition (`hip_burst`) and a true 1.5 m/s walk
start/stop (`start_stop_walk`). The original `start_stop` accelerates to the full 6 m/s run.
Teleports separate cases; they are not pivots. Reload starts with 20 rounds. The release has no melee; its slot is
explicitly marked unavailable. Only its review fixture normalizes the older 3.6 m/s aim speed to 1.6 m/s.
A separate `--cases=walk_000 --assert-motion --assert-spawn` gate observes the first 200 ms of a cold spawn,
requiring head pitch at most 30 degrees at the default camera pitch. Settled gait windows cannot catch an
uninitialized rig looking at the world origin. Device delivery is checked separately by
`menu_test --scenario=input`, which injects real keyboard and mouse events.

The recoil scene's `--assert-first-hip` checks the six arm joints' first-shot change (at most 15 degrees) and
unchanged trigger timing at the default −10-degree pitch. It does not cover steep pitch limits. The ordinary
motion probe exempts arms during exact barrel tracking, although its reported maximum still includes them;
that exemption cannot establish a smooth first-shot transition. Inspect the rendered transition at steep
angles as well. The independent −60-degree hip probe still reports a visible first-shot arm jump of about
78 degrees, an unresolved visual acceptance issue.

Fixed-step footage also cannot establish the rig's frame-time budget. Independent sequential navigation
diagnostics reproduced ordinary carry exhausting all 12 trials of 12 contact projections and repeatedly
holding the preceding pose. Scratch timing counters located the long callback cost in that coherent-pose
search; a fresh isolated course stayed much cheaper. Profile repeated transitions and fallback frequency
before claiming multiplayer performance, alongside the strict soak and full Lab gates.

For unstaged network recovery proof, `net_test` accepts `--trace-recovery`: it logs each owned actor's natural
ragdoll/get-up events and remaining match time. Pass it through `tools/dev/net_bots.sh` after `--`. This observes
ordinary play; it does not force a recovery. Require an observed get-up during play as well as clean engine logs
before treating a stress run as recovery coverage.

The integrated branch still failed an independent rendered four-bot observer match (`--scenario=watch`,
`--melee-start`, match seed 25): three initial-pose reach exceptions and two right-wrist contact errors.
Its five ordinary four-bot soak passes do not cover this failure. Keep the engine-error gate strict and
reproduce the observer/relocation path before claiming complete presentation robustness.

Tuners are fresh **code defaults**, independent of saved settings or project `tuning.tres`. Direct current-build runs
accept explicit `--tune.Name=value` overrides for pose research; comparison commands use defaults. Differences in a build's
camera code remain visible in the player view. Side view has fixed framing. `--cases=a,b` limits a run; `--view=player`
uses the real player camera. `--capture-dir=$PWD/build/...` saves 12 samples per simulated second by default;
`animation_review.py --capture-fps=60` captures every rendered sample for smoother owner clips. The manifest
records the cadence and assembly uses that value. The tool creates
captioned six-frame sheets and deterministic short MP4s from those images, paired sheets for every build pair,
and `index.html` opening release, dev and candidate side by side, with optional two-build selection,
synchronized restart and half speed. Independent clip selection compares Walk
against Carry or alternative strikes. A whole-run real-time recording
can also be requested directly:

```sh
godot-mono --resolution 960x540 --disable-vsync --path . res://scenes/dev/animation_review.tscn -- --cases=pivot,turn,burst,reload,melee --view=side --record=$PWD/build/animation-review/actions.mp4 --record-fps=60
```

Fixed-step films are the comparison evidence; real-time `--record` cadence is affected by other GPU work. Neither
is a frame-time benchmark. Inspect all sheets and action footage before asking the owner to approve a redo.

For compact owner material, capture the selected cases at 60 fps, then compose their six views:

```sh
python3 -B tools/dev/animation_owner_review.py build/animation-owner-source --output build/owner-review
```

The source must contain `release`, `dev` and `candidate` media from `animation_review.py`. Composition writes one
H.264 1280×720 MP4 per case, checks each is below 8 MB, and writes a short viewing guide and exact provenance.

## Metrics and calibration

`make test` samples the seven named gate cases at 60 Hz in a final skeleton modifier, after feet, rifle and hands.
It rejects missing bones, non-finite values, incomplete windows, insufficient contact observations and skipped
frames. Python unit tests reject frozen feet, dropped samples and ineffective collision bounds. Each settled window
is 0.8–2.5 seconds: acceleration, stopping and teleport frames are excluded. A metric JSON lists every visual check and measured value. `--strict` makes any visual failure return nonzero.
`make test-full` measures all 36 cases with motion and grip assertions and runs the same strict checks. An improvement is reported as a pass rather than rejected as an unexpected success.

The immutable calibration in `tools/dev/animation_calibration.json` comes from `v0.2.0` (`7170a13c8d44`) through
this harness. Bare authored UAL1 clips independently cross-check it: Walk ankle correlation −0.597 and head
span 51.8 mm; Jog −0.548/228.6 mm; Sprint −0.791/125.3 mm. Forward walk, jog, creep and sprint establish each gait's envelope; today's `dev` does not set it.
The file includes reference measurements so threshold changes can be reviewed. Reproduce the reference with
`animation_review.py --metrics-only --build release=v0.2.0 --cases=walk_000,jog_000,creep_000,creep_180,creep_090,sprint,aim_idle`.

| Check | Threshold and calibration | Interpretation / limitation |
| --- | --- | --- |
| Ankle-height correlation | At most half the negative forward-reference correlation: walk −0.287, jog −0.289, creep −0.280, sprint −0.410 | Positive means the feet lift together. Frozen feet fail measurement. Sprint's flight still has a distinct alternating swing. |
| Head/pelvis vertical span | 0.5–1.5 times each reference gait's span; walk head 26–78 mm, jog 115–346 mm, creep 27–80 mm, sprint 63–188 mm | Height relative to character root. An authored stylized envelope, not a universal human-motion bound. Revisit if new approved clips deliberately have less bob. |
| Stock/head | Signed clearance at least −5 mm | Skin-derived head ellipsoid, allowing 5 mm proxy uncertainty; clear contact, not current fitting, is the reference. |
| Stock/shoulder | Signed clearance −5 to +25 mm | A shouldered stock contacts the shoulder without deep penetration; the upper bound catches floating stocks. The 25 mm allowance is anatomical/proxy fit tolerance, not a release measurement. |
| Knee/thigh center separation | At least 0.8 times reference minimum, about 141–143 mm | Knee centers and shortest thigh-bone segment distance; no claimed full mesh collision guarantee. |
| Hips/aim yaw, aimed back/side travel | At most 15° | Authored forward walk pelvis sway is under 2°; 13° additional allowance. This assumes a forward-facing rifle strafe. Owner must judge the silhouette; release combat facing is not a natural-motion reference. |
| Planted-foot drift | At most twice forward-reference median +0.02 m/s: walk 0.200, jog 1.159, creep 0.203; sprint 0.200 | Consecutive world-space ankles within 3 cm of each foot's minimum. Sprint uses the slow-walk bound because release sprint slides badly. This independent contact proxy includes some lift-off frames; report contact-pair counts too. |

The stock proxy is a rounded 18 mm-radius core from rifle-local `(0, .064, .08)` to `(0, .064, .255)` meters,
ending just ahead of the authored butt socket at z=.2605. The head ellipsoid uses all mesh vertices with >50%
Head weight, mapped through the final head bone. Shoulder is the proximal 14 cm of the right upper arm with a
55 mm radius. These are good regression proxies, not exact collision meshes: ellipsoid corners and stock cavities
are approximate. Check stock/head, shoulder and limb silhouettes in rendered views before promoting a clearance
check. A4 can change their result without changing any gameplay rule.

The release passes all alternation and span checks but fails shoulder clearance, sideways creep sliding,
sideways hips yaw and sprint sliding. Current `dev` fails forward walk/jog/creep alternation and aimed head
clearance. Good procedural foot locking by itself does not imply natural gait. Neither baseline is a blanket
naturalism target. Clearance and facing tolerances still need owner confirmation on the accepted rifle pose;
no free source clip shoulders a rifle, so an empirical rifle-clip calibration is unavailable.

## External clip review

Recorded Quaternius CC0 and 100STYLE CC BY 4.0 sources are kept outside the repository in a shared `assets-src/` directory. Its
`SOURCES.md` records stable download URLs, licenses and archive SHA-256 values. See [source choices](animation-sources.md).
Inventory every clip, including the separate root-motion variants:

```sh
python3 -B tools/dev/animation_inventory.py ../assets-src build/animation-review/clip-inventory.csv
python3 -B tools/dev/animation_review.py --candidate 'UAL1=../assets-src/UAL1-Standard/Universal Animation Library[Standard]/Unreal-Godot/UAL1_Standard.glb' --anims=Walk_Loop,Walk_Loop@reverse,Jog_Fwd_Loop,Sprint_Loop,Punch_Cross --output build/animation-candidates
python3 -B tools/dev/animation_review.py --candidate 'UAL2=../assets-src/UAL2-Standard/Universal Animation Library 2[Standard]/Unreal-Godot/UAL2_Standard.glb' --anims=Walk_Carry_Loop,Walk_Carry_Loop@reverse,Melee_Hook,Sword_Regular_A,Sword_Heavy_Combo --output build/animation-candidates
python3 -B tools/dev/animation_review.py --candidate 'hands=../assets-src/UAL2-Standard/Universal Animation Library 2[Standard]/Unreal-Godot/UAL2_Standard.glb' --anims=Walk_Carry_Loop,Melee_Hook --hands --output build/animation-candidates
python3 -B tools/dev/animation_review.py --assemble-only --build UAL1=unused --build UAL2=unused --build hands=unused --output build/animation-candidates
```

External GLBs are loaded directly, never copied into `assets/`. Animations map by bone name onto the packaged
mannequin. Every bone rest position must match within 0.1 mm, and each basis vector within 0.0001; incompatible skeletons fail loudly.
`_Loop` clips loop; one-shots hold their last pose for inspection and their sheets sample the action's own duration.
`@reverse` is explicitly a derived playback preview. Raw previews have neither weapon nor production modifiers; their player view uses the real CameraRig on code
defaults, and their side view has fixed framing.
`--hands` plays the clip on a stationary real character with its animation tree and foot correction disabled only
inside this dev scene, retaining the current rifle rig and hand solve. It is an integration sketch, not a finished
rifle pose or a controller-driven directional gait. Source root-motion measurements are data only: the controller
continues to own movement in production.

Burst/reload footage also remains the review fixture for the later recoil package. Recheck nominal barrel versus
aim-ray alignment there without forcing every spread bullet to follow the bore, and distinguish weapon kick
return from player-controlled aim recovery.

The rebuilt mannequin includes authored directional and turn derivatives. The owner approves its look before
landing; passing metrics alone does not supply that approval. [Locomotion](locomotion.md) describes the bounded
terrain correction, and [motion](motion.md) describes rifle contact and clearance checks.
