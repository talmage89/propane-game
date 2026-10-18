# Propane

[![CI](https://github.com/talmage89/propane-game/actions/workflows/ci.yml/badge.svg?branch=dev)](https://github.com/talmage89/propane-game/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/talmage89/propane-game)](https://github.com/talmage89/propane-game/releases/latest)

**Shoot propane tanks in a toy suburb and enjoy what happens next.**

![The player is thrown into the air as a pile of propane tanks goes up beside a house and a car](docs/readme_still.jpg)

Propane is a third-person sandbox built with Godot 4.7.2 (.NET, C#) and Jolt physics. You run around a procedurally
generated suburb that floats in a white void, find the propane tanks stacked in the yards, and shoot them. The first shot
punctures a tank and it vents fire forever, pushed around by its own jet. The second shot blows it up, throwing cars,
bins, fence panels and you. Tanks sit in piles so that blasts chain from one to the next. Clear every tank and a new
suburb assembles around you. In single player there is no health and no score. The physics is arcade, built for readable
cause and effect rather than realism, and a match lasts a few minutes.

![A tank is punctured and vents, then the pile goes up and smoke fills the yard](docs/readme_chain.gif)

## Features

- **Tanks that behave.** A punctured tank vents fire and drives itself into things. A blast acts as a shot on every
  tank near it, so a pile goes up one after another.
- **Everything moves.** Cars, bins, fence panels, tanks and players fly when a blast reaches them, and debris stays
  where it lands. A blast throws you as a ragdoll.
- **A new suburb every round.** Streets, houses, yards, fences and tank piles are generated from a seed. A cleared
  suburb dissolves and a new one materializes.
- **Multiplayer.** Tank Bank is a free-for-all for 2 to 16 players where every tank you blow up is banked as a point and
  being shot spills yours. In King of the Cul-de-sac you hold a moving zone alone to mint banked tanks. Both modes offer 1×
  to 8× tank supply, and anyone in a lobby can add Easy, Normal or Hard bots.
- **A Lab.** A huge upright stack, a shooting range, a duel against the banking bot, free tuning, and prototype modes
  (Ring Out, King of the Cul-de-sac, Short Fuse, Dissolve Run) whose rules can be judged before anything ships.
- **Live tuning.** Every gameplay and effect value is a dial you can move in game (F1).
- **Keyboard and mouse, or a gamepad** (Xbox and PlayStation layouts), on Windows, macOS (Apple Silicon) and Linux.

## Download and play

Builds for Windows (x86_64), macOS (Apple Silicon) and Linux (x86_64) are on the
[latest release](https://github.com/talmage89/propane-game/releases/latest). Each build carries its own .NET runtime, so
nothing else needs installing.

- **Windows:** extract the ZIP and run `Propane.exe`, keeping all the extracted files together. The build is not code
  signed, so Windows SmartScreen may block the first launch: click **More info**, then **Run anyway**. Needs a GPU with
  Vulkan drivers.
- **macOS:** unzip and open `Propane.app`. The app is not notarized by Apple, so the first launch is blocked: open
  System Settings > Privacy & Security and click **Open Anyway**. Downloading with
  `gh release download --repo talmage89/propane-game` skips the prompt.
- **Linux:** extract the archive and run `./Propane.x86_64`. Needs a GPU with Vulkan drivers.

The game opens on the main menu: Single player, Multiplayer, Lab or Quit. A downloaded game checks GitHub for a newer
release at launch and, if there is one, offers this platform's archive on the main menu; updating is up to you.

## Controls

| Keyboard / mouse | Gamepad (Xbox / PlayStation) | Action |
| --- | --- | --- |
| W A S D | Left stick | Move |
| Shift (hold) | Left stick click (hold) | Sprint |
| Space | A / Cross | Jump |
| Mouse | Right stick | Look |
| Left click | Right trigger | Fire (hold for full auto; toggle it in the tuning panel) |
| Right click (hold) | Left trigger (hold) | Aim: walk slowly with a tight spread |
| F or either mouse thumb button | Right stick click (R3) | Rifle-butt strike: knocks people down and shoves tanks intact |
| R | X / Square | New suburb (single player); reload (multiplayer) |
| Esc | Start / Options | Pause and show the controls (a match keeps running) |
| F1 or the backtick key | — | Tuning panel (live sliders) |
| F11 or Alt+Enter | — | Fullscreen |

In play the game captures the mouse; click the window to recapture it after switching away. Menus work with the mouse, the
keyboard, or a gamepad's D-pad or left stick with A / Cross to confirm and B / Circle to go back. The
[developer guide](docs/development.md#controls-and-input) has every binding with its speeds and the rules for how
sprint, aim, reload and melee interact.

## Playing together

Choose **Multiplayer**, type your name and the server's address (`host` or `host:port`; the default is UDP port 24680),
then create a lobby or join one. A lobby is a shared void world where everyone roams, shoots and throws tanks while
waiting; Tab (View on a controller) opens its panel. Whoever created the lobby sets capacity, mode, match length and tank
supply, and starts the match. After the match everyone is back in the lobby.

**Host on this computer** runs a server inside your game, and the lobby shows the addresses friends can use. On Windows
or macOS, allow incoming connections the first time you host if the system asks.

Versions with the same major and minor number play together (0.4.0 with 0.4.3); a server turns away any other and says
which version it needs.

The dedicated server is the same game run headless, `./Propane.x86_64 --headless -- --server [--port=24680]`, and
`tools/server/deploy.sh <ssh host> <dir>` runs it from a clean release-tag checkout in Docker on any machine you can reach over ssh. See
[hosting the server](docs/multiplayer.md#hosting-the-server) for reaching it.

## Building from source

Propane needs Godot 4.7.2 (.NET edition) and the .NET 10 SDK.

```sh
make setup   # import the assets and compile; run it again after switching branches or pulling
make run     # play from the command line, or open project.godot in the editor and press F5
make check   # lint, the headless test gate and the gate's own self-tests
make         # list every task: tests, a local server, bots, benchmarks, releases
```

The [developer guide](docs/development.md) covers running from source, the quality gate, tuning, the repository layout,
the scripted test scenes, and how versions and releases work.

## Documentation

- [docs/README.md](docs/README.md) says which documents are current contracts, which are runbooks, and which are dated
  evidence.
- [Developer guide](docs/development.md): building, testing, tuning, harnesses, releases.
- [Multiplayer](docs/multiplayer.md): the networking model, Tank Bank's rules, lobbies, bots and hosting.
- [design/](design/): the owner's standing decisions. [design/VISION.md](design/VISION.md) is what the game is and must
  stay.
- [CREDITS.md](CREDITS.md): the third-party assets and their licenses.

<!-- License: this repository declares none yet. Add a LICENSE file and a "License" section here once one is chosen. -->
