# Everyday tasks. `make` lists them. The scripts they run live in tools/ and work on their own too.
# GODOT is the Godot .NET editor: godot-mono or godot on PATH, else the macOS app. Set it to override.
GODOT ?= $(shell command -v godot-mono || command -v godot || echo /Applications/Godot_mono.app/Contents/MacOS/Godot)
export GODOT

N ?= 4

.DEFAULT_GOAL := help
.PHONY: help run build test server bots bots-stop package deploy release-patch release-minor release-major

help: ## List the tasks
	@grep -E '^[a-z-]+:.*## ' $(MAKEFILE_LIST) | awk 'BEGIN { FS = ":.*## " } { printf "  make %-14s %s\n", $$1, $$2 }'

run: build ## Play from source
	"$(GODOT)" --path .

build: ## Compile the C# code
	dotnet build Propane.sln -c Debug -nologo -v quiet

test: build ## Run the generator tests (as CI does before a release)
	"$(GODOT)" --headless --path . res://scenes/dev/generator_tests.tscn -- --seeds=100

server: build ## Run a multiplayer server here (PORT=24680)
	"$(GODOT)" --headless --path . -- --server $(if $(PORT),--port=$(PORT))

bots: build ## Add N bots to a server's first open lobby (SERVER=host[:port], default this computer)
	tools/dev/add_bots.sh -n $(N) $(if $(SERVER),-S $(SERVER))

bots-stop: ## Stop the bots
	tools/dev/add_bots.sh -x

package: ## Build the macOS and Linux release archives here, in build/dist (needs rcodesign)
	tools/release/build.sh "$(GODOT)"

deploy: ## Run the server in Docker over ssh: make deploy HOST=host DIR=dir (from main, the latest release)
	tools/server/deploy.sh "$(HOST)" "$(DIR)" "$(GODOT)"

release-patch: ## Release a patch (plays with the same minor): fixes local to each player
	tools/release/cut.sh patch

release-minor: ## Release a minor version: anything players in a match share changed
	tools/release/cut.sh minor

release-major: ## Release a major version
	tools/release/cut.sh major
