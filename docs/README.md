# Documents

Which document to trust for what. When they disagree, the owner's [design](../design/) wins on intent, the code wins on
behavior, and dated evidence yields to both.

## Binding design

[`design/`](../design/) is the owner's: what Propane is and must stay (feel, look, personality, direction). It is
current and binding, and no one else edits it. Start with [VISION.md](../design/VISION.md).

## Current contracts and runbooks

These describe the code as it is today. Change them in the same commit as the code.

| Document | What it covers |
| --- | --- |
| [multiplayer.md](multiplayer.md) | Networking model, messages and protocol 3, hosting, the lobby, bots and the network test tools. |
| [net-scenarios.md](net-scenarios.md) | Network harness ownership, lifecycle, CLI inventory and completion obligations. |
| [modes.md](modes.md) | How a game mode plugs in (rules, definition, runner, envelope) and the Lab prototypes. |
| [world-services.md](world-services.md) | `Game`, `Match`, session and world contexts: who owns what, and what stays shared. |
| [shutdown.md](shutdown.md) | Orderly process exit through `ShutdownGuard` and finalizer draining. |
| [code-quality.md](code-quality.md) | Formatting and analyzers, the gate (`make lint`, `check`, `test`, `test-infra`, `test-full`), adding a suite or scene. |
| [bot-navigation.md](bot-navigation.md) | The navigation layer and bot policies. |
| [lab.md](lab.md) | The Lab: its stations, environments and how its sessions are verified. |
| [locomotion.md](locomotion.md), [motion.md](motion.md) | Clips played as authored, and the rifle stance layered over them. |
| [animation-sources.md](animation-sources.md) | Where the animation clips come from, and their licenses. |
| [animation-review.md](animation-review.md) | The animation acceptance harness: commands and what it checks. |
| [measuring-scale.md](measuring-scale.md) | `make bench` and how to measure scale. |
| [upright-piles.md](upright-piles.md) | How upright pyramids are built and proved stable (ends with a dated review). |

## Dated investigation and verification evidence

Point-in-time reports: commits, branches, machines and measurements as of their date. Keep them for provenance and for
the negative results; do not read their numbers or "unreleased" labels as the present state. Several begin with a current
rule or procedure, which the contract documents above restate.

| Document | What it records |
| --- | --- |
| [test-speed.md](test-speed.md), [test-flakes.md](test-flakes.md) | Gate timings and the flaky-test investigations behind today's scheduler and fixtures. |
| [idle-checks.md](idle-checks.md) | Why Lab idle replays were not deterministic, and how they were made so. |
| [network-soak.md](network-soak.md), [network-corrections.md](network-corrections.md), [network-integration.md](network-integration.md), [participant-integration.md](participant-integration.md) | Tank-agreement calibration, correction fixes and integration reviews, with the soak and capacity procedures they used. |
| [tank-scaling.md](tank-scaling.md), [scale-actors.md](scale-actors.md), [scale-baseline.md](scale-baseline.md), [bot-scale.md](bot-scale.md), [vent-performance.md](vent-performance.md) | Performance and scale work: baselines, paired measurements, rejected approaches. |
| [king-online.md](king-online.md), [king-crowd.md](king-crowd.md), [lobby-tanks.md](lobby-tanks.md) | Verification of King online, crowd tactics and lobby tank supply. |
| [recoil.md](recoil.md), [rifle-contact.md](rifle-contact.md), [movement.md](movement.md) | Recoil, rifle contact and creep-speed work; each marks which parts are superseded. |

[Network scenario ownership verification](net-scenarios-verification.md) records the P4 harness migration,
including the preserved vent-contact failure, native inventories and paired measurements.

Their data and images sit beside them: `perf-vents/`, `recoil/`, `tank-scaling/`, `lobby-tanks/` and
`king-crowd-results.csv`. Some tools read them (for example, the vent baseline patch in `perf-vents/`), so move them
only together with those references. `readme_chain.gif` and `readme_still.jpg` illustrate the root README.

## Outside docs/

- [README.md](../README.md): what the game is, how to run it, the harness commands and the release process.
- [AGENTS.md](../AGENTS.md): the rules for agents working in this repository.

## Adding or changing a document

- A contract goes in the document that already owns the subject, or in a new one listed above, and changes with the code.
- A report starts with its date, the commit or branch it measured and the machine; it is not edited afterwards except
  to mark what has been superseded and where the current statement lives.
- Release and protocol labels name versions (`Protocol 3`, 0.4.0), not "unreleased", which goes stale.
