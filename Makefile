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
.PHONY: help setup run build lint check test server bots bots-stop package deploy release-patch release-minor release-major

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

test: build ## Run the headless regression tests (as CI does)
	"$(GODOT)" --headless --path . res://scenes/dev/movement_tests.tscn
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=grip --grip-sweep --assert-grip
	"$(GODOT)" --headless --fixed-fps 60 --path . res://scenes/dev/player_range.tscn -- --scenario=ragdoll --assert-grip
	"$(GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=100
	"$(GODOT)" --headless --path . res://scenes/dev/platform_tests.tscn

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
