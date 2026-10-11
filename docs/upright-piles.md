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
