# Mode scripts

`scenes/dev/mode_test.tscn` feeds each `*.mode` script here into a mode's pure rules and checks their state, with
no world, transport or clock (`-- --script=NAME` runs one). It runs in `make test`. One command per line; `#` starts
a comment.

| Command | Meaning |
| --- | --- |
| `mode tankbank` | Create the rules of a registered mode (its name without spaces). |
| `tune Name=value ...` | The tuner values the next `actors` copies. |
| `actors 1=4 2=0` | Initialize: actor ids with their starting scores, in roster order. |
| `retune Name=value` | A tuning change, as an explicit input. |
| `phase round ends=150 [round=N]` | Enter a phase; `ends` sets its server-time deadline. |
| `now 12.5` | Move the server clock (no input is sent). |
| `detonate 1 map` / `detonate 1 drop` | The ledger accepted a detonation credited to actor 1. |
| `spill 2 7 thrown 3 [life=1]` | Actor 2's owner requests ticket 7: a throw spilling three tanks. |
| `register 2 7` / `fail 2 7` | The ledger registered or refused that ticket's batch. |
| `leave 2` / `join 5=0` | Departure, or a late join with a starting score. |
| `expect last ...` | The commands from the latest input, `; `-separated, or `none`. |
| `expect bank 1=3`, `reserved 1=2`, `stats 1=gained/hits/throws` | Tank Bank state. |
| `expect standings 2=6(left) 1=4` | Ranked standings; departed actors are marked. |
| `expect audit initial=... ok=True` | The conservation audit line, after `bank audit `. |
| `expect open true` | Whether the world gate is open on the current clock. |
