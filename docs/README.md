# Documents

Which document to trust for what. When they disagree, the owner's [design](../design/) wins on intent, the code wins on
behavior, and dated evidence yields to both.

Three kinds of document live here, in three places:

- **Contracts** (this folder): how the code works today and the rules for changing it. Binding. Change them in the same
  commit as the code.
- **Runbooks** ([runbooks/](runbooks/)): how to measure, soak, test and profile. Current commands, flags and procedures.
- **Evidence** ([evidence/](evidence/)): dated verification reports and investigations, with their datasets. Records of
  what was measured then, on that commit and machine. Not binding.

## Binding design

[`design/`](../design/) is the owner's: what Propane is and must stay (feel, look, personality, direction). It is
current and binding, and no one else edits it. Start with [VISION.md](../design/VISION.md).

## Contracts

| Document | What it covers |
| --- | --- |
| [architecture.md](architecture.md) | Living architecture contract: modules, scopes, ordering, approved exceptions, extension boundaries and unfinished acceptance. |
| [development.md](development.md) | The developer guide: running from source, the gate, tuning, repository layout, the scripted scenes in `scenes/dev`, versions and releases, and how the README media are made. |
| [multiplayer.md](multiplayer.md) | Networking model, messages and protocol 3, hosting, the lobby and its world, bots and the network test tools. |
| [net-scenarios.md](net-scenarios.md) | Network harness ownership, lifecycle, CLI inventory and completion obligations. |
| [modes.md](modes.md) | How a game mode plugs in (rules, definition, runner, envelope) and the Lab prototypes. |
| [world-services.md](world-services.md) | `Game`, `Match`, session and world contexts: who owns what, and what stays shared. |
| [suburb-generation.md](suburb-generation.md) | Generator ownership, stage order, random streams and exact-plan verification. |
| [world-clock.md](world-clock.md) | `WorldClock` timer scheduling and dispatch: order, cancellation, nesting, allocation. |
| [shutdown.md](shutdown.md) | Orderly process exit through `ShutdownGuard` and finalizer draining. |
| [code-quality.md](code-quality.md) | Formatting and analyzers, the gate (`make lint`, `check`, `test`, `test-infra`, `test-full`), launch options, adding a suite or scene. |
| [release-reference.md](release-reference.md) | The frozen v0.4.0 reference fixtures, their checks and the deliberate refresh. |
| [bot-navigation.md](bot-navigation.md) | The navigation layer and the bot policies. |
| [lab.md](lab.md) | The Lab: its stations, environments and how its sessions are verified. |
| [lab-scenarios.md](lab-scenarios.md) | Lab harness ownership: the host, scenario lifecycle, registry and completion obligations. |
| [recoil.md](recoil.md) | How a shot kicks the logical aim, the recoil tuners and the logical muzzle. |
| [locomotion.md](locomotion.md), [motion.md](motion.md) | Clips played as authored on one stride clock, and the rifle stance layered over them. |
| [animation-sources.md](animation-sources.md) | Where the animation clips come from, and their licenses. |
| [upright-piles.md](upright-piles.md) | How upright pyramids are built and proved stable. |

## Runbooks

| Document | What it covers |
| --- | --- |
| [runbooks/measuring-scale.md](runbooks/measuring-scale.md) | `make bench`, reading its measurements, comparing optimization branches and playing optimized C# locally. |
| [runbooks/moving-vent-residual.md](runbooks/moving-vent-residual.md) | FU2a scratch observation, correction decisions, contact traces and final-grid alignment. |
| [runbooks/network-soak.md](runbooks/network-soak.md) | Running the multiplayer soaks, how `compare_tanks.py` judges a match, the capacity gate and the lobby-bot soaks. |
| [runbooks/profiling-effects.md](runbooks/profiling-effects.md) | Measuring the cost of vents, smoke, blasts and chains in a crowd, alone or against a baseline build. |
| [runbooks/animation-review.md](runbooks/animation-review.md) | The animation acceptance harness: commands, the metrics gate and external clip review. |
| [runbooks/release-artifacts.md](runbooks/release-artifacts.md) | Packaged export inventory, Linux smoke, CI limits and Windows/macOS runtime checklists. |

## Evidence

Point-in-time reports: commits, branches, machines and measurements as of their date. Keep them for provenance and for
the negative results; do not read their numbers or "unreleased" labels as the present state. The date is when the
work was recorded (the repository's commit dates). "Superseded" says whether a later change or document replaced what
the report concludes; the contract named in each report's status line holds the current rule.

| Document | Date | Superseded | What it records |
| --- | --- | --- | --- |
| [evidence/multiplayer-verification.md](evidence/multiplayer-verification.md) | 2026-10-09 to 10-11 | No | The first-bot comparison, a LAN and VPN soak and the melee package's verification, moved out of the multiplayer contract. |
| [evidence/network-soak.md](evidence/network-soak.md) | 2026-10-11 to 10-13 | Procedures moved | Tank-agreement calibration, validation, the 16-actor capacity proof and the lobby-bot soaks. |
| [evidence/moving-vent-residual.md](evidence/moving-vent-residual.md) | 2026-10-18 | No | FU2a: retained parent terminal failure, acceleration/contact/age/timing discrimination, commands and open correction proof (data in `moving-vent-residual/`). |
| [evidence/network-corrections.md](evidence/network-corrections.md) | 2026-10-11 to 10-16 | No | The replication fixes that followed the soak assertion, each with its soak evidence. |
| [evidence/network-integration.md](evidence/network-integration.md) | 2026-10-11 | No | The independent review of the network-assertion branch: disconnect latency, vent correction, capacity follow-ups. |
| [evidence/participant-integration.md](evidence/participant-integration.md) | 2026-10-11, 10-12 | No | The participant (A6) merge review, the capacity proof and the tank-agreement tails it left open. |
| [evidence/net-scenarios-verification.md](evidence/net-scenarios-verification.md) | 2026-10-16 | No | The P4 harness migration: preserved vent-contact failure, native inventories, paired measurements. |
| [evidence/suburb-generation-verification.md](evidence/suburb-generation-verification.md) | 2026-10-17 | No | B10: stage ownership, parent plan bytes, exact references, deep sweeps and rendered comparisons. |
| [evidence/mode-client-adapters.md](evidence/mode-client-adapters.md) | 2026-10-18 | No | B3b1: client policy ownership, exact mode/HUD transcripts, session collection, native audits and paired callback measurements. |
| [evidence/actor-registration-lifetime.md](evidence/actor-registration-lifetime.md) | 2026-10-18 | No | L1: actor-lived registrations, matched retention/collection controls, callback ordering, paired costs and native audits. |
| [evidence/match-player-cache.md](evidence/match-player-cache.md) | 2026-10-17 | No | M0: ordered local-player selection, empty ownership, paired frame costs and native audits. |
| [evidence/mode-catalog-server-inputs.md](evidence/mode-catalog-server-inputs.md) | 2026-10-18 | No | B3a follow-up: server-owned mode inputs, retired catalog dependencies and named remaining boundaries. |
| [evidence/mode-catalog-verification.md](evidence/mode-catalog-verification.md) | 2026-10-18 | No | B3a: complete compiled catalog, scoped restoration, identical inventories/packets, paired server ticks and native mode audits. |
| [evidence/client-authority-transactions.md](evidence/client-authority-transactions.md) | 2026-10-17 | No | B2: exact authority transactions, session retirement, allocation correction, paired playing ticks, native audits and Linux export. |
| [evidence/dissolve-stop-deadline.md](evidence/dissolve-stop-deadline.md) | 2026-10-18 | No | H2: controlled negative-gap catch, corrected harness completion deadline and repeated seed-12 shards. |
| [evidence/streaming-history.md](evidence/streaming-history.md) | 2026-10-18 | No | W1: ordinary streaming storage bounds, parent feed decisions, raw diagnostics, native collection and the paired callback measurement protocol. |
| [evidence/architecture-verification.md](evidence/architecture-verification.md) | 2026-10-17 | No | A0: architecture cross-check, acceptance ledger, dependency baseline, negative probes, rendered input and local gate cost. |
| [evidence/artifact-verification.md](evidence/artifact-verification.md) | 2026-10-18 | No | R1: setup failures, packaged inventory, Linux/Proton smoke, source configurations and deployment identity. |
| [evidence/server-run-verification.md](evidence/server-run-verification.md) | 2026-10-17 | No | B1: run ownership, exact parent packets, resource retirement, paired ticks, native matrix and capacity. |
| [evidence/ledger-count-verification.md](evidence/ledger-count-verification.md) | 2026-10-17 | No | B1a: live tank accounting, parent operation transcripts, paired stock-tick costs and native soaks. |
| [evidence/world-clock-verification.md](evidence/world-clock-verification.md) | 2026-10-16 | No | The B6 clock dispatch change: parent callback transcripts, allocation and burst measurements. |
| [evidence/lab-scenarios-verification.md](evidence/lab-scenarios-verification.md) | 2026-10-17 | No | The B7a Lab harness migration: parent inventories, ordered awaits, byte-identical traces, captures. |
| [evidence/lab-tuner-sampling.md](evidence/lab-tuner-sampling.md) | 2026-10-17 | No | P5: scalar/trace equivalence, zero-allocation sampling, paired costs, captures and the retained deep-gate network failure. |
| [evidence/king-online.md](evidence/king-online.md) | 2026-10-14 | No | King online: gates, network matrix, late loading, rendered presentation. |
| [evidence/king-crowd.md](evidence/king-crowd.md) | 2026-10-15 | No | King crowd tactics: holds, mints and thinking cost at 4, 8 and 16 actors (data: [king-crowd-results.csv](evidence/king-crowd-results.csv)). |
| [evidence/lobby-tanks.md](evidence/lobby-tanks.md) | 2026-10-14 | No | Lobby and Lab tank supply: gates, device delivery, native matrix (images in `lobby-tanks/`). |
| [evidence/test-speed.md](evidence/test-speed.md) | 2026-10-11 to 10-14 | Partly (the gate changed) | Gate timings and the scheduler changes behind today's parallel `test-full`. |
| [evidence/test-flakes.md](evidence/test-flakes.md) | 2026-10-14 | No | The gather and torso-twist ragdoll flake investigations. |
| [evidence/idle-checks.md](evidence/idle-checks.md) | 2026-10-16 | No | Why Lab idle replays were not deterministic and how they were made so. |
| [evidence/unit-time-regression.md](evidence/unit-time-regression.md) | 2026-10 | No | The loaded-machine proof for manual-clock unit tests. |
| [evidence/tank-scaling.md](evidence/tank-scaling.md) | 2026-10-14 | Partly (pacing claims) | Tank scaling: baselines, paired measurements, rejected approaches (data in `tank-scaling/`). |
| [evidence/scale-baseline.md](evidence/scale-baseline.md) | 2026-10-14 | Yes, as a baseline | The scale baseline before bot and tank optimization. |
| [evidence/scale-actors.md](evidence/scale-actors.md) | 2026-10-14 | No | Actor CPU and rendering scale after tank scaling. |
| [evidence/prototype-observations.md](evidence/prototype-observations.md) | 2026-10-18 | Focused proof | B3b2: plain prototype HUD/bot observations, exact traces, allocation/lifetime and paired runner cost. |
| [evidence/lab-mechanics-verification.md](evidence/lab-mechanics-verification.md) | 2026-10-18 | No | B4b: shared Lab facts/difficulty/placement, exact traces, collection, captures and paired runner costs. |
| [evidence/bot-tactics-verification.md](evidence/bot-tactics-verification.md) | 2026-10-17 | No | B4a: owned tactical state, parent/frozen traces, allocation and owned-bot native evidence. |
| [evidence/bot-scale.md](evidence/bot-scale.md) | 2026-10-14 to 10-16 | No | Bot scale: 16 in-process bots, frame and brain cost, idle bots. |
| [evidence/bot-navigation-measurements.md](evidence/bot-navigation-measurements.md) | 2026-10-10 | Yes (shared-cache navigator) | The original navigator's course sweeps and paired matches. |
| [evidence/vent-performance.md](evidence/vent-performance.md) | 2026-10-12 | No | Crowded effect profiling and the optimizations it drove (data in `perf-vents/`). |
| [evidence/lab-stack-budget.md](evidence/lab-stack-budget.md) | 2026-10 | Yes (the range is now 24-512) | The 24-40 tank stack budget comparison. |
| [evidence/lab-tracking-verification.md](evidence/lab-tracking-verification.md) | 2026-10-16 | No | P2: tank-tracking lifetime proofs; rendered paired performance still owed. |
| [evidence/body-publication-verification.md](evidence/body-publication-verification.md) | 2026-10-16 | No | P3: body publication buffers; the native performance matrix is still owed. |
| [evidence/release-reference-verification.md](evidence/release-reference-verification.md) | 2026-10-16 | No | P0: the v0.4.0 reference fixtures, repeated-load results and rejection probes. |
| [evidence/recoil.md](evidence/recoil.md) | 2026-10-11 | Partly (the clip redo) | R19 recoil: climb and curves (data in `recoil/`), barrel alignment (removed), bot comparison. |
| [evidence/rifle-contact.md](evidence/rifle-contact.md) | 2026-10-10 | Mostly (the clip redo) | Package 2: rifle wrist and finger calibration. |
| [evidence/movement.md](evidence/movement.md) | 2026-10-10 | No | The creep-speed comparison behind the 1.6 m/s default. |
| [evidence/upright-piles-review.md](evidence/upright-piles-review.md) | 2026-10-11 | No | The Linux stability and soak review of upright pyramids. |

Their data and images sit beside them: `evidence/perf-vents/`, `evidence/recoil/`, `evidence/tank-scaling/`,
`evidence/lobby-tanks/` and `evidence/king-crowd-results.csv`. Some tools read them (the vent baseline patch in
`perf-vents/`, the default output of `tools/dev/plot_recoil.py`), so move them only together with those references.
`readme_chain.gif` and `readme_still.jpg` illustrate the root README; [development.md](development.md#readme-media) says how they are made.

## Outside docs/

- [README.md](../README.md): what the game is and how to download, play, host and build it. The harness commands and the release process are in [development.md](development.md).
- [AGENTS.md](../AGENTS.md): the rules for agents working in this repository.

## Adding or changing a document

- A contract goes in the document that already owns the subject, or in a new one listed above, and changes with the code.
- A runbook gives commands that work today and says how to read their output. When it needs a measurement as proof,
  it links the evidence report instead of repeating the numbers.
- A report starts with its date, the commit or branch it measured and the machine, and a **Status** line. It is not edited
  afterwards except to mark what has been superseded and where the current statement lives. A report that also states a
  rule moves the rule into the contract and keeps the history here.
- Release and protocol labels name versions (`Protocol 3`, 0.4.0), not "unreleased", which goes stale.
- Moving or renaming a document means fixing every link to it. `make lint` runs `tools/dev/check_links.py`, which
  fails on a relative Markdown link, image or `#fragment` that does not resolve.
