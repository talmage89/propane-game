# A11b prototype review frames

Rendered on the build VM's CPU (llvmpipe) at 1280×720, `--fixed-fps 30`, from a high overview camera with a bot on
every actor. Timings are compressed so the events fit a short capture: Ring Out with 4 s bands and a 2 s warning,
King with a 10 s move period; everything else is at the shipped dials. Reproduce with `lab_test.tscn -- --scenario=prototype
--mode=ringout|king --overview --windowed --tune.RingOutBandSeconds=4 --tune.RingOutWarningSeconds=2
--tune.KingMoveSeconds=10 --shots=... --until=... --capture-dir=...`.

| Frame | What to look at |
| --- | --- |
| `ringout-1-start.png` | The 5 × 5 slab, the pile ring, the HUD: standing count, knock-offs, the clock to the next dissolve. |
| `ringout-2-warning-pulse.png` | A band of the rim pulsing before it goes; the banner; a pile blast. |
| `ringout-3-dissolved-band-and-next-warning.png` | The first band gone (lots and colliders), the next one pulsing. |
| `ringout-4-debris-falling-into-void.png` | Two bands gone; debris falling through into the void. |
| `king-1-start-zone-beam.png` | The zone's beam and ground ring on its pile, the bank and minted counters. |
| `king-2-contest.png` | Several players in the zone: the beam and ring pulse red. |
| `king-3-move-warning.png` | "The zone moves in 1" and the pale beam at the next spot (right edge). |
| `king-4-moved-fresh-pile.png` | The zone on its new spot, tinted with its holder, over the fresh pile. |
