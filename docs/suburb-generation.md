# Suburb generation

`SuburbGenerator` coordinates plan generation with one seeded master `Random`. Its call order is part of the saved
plan and multiplayer compatibility contract. Keep arithmetic, iteration and tie order, random draws, model keys and
placement reasons unchanged when moving implementation.

`SuburbLayout` owns road templates, lot packing, content cropping and the road clearance areas. It receives the
settings and the existing master RNG, writes into the coordinator's plan, and exposes the resulting clearance areas
for later placement. Strip seam gaps constrain lot packing; cropping restores the full strip length. Cropping also
rebuilds road clearance areas from the clipped roads.

The remaining furnishing and supply work runs after layout: furnish lots in plan order, merge fences, place street
furniture, scatter parkland, choose tanks, then choose the single-player spawn. Lot furnishing seeds its own RNG
from each lot's seed; it does not draw from the master stream. Multiplayer starts and ammo are chosen last so they
do not perturb single-player plans.

Stages are ordinary owned objects, with no pipeline interface or scene callbacks. The generator retains its stage
state and random stream for its lifetime, as before extraction; callers normally create one generator per plan.

Verify changes with `make test-generator`, `make test-world`, and `make test-equivalence`. The last command compares
exact saved release plans, world construction and segment hashes on the reference platform, using
`--platform-references`; never regenerate those fixtures for a refactor. Also compare `PlanCodec.Encode` bytes
against the immediate parent across seeds, forced formations, match sizes and strip settings. Deep generator
shards live in `make test-full`.
