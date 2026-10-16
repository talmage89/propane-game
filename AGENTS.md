# AGENTS.md

Keep this file lean: only what an agent can't learn from the code and the other docs, in as few words as possible.

Propane: a Godot 4.7.2 .NET (C#, Jolt) game. Read README.md and design/ first, and
docs/multiplayer.md for network work. Windows, macOS (Apple Silicon) and Linux are all targets; development and
verification happen on Linux.

## Rules

- The owner sets the design: design/ and the game's current rules and feel. Present options neutrally and ask before
  changing them.
- Every gameplay or effect value is a `Tuning` tuner.
- `dev` is for work, `main` for releases. Never commit or push to `main`: a push publishes a public release. Release
  only with `make release-patch|minor|major`. Ask before pushing.
- `dev` may move fast but is always a release candidate: land only work that builds and passes `make test`.
- A patch must play with every patch of its minor version. Anything players in a match share (messages, how the
  suburb is built, tanks, tuners) needs a minor release. Bump `Protocol.Version` when a message changes shape; Hello,
  Welcome and Rejected may only gain trailing fields.
- Public repo: no personal hosts, IPs or paths.

## Working

- `make` lists the tasks. Run `make build` before any scene; `make test` runs all suites, forced formations and separate-process world hashes at bounded seed depth; `make check` adds lint and the gate self-tests (`make test-infra`); `make test-full` keeps the deep sweeps, and `make -j4 test-full` runs them as parallel jobs.
- Verify with the scripted scenes in `scenes/dev` and review their screenshots. Test network changes with
  `tools/dev/net_bots.sh`, giving each run a new `-o` directory (it refuses one that holds old logs unless you pass `-f`).
- Don't commit `.import` churn from opening the editor.
- Update the docs with the code. Use American spelling and match the surrounding style.

## Workspace

- The primary clone, `dev-0`, always has `dev` checked out. Commit there only when working directly on `dev`; never
  switch its branch.
- Its siblings `dev-1` to `dev-5` are long-lived git worktrees, one agent each. Check out any branch there, or
  `git switch --detach dev`. Git won't check out `dev` twice, so land a branch by merging it in `dev-0`.
- Worktrees share the repository, so commits and branches made in one are visible in all without a fetch.
- Keeping your worktree current is your job. Start work from the latest `dev`, and bring `dev` into your branch when
  the work depends on it (not when extending a separate branch that is meant to stay behind).
- After any checkout, switch, merge or pull, run `make setup` so the ignored state matches: it imports the assets into
  `.godot/` and compiles (about 25 s fresh, a few seconds after), and its `override.cfg` gives the checkout its own
  `user://`. Nothing else gitignored is needed: `build/` holds only release output and dev logs.

## Parallel agents

Each worktree has its own `.godot/`, build, `tuning.tres`, `user://` (after `make setup`), server port (24680 plus
its number) and bot logs in `build/`, so builds, scenes, servers, bots and Blender scripts run side by side. Still
shared:

- `/tmp`: give `--capture-dir` and `--record` absolute paths in the worktree, such as `$PWD/build/shots`.
- One GPU and CPU: `--fixed-fps` screenshots are unaffected, but soak frame times and `--record` are skewed by others'
  load.
- Blender scripts write only the worktree's `assets/`, but `.glb` files can't be merged: on a conflict, rerun the
  script after the merge.
