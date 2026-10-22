# Paw Noon: feel notes

Presentation-only feel passes. Nothing here touches Core timing, `ShotReport`, `SwipeDraw` or the authority:
every moment reads the authority's round record after the fact. Tunables live in
`Resources/Data/feel_config.json` (client-only, `FeelConfig`), never in `duel_config.json`.

## Outcome moments (exchange readability + win moments)

Goal: each exchange's outcome reads in one glance, and winning feels like more than a number.

### 1. Verdict headline (round card)

The old gap row ("+73.72 ms FASTER") is now a colour-coded verdict (`Core/RoundVerdict.cs`, unit-tested,
`ResultPanel.ShowVerdict`):

| Kind | Long | Short | Minimal | Colour |
|---|---|---|---|---|
| Faster | `YOU WERE FASTER  +12 ms` | `FASTER +12 ms` | `+12 ms` | Green2 |
| Beaten | `PAWTRUTH BEAT YOU  -31 ms` (opponent plate name) | `BEATEN -31 ms` | `-31 ms` | PaintRed |
| Dead heat (inside the tie window) | `DEAD HEAT  +-3 ms` | `DEAD HEAT +-3 ms` | `DEAD HEAT` | Brass2 |
| Too early | `TOO EARLY!  wait for DRAW` | `TOO EARLY! wait for DRAW` | `TOO EARLY!` | PaintRed |
| No shot | `NO SHOT  swipe up from the holster` | `NO SHOT  swipe up!` | `NO SHOT` | PaintRed |
| Clean shot (he fouled / never shot) | `CLEAN SHOT!` | | | Green2 |

- The gap is whole milliseconds, rounded half up; under 0.5 ms one decimal (`0.4`). `font_medium` has no `±`, so
  the dead heat prints `+-` (the card switches to `±` by itself if the glyph is ever added).
- **Fit:** the card measures each form with `BitmapText.Width` and takes the longest that fits. The limit is the card's
  inner width (192 px), or, when the stamp will land on the verdict row, the space left of the stamp (it lands
  0.35 s later on top of the row). A grade stamp leaves 125 px, so on graded cards Faster / Beaten show the short form
  (`FASTER +74 ms`, 77 px); `YOU WERE FASTER  +12 ms` is 134 px. TOO EARLY and DEAD HEAT fit their long forms.
- The wide `stamp_too_early` (148 px) now lands across the times band (top right, over the "--") instead of the
  bottom-right corner, where it covered the verdict and the damage line.
- The tie card no longer rewrites the row to "TOO CLOSE" in sigOrange; the damage line still shows both hits. The
  in-world "TOO CLOSE!" beat is unchanged. (The share card keeps its own `ShareCardText.Gap` wording.)

### 2. Win streak

`Core/StreakCounter.cs` (unit-tested): an outright win (`RoundVerdict.Classify`: you shot, outside the tie window,
and faster than him, or he fouled / never shot / was dropped first) adds one; a loss, a foul or a no-shot resets to
zero; a tie or a void leaves it. Reset at every `StartMatch`.

- From `streakMin` (2) wins in a row: a brass pennant `tag_streak` with `x2`, `x3` ... pops in pinned to the grade
  stamp's lower-left edge, `streakTagDelay` after the stamp (later when a NEW BEST shares the card, so the two
  haptics never blur). Sound `sfx_streak` (two rising bell notes, pitch +1 semitone per extra win),
  `HapticMoments.StreakStep(n)`.
- From `streakRollMin` (3): the HUD round sign flashes **ON A ROLL** (`DuelHud.FlashRoundLabel`: pop, two
  yellow/white blinks, back to ROUND n / LAST LIGHT after `rollLabelSeconds`; a new round label ends it), and the
  next standoff's banter is `BanterContext.Rattled` (outranks everything but the match start, always speaks; lines
  under `"rattled"` in `characters.json`, built-ins for Pawtruth and Whiskers).

### 3. NEW BEST

When the exchange's reaction beats the all-time best (`PawSettings.SubmitBest` returned true: never for injected input,
i.e. auto-tap / SimulateSwipe / review), `newBestDelay` after the stamp: a SigYellow ribbon `tag_best` "NEW BEST!"
pops over the card's top edge above your time, a confetti burst (`confettiCount` whole-pixel motes, SigYellow +
Red1, tumbling 2x1 / 1x2, swaying, blinking out over the last fifth of `confettiSeconds`), `sfx_new_best` (A-major
arpeggio) and `HapticMoments.NewBest`. The confetti is a 48-slot `PixelParticles` pool under the round card (overlay
layer, unscaled time) because the world pool zooms, shakes and sits under the UI. The match screen's
"NEW ALL-TIME BEST!" uses the same `SubmitBest` record, so a match that showed the ribbon always ends with it.
A player's first recorded shot ever counts as a best (as on the match screen).

### 4. Kill-shot punch

On the hit that drops the opponent to 0 (`DuelEvent.killed`, victim = slot 1), once the kill slow-mo and any hit-stop
are over: `killPunchFlashFrames` (1) frames of solid white on him (`DuelActor.FlashFrames`) and a `killPunchKickPx`
(2 px) downward `PixelPipeline.Kick` (the existing kick, no second system).

### Reduced Motion

No confetti, no kill flash or kick, no tag drop-in / blinking (tags and ON A ROLL just appear). Sounds and the first
haptic tap stay, so the information is the same.

### Tunables (`feel_config.json`)

`streakMin` 2, `streakRollMin` 3 (0 = off), `streakTagDelay` 0.42 s, `rollLabelSeconds` 1.6 s, `newBestDelay` 0.2 s,
`confettiCount` 26 (max 48), `confettiSeconds` 0.8, `confettiSpeed` 210 px/s, `confettiGravity` 320 px/s²,
`killPunchFlashFrames` 1, `killPunchKickPx` 2.

### Art / audio

- `Tools/ArtGen/ui_v3/build_outcome_tags.py` draws `tag_streak` (17x14) and `tag_best` (22x16) procedurally (house
  palette, alpha 0/255, ink outline, ink2 drop shadow, 9-slice in x), writes their `ui.json` entries and
  `preview_outcome_tags.png`.
- `Tools/AudioGen/gen_sfx.py sfx_streak sfx_new_best` (48 kHz mono, onset at 0, -1 dBFS peak), preloaded by PawAudio.

### Review hooks (`ReviewDirector.Show`)

```
call PawNoon.Runtime.ReviewDirector.Show roundresult faster
call PawNoon.Runtime.ReviewDirector.Show roundresult beaten
call PawNoon.Runtime.ReviewDirector.Show roundresult deadheat
call PawNoon.Runtime.ReviewDirector.Show roundresult early
call PawNoon.Runtime.ReviewDirector.Show roundresult noshot
call PawNoon.Runtime.ReviewDirector.Show roundresult clean
call PawNoon.Runtime.ReviewDirector.Show roundresult faster streak2
call PawNoon.Runtime.ReviewDirector.Show roundresult faster streak3 newbest
call PawNoon.Runtime.ReviewDirector.Show rattled
call PawNoon.Runtime.ReviewDirector.Show killpunch
call PawNoon.Runtime.ReviewDirector.Haptic streakx2
call PawNoon.Runtime.ReviewDirector.Haptic newbest
```

`roundresult` variants use sample values and never touch the stored best. The stamp lands at once; the ribbon ~0.2 s
and the streak tag ~0.42 s after the call (0.7 s with a NEW BEST); the confetti is mid-air ~0.3-0.6 s after the call.
In play the same beats follow the card's 0.35 s stamp delay. `killpunch` holds the
flash for 30 frames so a capture catches it (in play it is one frame). `rattled` holds ON A ROLL and the bubble 6 s.
