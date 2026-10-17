# Suburb generation

`SuburbGenerator` coordinates plan generation with one seeded master `Random`. Its call order is part of the saved
plan and multiplayer compatibility contract. Keep arithmetic, iteration and tie order, random draws, model keys and
placement reasons unchanged when moving implementation.

`SuburbLayout` owns road templates, lot packing, content cropping and the road clearance areas. It receives the
settings and the existing master RNG, writes into the coordinator's plan, and exposes the resulting clearance areas
for later placement. Strip seam gaps constrain lot packing; cropping restores the full strip length. Cropping also
rebuilds road clearance areas from the clipped roads.

`SuburbFurnishing` owns houses, paving, yard and street props, parkland, fence segments and gate openings. It receives
settings, the catalog, the existing master RNG and layout's road clearance areas. Its order is lot furnishing in
plan order, fence merging, street furniture, then parkland. Each lot seeds its own RNG from its lot seed; lot
furnishing does not draw from the master stream.

Later supply selection borrows the occupied areas, prop obstacles, story tank sites and original fence segments.
Clearance uses those original segments, before merging and cutting gate openings, rather than the plan's finished
fences. Furnishing remains their only writer. Fence segments retain their concrete `List` so repeated clearance
queries keep struct enumeration; the other outputs expose read-only views. Road-clearance queries live with layout
and take their plan/settings inputs explicitly.

The remaining supply work chooses tanks and then the single-player spawn. Multiplayer starts and ammo are chosen
last so they do not perturb single-player plans.

Stages are ordinary owned objects, with no pipeline interface or scene callbacks. The generator retains its stage
state and random stream for its lifetime, as before extraction; callers normally create one generator per plan.

Verify changes with `make test-generator`, `make test-world`, and `make test-equivalence`. The last command compares
exact saved release plans, world construction and segment hashes on the reference platform, using
`--platform-references`; never regenerate those fixtures for a refactor. Also compare `PlanCodec.Encode` bytes
against the immediate parent across seeds, forced formations, match sizes and strip settings. Deep generator
shards live in `make test-full`.
