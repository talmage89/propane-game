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
.PHONY: help setup run build lint check test test-core test-behavior test-generator test-world test-full server bots bots-stop package deploy release-patch release-minor release-major

help: ## List the tasks
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN { FS = ":.*## " } { printf "  make %-14s %s\n", $$1, $$2 }'

setup: ## Import the assets and compile: a fresh clone, or after switching branches or pulling
	@printf '; Written by make setup: user:// for this checkout only.\n[application]\nconfig/use_custom_user_dir=true\nconfig/custom_user_dir_name="Propane-dev/%s"\n' "$(CHECKOUT)" >override.cfg
	"$(GODOT)" --headless --path . --import >/dev/null 2>&1 || true
	@$(MAKE) --no-print-directory build

run: build ## Play from source
	"$(GODOT)" --path .

build: ## Compile the C# code
	dotnet build Propane.sln -c Debug -nologo -v quiet

lint: ## Check C# formatting and compile with warnings as errors
	dotnet format Propane.sln --verify-no-changes
	dotnet build Propane.sln -c Debug --no-restore --no-incremental -nologo -v quiet -warnaserror

# A recipe keeps lint before test even when make runs with -j.
check: lint ## Run lint, then the tests
	@$(MAKE) --no-print-directory test

# These same partitions run as separate CI jobs. Shared build runs once, even with make -j.
test: test-core test-behavior test-generator test-world ## Run the fast headless gate (the same suites as CI)

test-core: build ## Run checker, platform, movement and unit assertions
	GODOT="$(GODOT)" python3 -B tools/dev/check_test_threads.py
	python3 -B tools/dev/check_world_tests.py
	python3 -B tools/dev/time_gate_tests.py
	python3 -B tools/dev/check_scores_tests.py
	python3 -B tools/dev/compare_vents_tests.py
	python3 -B tools/dev/compare_tank_paths_tests.py
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/core_tests.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/shutdown_tests.tscn
	"$(GODOT)" --headless --path . res://scenes/dev/shutdown_tests.tscn -- --paused --window-close --repeat-quit

test-behavior: build ## Run isolated physics, navigation, menu, grip and feet probes
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/world_services_tests.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/blast_query_tests.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/gather_range.tscn
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/gather_range.tscn -- --scenario=grant-races
	"$(GODOT)" --headless --path . res://scenes/dev/gather_range.tscn -- --scenario=appearance
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/game_test.tscn -- --scenario=gather --tune.FixedSeed=23
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/game_test.tscn -- --scenario=clear --quit=32 --tune.FixedSeed=23
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/melee_range.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=melee --assert-motion --assert-grip
	"$(GODOT)" --headless --path . --fixed-fps 60 res://scenes/dev/bot_navigation_course.tscn -- --seeds=5
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=gamepad --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=devices --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=input --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=mouse --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=ragdoll --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --assert-feet --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet --feet-corrections --assert-feet --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=feet-events --assert-feet --assert-grip --assert-motion
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=movement --assert-motion --assert-grip
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=motion --assert-motion --assert-grip

test-generator: build ## Check every generator assertion with bounded seed depth
	"$(GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=20 --large-seeds=3 --formation-seeds=2

test-world: build ## Compare all formations built in two fresh processes
	tools/dev/check_world.sh

test-full: test ## Run the gate plus deep sweeps
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/menu_test.tscn -- --scenario=lifetime --port=$(PORT)
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=melee-reloads --assert-motion --assert-grip
	"$(GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=200 --large-seeds=100 --formation-seeds=6
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/bot_navigation_course.tscn -- --seeds=100

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
