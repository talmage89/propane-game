# Multiplayer verification records

**Status:** Dated results that were part of [multiplayer.md](../multiplayer.md) (the contract), moved here so it states
only how multiplayer works. They are records of the packages that built each part, on Linux and Protocol 2, not
current rules or acceptance bounds. The checks they describe now run as the commands in that document's Testing table.

## Bot comparison (date not recorded)

Measured over two-minute matches of four bots each, before and after (the server logs each player's tanks gained,
lost to hits and lost to throws at the end of a match): the first bot often threw itself with its own blasts and
ended with almost nothing; the current bots never throw themselves on purpose, and the leader usually ends with 8–17
banked. Against the first bot, two current bots ended 15 and 14 to 1 and 0, and 8 and 6 to 3 and 1.

## Soak results, 2026-10-09

Results, 2026-10-09: with three bots under 120 ms round trip, 30 ms jitter and 3% loss, every bot agreed on which tanks existed and their states throughout; resting tanks matched exactly and venting tanks to within about half a meter. The same held through the Docker server on another machine, over the LAN and over a VPN. When the match's creator left mid-match, the others played on in step. A client with a different protocol was turned away with a message.

## Melee verification, 2026-10-11

Verified on Linux, 2026-10-11: the lagged duel spilled exactly five tanks once, stunned the empty bank without a
second drop, and completed both get-ups. Both clients agreed with the bank audit (10 initial minus 5 spilled = 5).
The final 90-second close-pair soak recorded 14 accepted swings and eight player contacts across four smart bots,
including strikes while repositioning for tanks, followed by ordinary navigation, tank shooting and scoring. All four
final standings agreed: 20 map gains + 34 drop gains - 50 spills = 4 banked. The 407 tank comparisons ended with
identical ids/states and at most 0.165 m drift. During active chains the diagnostic reported transient differences
up to five states and 5.862 m drift; it is a
comparison report, not a threshold assertion. Those transients converged in the original melee package.
The later [tank assertion and correction work](network-corrections.md) adds automatic failure checks and fixes
the replication causes identified by repeated soaks.
The [independent integration review](network-integration.md) records A4 integration, native regressions,
remaining capacity limits and the verification matrix for this branch.
