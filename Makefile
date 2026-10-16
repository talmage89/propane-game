# Everyday tasks. `make` lists them. The scripts they run live in tools/ and work on their own too.
# GODOT is the Godot .NET editor: godot-mono or godot on PATH, else the macOS app. Set it to override.
GODOT ?= $(shell command -v godot-mono || command -v godot || echo /Applications/Godot_mono.app/Contents/MacOS/Godot)
export GODOT

N ?= 4
# Each checkout keeps its own Godot user data (override.cfg, written by setup). One whose folder name ends in a number
# (the worktrees dev-0, dev-1...) also gets its own server port, 24680 plus that number, so worktrees don't collide.
CHECKOUT := $(notdir $(CURDIR))
SLOT := $(shell echo '$(CHECKOUT)' | sed -n 's/.*[^0-9]\([0-9]\{1,2\}\)$$/\1/p')
PORT ?= $(if $(SLOT),$(shell echo $$((24680 + $(SLOT)))),24680)
SERVER ?= 127.0.0.1:$(PORT)

.DEFAULT_GOAL := help
.PHONY: help setup run play bench build lint check test test-infra test-core test-behavior test-generator test-world test-full test-capacity server bots bots-stop package deploy release-patch release-minor release-major

help: ## List the tasks
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN { FS = ":.*## " } { printf "  make %-14s %s\n", $$1, $$2 }'

setup: ## Import the assets and compile: a fresh clone, or after switching branches or pulling
	@printf '; Written by make setup: user:// for this checkout only.\n[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="Propane-dev/%s"\n' "$(CHECKOUT)" >override.cfg
	"$(GODOT)" --headless --path . --import >/dev/null 2>&1 || true
	@$(MAKE) --no-print-directory build

run: build ## Play from source
	"$(GODOT)" --path .

# Keep these generic defaults local to bench/play. SECONDS is also a Bash timer variable.
play: CONFIG ?= Release
play: ## Play optimized C# from source (CONFIG=Release default for this target; Debug also supported)
	python3 -B tools/dev/source_run.py --config $(CONFIG)

bench: CONFIG ?= Debug
bench: BOTS ?= 0,2,4,8,16
bench: TANKS ?= 1,2,4,8
bench: RENDERED ?= 0,1
bench: SECONDS ?= 10
bench: REPEATS ?= 3
bench: WARMUP ?= 2
bench: BENCH_ARGS ?=
bench: ## Measure the bot/tank grid (BOTS, TANKS, CONFIG, RENDERED, SECONDS, REPEATS, WARMUP)
	python3 -B tools/dev/bench.py --bots=$(BOTS) --tanks=$(TANKS) --config=$(CONFIG) --rendered=$(RENDERED) --seconds=$(SECONDS) --repeats=$(REPEATS) --warmup=$(WARMUP) $(BENCH_ARGS)

build: ## Compile the C# code
	dotnet build Propane.sln -c Debug -nologo -v quiet

lint: ## Check C# formatting and script .uid pairs, and compile with warnings as errors
	@unpaired=$$(git ls-files 'src/*.cs' 'src/*.cs.uid' | sed 's/\.uid$$//' | sort | uniq -u); \
	if [ -n "$$unpaired" ]; then printf 'Commit each script with its Godot .uid file, and no .uid without its script:\n%s\n' "$$unpaired"; exit 1; fi
	dotnet format Propane.sln --verify-no-changes
	dotnet build Propane.sln -c Debug --no-restore --no-incremental -nologo -v quiet -warnaserror

# A recipe keeps lint before test, and the gate self-tests last, even when make runs with -j.
check: lint ## Run lint, then the tests, then the gate self-tests
	@$(MAKE) --no-print-directory test
	@$(MAKE) --no-print-directory test-infra

# These same partitions run as separate CI jobs. Shared build runs once, even with make -j.
test: test-core test-behavior test-generator test-world ## Run the fast headless gate (the same suites as CI)

# Checks the gate itself rather than the game: that lint rejects the mistakes it claims to, that check and test-full run
# every command in the order and with the failure propagation they promise, that the deep shards add up, and that CI
# mirrors the local partitions. Needs dotnet, make, git and python3 only (CI runs it as its own job).
test-infra: ## Check the gate itself: analyzer negatives, make ordering and failure propagation, deep-shard and CI wiring
	python3 -B tools/dev/lint_tests.py

test-core: build ## Run checker, platform, movement, unit and scripted mode-rule assertions
	GODOT="$(GODOT)" python3 -B tools/dev/check_test_threads.py
	GODOT="$(GODOT)" python3 -B tools/dev/bench_tests.py
	python3 -B tools/dev/check_world_tests.py
	python3 -B tools/dev/time_gate_tests.py
	python3 -B tools/dev/check_scores_tests.py
	python3 -B tools/dev/check_capacity_tests.py
	python3 -B tools/dev/compare_vents_tests.py
	python3 -B tools/dev/compare_tanks_tests.py
	python3 -B tools/dev/net_bots_tests.py
	python3 -B tools/dev/release_tests.py
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/core_tests.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/mode_test.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/shutdown_tests.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/shutdown_tests.tscn -- --paused --window-close --repeat-quit

test-behavior: build ## Run isolated physics, navigation, menu, grip and feet probes
	GODOT="$(GODOT)" python3 -B tools/dev/check_shader_capacity.py
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/rifle_reach_tests.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/server_tuning_tests.tscn -- --server --tune.MaxPlayers=6 --port=$(PORT)
	"$(GODOT)" --headless --path . res://scenes/dev/server_seed_tests.tscn -- --server --match-seed=23 --port=$(PORT)
	"$(GODOT)" --headless --path . res://scenes/dev/transport_physics_tests.tscn -- --server --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/body_correction_tests.tscn
	GODOT="$(GODOT)" python3 -B tools/dev/check_lab.py
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/session_actors_tests.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_composition_tests.tscn
	for count in 2 8 16; do "$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/capacity_ui_tests.tscn -- --players=$$count || exit; done
	python3 -B tools/dev/animation_review_tests.py
	@mkdir -p build/animation-review-gate
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/animation_review.tscn -- --cases=walk_000,jog_000,creep_000,creep_180,creep_090,sprint,aim_idle --metrics=$(CURDIR)/build/animation-review-gate/metrics.csv
	python3 -B tools/dev/animation_metrics.py build/animation-review-gate/metrics.csv --output build/animation-review-gate/results.json
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/world_services_tests.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/actor_detail_tests.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/ragdoll_pose_tests.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/blast_query_tests.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/upright_pile_tests.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/gather_range.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/gather_range.tscn -- --scenario=grant-races
	"$(GODOT)" --headless --path . res://scenes/dev/gather_range.tscn -- --scenario=appearance
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/game_test.tscn -- --scenario=gather --tune.FixedSeed=23
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/game_test.tscn -- --scenario=clear --quit=32 --tune.FixedSeed=23
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/melee_range.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=melee --assert-motion --assert-grip
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/bot_navigation_course.tscn -- --seeds=5
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=gamepad --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=devices --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=mouse --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=bots --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=king --classic-lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lobby --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=ragdoll --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --assert-feet --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --feet-corrections --assert-feet --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --assert-feet --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=movement --assert-motion --assert-grip
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --assert-motion --assert-grip
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=aim --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=hip --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=creep --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=hip --burst-start=0 --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=bot-recoil --magazines=2

test-generator: build ## Check every generator assertion with bounded seed depth
	"$(GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=20 --large-seeds=3 --formation-seeds=2

test-world: build ## Compare all formations built in two fresh processes
	tools/dev/check_world.sh

# The deep sweeps are independent targets, so make -j4 test-full runs them side by side with the gate. label.sh prefixes
# each line of a job's output with its name. The menu lifetime run shares test-behavior's port and settings file, so it
# waits for it. The bot matches share this checkout's port and build/ names, and their server and clients run in real
# time and fail when other jobs starve them, so test-full runs them alone, one at a time, after everything else.
LABEL := tools/dev/label.sh
# Four contiguous shards of the whole generator sweep; test-full-generator-coverage proves they hold exactly its seeds.
GENERATOR_FULL := --seeds=200 --large-seeds=100 --formation-seeds=6 --segment-seeds=100
GENERATOR_1 := --first-seed=1 --seeds=50 --first-large-seed=1 --large-seeds=25 --first-formation-seed=1 --formation-seeds=2 --first-segment-seed=1 --segment-seeds=25
GENERATOR_2 := --first-seed=51 --seeds=50 --first-large-seed=26 --large-seeds=25 --first-formation-seed=3 --formation-seeds=2 --first-segment-seed=26 --segment-seeds=25
GENERATOR_3 := --first-seed=101 --seeds=50 --first-large-seed=51 --large-seeds=25 --first-formation-seed=5 --formation-seeds=1 --first-segment-seed=51 --segment-seeds=25
GENERATOR_4 := --first-seed=151 --seeds=50 --first-large-seed=76 --large-seeds=25 --first-formation-seed=6 --formation-seeds=1 --first-segment-seed=76 --segment-seeds=25
GENERATOR_SHARDS := test-full-generator-1 test-full-generator-2 test-full-generator-3 test-full-generator-4
# Independent full Lab sessions; keep King's cross-round mint assertion in one process.
LAB_soak-void := --only=soak-void
LAB_soak-suburb := --only=soak-suburb
LAB_king := --only=king-full
LAB_king-suburb := --only=king-suburb
LAB_fast := --only=rules,aimstall,duel,ringout,king,shortfuse,shortfuse-combo,dissolve,idle-bank,idle-king
LAB_pace := --only=dissolve-pace
LAB_ringout-11 := --only=ringout-full --first-seed=11 --seeds=1
LAB_ringout-12 := --only=ringout-full --first-seed=12 --seeds=1
LAB_ringout-13 := --only=ringout-full --first-seed=13 --seeds=1
LAB_shortfuse-11 := --only=shortfuse-full --first-seed=11 --seeds=1
LAB_shortfuse-12 := --only=shortfuse-full --first-seed=12 --seeds=1
LAB_dissolve-11 := --only=dissolve-full --first-seed=11 --seeds=1
LAB_dissolve-12 := --only=dissolve-full --first-seed=12 --seeds=1
LIFETIME_PARTS := 1 2 3 4
$(foreach part,$(LIFETIME_PARTS),$(eval LAB_lifetime-$(part) := --only=lifetime --lifetime-shard=$(part) --lifetime-shards=$(words $(LIFETIME_PARTS))))
DIAL_PARTS := 1 2 3 4 5 6 7 8
$(foreach part,$(DIAL_PARTS),$(eval LAB_dials-$(part) := --only=dials --dial-shard=$(part) --dial-shards=$(words $(DIAL_PARTS))))
LAB_JOBS := king king-suburb fast soak-void soak-suburb shortfuse-11 shortfuse-12 dissolve-11 dissolve-12 pace \
	ringout-11 ringout-12 ringout-13 $(addprefix lifetime-,$(LIFETIME_PARTS)) $(addprefix dials-,$(DIAL_PARTS))
LAB_SHARDS := $(addprefix test-full-lab-,$(LAB_JOBS))
# Start long and variable-cost jobs early; enough bounded jobs keep four slots busy through the tail. The gate
# overlaps deep probes, and menu lifetime still waits for behavior before using its settings and server port.
FULL := test-full-lab-king test-behavior test-full-lab-soak-void test-full-lab-soak-suburb $(GENERATOR_SHARDS) \
	test-full-lab-fast test-full-navigation test-full-lab-king-suburb test-full-piles test-core \
	$(filter-out test-full-lab-king test-full-lab-king-suburb test-full-lab-fast test-full-lab-soak-void test-full-lab-soak-suburb,$(LAB_SHARDS)) \
	test-full-range test-full-menu test-full-generator-coverage test-full-lab-coverage
.PHONY: $(FULL) $(LAB_SHARDS) test-full-bots

test-full: $(FULL) test ## Run the gate plus deep sweeps (make -j4 test-full runs them side by side)
	@$(MAKE) --no-print-directory test-full-bots

# The deep probes use optimized Debug (DEBUG/TOOLS and the Debug Godot API), in a separate runtime project.
# Normal test/check/CI, benchmark smoke and the real-time bot matches retain the normal Debug build.
FULL_GODOT := $(CURDIR)/tools/dev/full_engine.py
DEEP := $(filter-out test-core test-behavior,$(FULL))
.PHONY: test-full-engine
test-full-engine: build
	$(LABEL) engine python3 -B tools/dev/full_engine.py --prepare --godot="$(GODOT)"

$(DEEP): test-full-engine

test-full-bots: build
	$(LABEL) bots tools/dev/net_bots.sh -f -o "$(CURDIR)/build/rifle-soak-gate" -n 4 -w 0 -l 90 -s '--match-seed=25' -- --melee-start --net-seed=2 --net-lag=120 --net-jitter=30 --net-loss=3
	$(LABEL) bots tools/dev/net_bots.sh -f -n 2 -w 0 -l 20 -s '--start-score=8 --match-seed=23' -o "$(CURDIR)/build/duel-lane-gate" -- --scenario=duel --matches=2 --duel-blocked --tune.DropScatter=0 --tune.DropToss=0 --net-lag=120 --net-jitter=30 --net-loss=3
	$(LABEL) bots tools/dev/net_bots.sh -f -n 2 -l 20 -o "$(CURDIR)/build/loading-readiness-gate" -- --scenario=body-stream --tank-check-ready-stall=Bot2

$(LAB_SHARDS): test-full-lab-%: build
	$(LABEL) lab-$* env GODOT="$(FULL_GODOT)" python3 -B tools/dev/check_lab.py --full $(LAB_$*)

test-full-lab-coverage: build
	$(LABEL) lab-coverage env GODOT="$(FULL_GODOT)" python3 -B tools/dev/check_lab_shards.py \
		$(foreach job,$(LAB_JOBS),--shard='--full $(LAB_$(job))')

test-full-piles: build
	$(LABEL) piles "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/upright_pile_tests.tscn -- --deep --awake

test-full-menu: build test-behavior
	$(LABEL) menu "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lifetime --classic-lobby --port=$(PORT)

test-full-range: build
	$(LABEL) range "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=melee-reloads --assert-motion --assert-grip
	$(LABEL) range "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=recoil --stance=air --assert-grip --assert-motion
	$(LABEL) range "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=bot-recoil --magazines=10

$(GENERATOR_SHARDS): test-full-generator-%: build
	$(LABEL) generator-$* "$(FULL_GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- $(GENERATOR_$*)

test-full-generator-coverage: build
	$(LABEL) generator-coverage env GODOT="$(FULL_GODOT)" python3 -B tools/dev/check_generator_shards.py --full='$(GENERATOR_FULL)' \
		--shard='$(GENERATOR_1)' --shard='$(GENERATOR_2)' --shard='$(GENERATOR_3)' --shard='$(GENERATOR_4)'

test-full-navigation: build
	$(LABEL) navigation "$(FULL_GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/bot_navigation_course.tscn -- --seeds=100

test-capacity: build ## Repeat the separate-client tested-capacity gate (three clean and three faulted matches)
	tools/dev/capacity_acceptance.sh

server: build ## Run a multiplayer server here (PORT, default 24680, or this worktree's)
	"$(GODOT)" --headless --path . -- --server --port=$(PORT)

bots: build ## Add N bots to a server's first open lobby (SERVER=host[:port], default this computer on PORT)
	tools/dev/add_bots.sh -n $(N) -S $(SERVER)

bots-stop: ## Stop this checkout's bots
	tools/dev/add_bots.sh -x

package: ## Build the Windows, macOS and Linux release archives here, in build/dist (needs rcodesign)
	tools/release/build.sh "$(GODOT)"

deploy: ## Run the server in Docker over ssh: make deploy HOST=host DIR=dir (from main, the latest release)
	tools/server/deploy.sh "$(HOST)" "$(DIR)" "$(GODOT)"

release-patch: ## Release a patch (plays with the same minor): fixes local to each player
	tools/release/cut.sh patch

release-minor: ## Release a minor version: anything players in a match share changed
	tools/release/cut.sh minor

release-major: ## Release a major version
	tools/release/cut.sh major
