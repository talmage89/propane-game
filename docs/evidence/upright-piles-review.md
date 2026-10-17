# Upright pile review, 2026-10-11

**Status:** Dated Linux record moved out of [upright-piles.md](../upright-piles.md), the contract, which keeps the
construction rule and how to reproduce the proof. The counts below are those of the review, not acceptance bounds.

The normal probe held 49 piles / 354 tanks for 120 simulated seconds with 60 real player passes: maximum drift
0.451 mm and tilt 0.086 degrees. The deep awake probe held 53 piles / 738 tanks, including both 128-tank shapes:
1.669 mm drift and 0.151 degrees. Both exceeded the 30-degree toppling threshold in every disturbed multi-tier
fixture. After site clearance, nine original tanks regathered in the normal run and eight in the deep awake run.
Headless `tank_range` runs with `--chain=fresh` and `--chain=primed` passed for both shapes at count 14:
all 13 neighbors kept venting after one blast, or detonated only after their fuses when already primed.

Rendered trials at 1280×720 used the range's 1.8 m camera. Awake square (14 tanks) and triangular (14 and 20 tanks)
contact trials remained still; shell hits, an intact shove and a triangular base blast produced physical falls.
The full triangular shell-hit sequence is particularly clear: four tiers stand, the top vents sideways, then tanks
roll down the front and off the side. Captures are retained locally under `build/upr-*`.

The four-bot 90-second `--melee-start` soak at 120 ms lag / 30 ms jitter / 3% loss recorded 14 accepted swings and
seven player contacts. `check_scores.py` passed: 29 map gains + 13 drop gains - 23 spills = 19 banked, agreed by all
four clients. `compare_tanks.py` completed 424 comparisons: final ids/states agreed and final drift was at most
0.340 m; active-chain differences peaked at ten states and 6.523 m. This is a comparison diagnostic, not a drift
threshold assertion. These results do not change body synchronization. Windows and macOS were not tested locally.
