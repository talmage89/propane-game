# Propane — design

A third-person sandbox: run around a procedurally generated suburb, find the propane tanks, and shoot them. A shot punctures a tank and it vents fire indefinitely; a second shot blows it up. Blasts throw everything nearby, including the player, who ragdolls and gets back up. Clearing the last tank generates a new neighborhood.

Status: **approved 2026-10-07**, built and validated (see section 6). No audio in the MVP.

Multiplayer (tank bank, 2–5 players) is designed and built separately: see [MULTIPLAYER.md](MULTIPLAYER.md). The game now opens on a main menu that leads to this sandbox or to multiplayer.

## 1. Decided

| Area | Decision |
| --- | --- |
| Tank behavior | Shot 1 punctures it: a hole appears where the bullet hit, and it vents fire forever. Shot 2 detonates it. |
| Vent | The jet pushes the tank: thrust at the hole, opposite the jet, leaning around the tank's axis so it spins and rolls. |
| Chain reactions | A blast within a short radius counts as a shot on other tanks: an intact tank starts venting, a venting one detonates after a short fuse. Further out, blasts only knock things back. |
| Player harm | Knockback and ragdoll, then get up. No health or death. |
| Loop | Pure sandbox. R generates a new suburb at any time (there is no reset of the current one), and clearing the last tank does the same. |
| Tank count | 30 per map by default, standing in groups of 1 to 10, so one blast sets off its neighbors. Finding every group takes a few minutes. |
| World | A procedurally generated suburb (houses, backyards with fences and gate openings, streets, cars) in a bounded chunk, on an infinite floor. Nothing outside the chunk is rendered. You can walk forever, but there is nothing out there. |
| Motion | Objects are untethered and have weight. Blasts (knockback) are the only thing that moves them. |
| Anchoring | Houses, trees, the ground, curbs and street lamps never move. Cars, bins, grills, furniture, mailboxes and props are loose. Fence panels hold until a blast is strong enough to tear them loose. |
| HUD | The number of tanks left, a crosshair (a dot and four marks set out at the current spread), and small arrows toward every group of tanks: on the screen edge when the group is out of view, hovering over it when in view, faint when far and solid when close (a toggle in the tuning panel). |
| Character | The Quaternius mannequin (CC0), with animations merged from both Universal Animation Libraries. To revisit later. |
| Weapon | Rifle, fully automatic by default (a toggle in the tuning panel switches to semi-automatic). |
| Input | Mouse and keyboard only. No gamepad. |
| Audio | None in the MVP. |
| Language | C#, on Godot 4.7.2 .NET with Jolt physics and Forward+. |

## 2. Gameplay

**Controls.** WASD to move, Shift to sprint, Space to jump, and the mouse to look. Left-click fires; holding right-click aims. R generates a new suburb. Esc pauses, frees the mouse and lists the controls. F1 (or `) opens the tuning panel, and F11 (or Alt+Enter) toggles fullscreen.

**Rifle.**
- The camera never moves when aiming. Holding right-click only changes the character's stance and the spread.
- Unaimed, the character carries the rifle low and relaxed, snapping it up to the hip to fire, and shots spread wide. Moving and jumping widen the spread further. Sprinting drops it to a low-ready carry.
- Aimed, the rifle comes up to the shoulder and the spread is tight. The character turns to face where the camera points.
- Holding the trigger fires automatically at the fire rate (semi-automatic when the toggle is off), with unlimited ammo and no reload. They are hitscan from the camera through a random point in the spread cone. A second ray from the muzzle stops shots from passing through walls next to the character.
- Each shot has recoil kick, a muzzle flash, a tracer, an impact effect matched to the surface, and a small push on light objects.

**Tanks.**
- 30 per map, in groups of 1 to 10 packed tight enough to chain, placed where they belong: by grills and patio heaters, beside garages and sheds, on porches, at the curb, filling pickup beds, and now and then out on a lawn. The groups are spread across the map, about seven to a suburb. The total and the group sizes are dials.
- Clearing the last tank leaves a short beat to enjoy the result, then the next suburb assembles.

**Physics feel.** Arcade, not realism. Debris stays until the next suburb, so the mess builds up. A blast close to the player throws them as a ragdoll; one further out makes them stagger.

**Scale.** The world is real-world scale, because the tank is real at 46 cm. The map is cropped to its built-up area, about 180 × 180 m with 16–35 lots, and takes about 20 s to cross at a sprint. A layout with fewer than 18 lots is rerolled.

## 3. Tuning

Every effect strength lives in one resource, `tuning.tres`, which can be edited in the Godot inspector. F1 opens the same values in-game as live sliders, with a Save button that writes them back. The tunable values include:
- vent thrust;
- blast impulse, radius and falloff;
- the chain-reaction radius and fuse;
- the player's ragdoll threshold;
- debris speed;
- camera shake and hitstop;
- hip and aimed spread, and fire rate;
- player speeds and mouse sensitivity;
- the fireball scale, light intensity, and so on.

## 4. Look

**The void is white.** The world sits in a sterile, high-tech, VR-construct space. The infinite floor is near-white with a faint grid that fades with distance, under a pale sky with a soft white horizon. The suburb sits on a slab 12 cm proud of the floor, like a diorama, with a crisp edge: lawns, asphalt and roads simply stop.

**The light is golden hour.** The sun sits about 8° above the horizon, deep orange, with a warm haze along the horizon toward it and cool sky light in the shadows, which stretch far across the suburb and the white floor.

**The suburb is clean, stylized and reuses its parts heavily.**
- **Houses:** the 21 houses of the Kenney Suburban kit, each with one of four color palettes, set back on lots with driveways, walks and patios.
- **Fences:** surround the backyards, with gate openings and no gates.
- **Props:** Kenney furniture and nature kits plus props built in Blender (grills, bins, mailboxes, lamps, hydrants, patio heaters, sheds), with random rotation, scale and color per instance.
- **Cars:** from a CC0 kit.

**The tank is the hero.** It is the one fully realistic PBR object, so it reads as precious against the toy-like world.

**Explosion:**
- **Fireball:** layered 3D particles, a rolling fireball (your flipbook), a smoke column and embers.
- **Shockwave:** a ground ring.
- **Light:** a hard orange flash.
- **Feedback:** a camera shake scaled by distance, and a brief hitstop when the blast is close.
- **Aftermath:** a scorch decal and the 25 debris pieces with sooty interiors.

**Venting:** a blue-throated flame jet from the bullet hole with flickering light, and a gray smoke column that rises above fences and roofs, the only cue to where a venting tank has got to. The thrust skids and tumbles the tank, with its spin capped so the flame stays a readable tongue.

**Map transition.** A scan ring sweeps out from the player. The old suburb dissolves behind it and the new one materializes, in keeping with the VR void.

## 5. Build plan

1. **Foundation.** Project scaffolding, the tuning resource and panel, and the tank import (mesh, collision, debris, material).
2. **Tank.** Puncture, venting, detonation, blast physics and chain reactions.
3. **Player.** Character, locomotion, the third-person camera, and the rifle with hip and aimed stances.
4. **Ragdoll.** Blast launch, a camera that follows the hips, and getting back up.
5. **World.** The white void and infinite floor, procedural streets, lots, houses, fences and props.
6. **Loop.** Tank placement, the HUD, completion and R both leading to the transition into a new suburb.
7. **Polish.** A tuning pass, effects and lighting, and a performance pass.

All seven steps are built.

**Verification at each step:**
- headless C# tests for the generator: it is deterministic from a seed, nothing overlaps, and every tank is reachable;
- a physics soak test that sets off every tank at once;
- scripted screenshot captures, which I review before calling a step done.

## 6. Validation

- **Generator** (`generator_tests.tscn`, 200 seeds): deterministic per seed; no overlapping lots, houses, fences or tanks; exactly the full tank count inside the map and clear of fences; a safe spawn on a road; every tank reachable on foot.
- **Settling** (`game_test.tscn`, settle scenario, 13 seeds): no prop, car or tank moves or tips before something hits it.
- **Game loop** (`game_test.tscn`): a bot clears a whole suburb and the next one assembles; spawn and materialize, puncture, vent, detonate, the count dropping, chain reactions, R and the dissolve into a new suburb, the ragdoll and get-up from the player camera, shots and impacts, camera behavior against walls, fences and the slab edge, every tank spot, and the menus.
- **Soak** (`soak.tscn`): ten tanks venting, then all detonating at once with about 250 debris bodies, costs under 2 ms of physics per frame on an M4 Pro, with no hitch on the first explosion thanks to the shader warm-up.
