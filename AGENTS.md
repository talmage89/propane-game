# AGENTS.md

Propane: a Godot 4.7.2 .NET (C#, Jolt) game. Read README.md, DESIGN.md and MULTIPLAYER.md
first. macOS (Apple Silicon) first, Linux second.

## Rules

- The owner sets the design. The "Decided" tables are rulings: present options neutrally and ask before changing them.
- Every gameplay or effect value is a `Tuning` tuner.
- `dev` is for work, `main` for releases. Never commit or push to `main`: a push publishes a public release. Release
  only with `make release-patch|minor|major`. Ask before pushing.
- A patch must play with every patch of its minor version. Anything players in a match share (messages, how the
  suburb is built, tanks, tuners) needs a minor release. Bump `Protocol.Version` when a message changes shape; Hello,
  Welcome and Rejected may only gain trailing fields.
- Public repo: no personal hosts, IPs or paths.

## Working

- `make` lists the tasks. Run `make build` before any scene; `make test` takes about 75 s.
- Verify with the scripted scenes in `scenes/dev` and review their screenshots. Test network changes with
  `tools/dev/net_bots.sh`.
- Don't commit `.import` churn from opening the editor.
- Update the docs with the code. Use British spelling and match the surrounding style.
