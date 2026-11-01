# First-run tutorial

Playtests: players swiped before DRAW, did not read the heartbeat as "wait", and missed the small swipe sign.
First-run players now get the **HOW TO DUEL comic** (about 10 s, below) before their first PLAY or PVP. The longer
interactive **practice round** (the rest of this page) teaches the same three beats on the real duel stage; it no longer
runs by itself and is replayed from Settings > Practice.

## HOW TO DUEL comic

A modal over the town (title backdrop): three comic panels built from the game's own sprites. Each panel is panel_wood
with panel_paper inside, a font_medium caption, a font_small sub-line, a 220x168 looping scene with a 1 px ink
frame, and page dots 1-2-3. SKIP is top-right, "TAP FOR NEXT" under beats 1-2, a btn_large **GOT IT!** under beat 3.
Panels slam in (whole-pixel drop `-slam, -slam/2, 0, -2, 0`, whoosh, then a stamp thud and one white frame over the
scene on landing), turn by themselves after `howToBeatSeconds`, and any tap turns the page at once. Beat 3 loops until
GOT IT. The whole block (275 px tall, 244 wide) is centred in the 270x480 core, clamped to the safe area.

| Beat | Caption / sub | Scene (scene time) |
|---|---|---|
| 1 | PAW ON THE HOLSTER / rest your thumb here | Sand halftone. The SwipeCue thumb pad drops onto holster_zone's grip in 4 steps (18, 12, 7, 3 px), lands at 0.24 s ("PAT", soft `sfx_ui_tap`, UiTap haptic, grip glows = frame 1), bounces 3/5/2 px, second pat at 0.48 s ("PAT", higher pitch), rests. Whiskers' portrait rises from the bottom-left in three 12 px steps at 0.8 s, "comfy." at 0.98 s. Lifts off at 1.45 s; loops twice per page |
| 2 | HEARTBEAT = WAIT / twitch early and your gun jams | Night hatch. A brass-bezel mini EcgMonitor (118x40) with the duel's WAIT word (font_code) that thumps yellow + 1 px on every R peak (`sfx_heartbeat` + the hold haptic). Pawtruth's portrait side-eyes from the bottom-right corner (two thirds up). At `howToJamAtSeconds` (1.5 s) the thumb twitches up 3, 6 px: smoke_puff from the holster, stamp_too_early drops in, "JAM" (SigRed), WAIT turns red, `sfx_misfire` + `sfx_stamp`, Foul haptic, comic shake; the thumb settles back (8, 5, 2, 0 px); Pawtruth rises all the way for "heh." |
| 3 | DRAW! SWIPE UP / fastest paw wins | Sunburst behind Pawtruth (bandit_idle frame 0, bust crop). Thumb resting on the glowing grip (SwipeCue Wait). At `howToDrawAtSeconds` (0.6 s) cue_draw drops in with one white frame, `sfx_draw`, the real SwipeCue flicks up its chevrons (Go), Swipe haptic, and a font_timer counts from 0.000. At DRAW + `howToReactionMs` (0.214 s): BANG (font_code), hero gun sfx, muzzle_side, the holster shows empty (frame 2), the timer freezes brass at "0.214 s", Pawtruth flashes white for a frame, flash_ring at his crown, scene flash + shake, recoil haptic; his hat (ears included) hops 4..11 px off his head and drops back with a 2 px bounce (his fur fills the gap). +0.45 s indignant meow (`sfx_meow_pawtruth`) and a sweat drop, +0.5 s smoke curls from the hat's bullet hole. Loops every `howToDrawLoopSeconds` |

**Reduced Motion:** no slam, no landing / BANG white frames, no shake, no DRAW / word / stamp drops, WAIT does not
thump; the panels simply appear (with the stamp thud) and every sound and haptic stays.

**When it shows.** First PLAY or PVP while `PawSettings.TutorialDone` is false: the comic opens over the title, and
GOT IT / SKIP set `TutorialDone` (PlayerPrefs `pn_tutorialDone`) and then start that match as before. The title's
HOW TO PLAY button (top-right row) opens it any time and returns to the title (it also marks the lesson done).
Settings > **Practice** (REPLAY) runs the interactive practice round below, then returns to the title.

**Code** (`Code/Runtime/HowTo/`): `HowToComic` (the ScreenBase modal: frame, captions, dots, SKIP, GOT IT, page
timer, slam / flash / shake; orders 398..421), `HowToStage` (scene rect + offset, crops, procedural backdrops, white
flash, comic words), `HowToBeat` (looping scene base: picture = function of scene time, events on crossings),
`HowToBeatHolster` / `HowToBeatWait` / `HowToBeatDraw`, `HowToTuning`, `ReviewDirector.HowTo`. Wiring in
`PawGame.ShowHowTo(HowToThen)` / `FinishHowTo()`. Allocation-free per frame.

**Tunables** (`feel_config.json`): `howToBeatSeconds` 3.3, `howToSlamPx` 14, `howToSlamStepSeconds` 0.04,
`howToFlashFrames` 1, `howToShakePx` 2, `howToJamAtSeconds` 1.5, `howToDrawAtSeconds` 0.6, `howToReactionMs` 214,
`howToDrawLoopSeconds` 3.6 (raised to fit DRAW + shot + 1.6 s).

**Bridge** `ReviewDirector.HowTo(arg)`: `show` (open over the title), `beat N [t]` (jump to beat 1..3 and hold it, no
page turn; with t the scene also freezes at t seconds: `beat 1 1.0` comfy, `beat 2 1.7` the jam, `beat 3 0.75` DRAW
+ thumb, `beat 3 0.95` the hat in the air), `next`, `close` (GOT IT), `skip`. `Status()` includes
`howto=off|beatN[(hold[,frozen@t])]`.

# Practice round (interactive)

The practice teaches the three beats on the real duel stage (same cats, holster, SwipeCue, heart monitor, DRAW
cue, timer and jam). It runs from Settings > Practice, or before every StartDuel with the bridge's `Tutorial("on")`.

## Flow

| Card | Step (`TutorialFlow`) | Advances when | On screen |
|---|---|---|---|
| 1/3 REST YOUR THUMB ON THE HOLSTER | `Grip` | a finger rests in the holster zone for `tutorialGripSeconds` (0.6 s) without a break | SwipeCue Grip, holster glows while resting, PRACTICE round sign |
| 2/3 WAIT... DON'T MOVE ("The heartbeat means WAIT. Swipe now and you lose the round.") | `Hold` | the hold ends after a seeded uniform `tutorialHoldMin..MaxSeconds` (2.8..4.2 s) | the real hold: `EnterHold` + `TickHold` (heart monitor via `SetHoldMood(true)`, heartbeat sfx + haptic, push-in, hold vignette, haptic warm-up, ambient freeze), SwipeCue Wait |
| 2/3 TOO EARLY! / STAY ON THE HOLSTER / 3/3 NO SHOT! | `Fail` | after `tutorialFailSeconds` (1.8 s) | swipe: the real jam (foul anim, TOO EARLY stamp, misfire, error haptic); lift: misfire + warning haptic; no shot: NO SHOT stamp. Card shakes |
| 2/3 THUMB BACK ON THE HOLSTER / ONE MORE TIME | `Regrip` | rest again for `tutorialGripSeconds` | back to step 2 (a new hold length each attempt) |
| 3/3 DRAW! SWIPE UP! | `Draw` | a valid swipe, an early one (Fail), or the no-shot timeout (Fail) | the real `PresentDraw` (burst, cue_draw, flash, DSP-scheduled sfx + haptic, SwipeCue Go), live 6-decimal timer |
| 3/3 "0.312 s - NICE!" (LIGHTNING!/PERFECT! above GREAT) or "0.812 s - SLOW" | `Verdict` | after `tutorialVerdictSeconds` (1.8 s) | the real hero draw (draw_fire, muzzle, recoil; nobody is hit), frozen timer, the stamp haptic for the grade. SLOW/SLOWPOKE go back to `Regrip` up to `tutorialSlowRetries` (1) times |
| YOU'RE READY. PAWTRUTH IS WAITING. + TAP TO START (replay: BACK TO TOWN) | `Ready` | the button | hold presentation off, timer hidden |

SKIP (bottom-right of every step card) ends it at once. Finishing or skipping sets `PawSettings.TutorialDone`
(PlayerPrefs `pn_tutorialDone`). Players whose prefs predate the key but have a stored best count as done.

Grades come from `duel_config.json` (`gradeGoodMs` etc. via `Grading.FromMs`); a draw under `anticipationMs` is a
guess and fails as TOO EARLY, exactly as in a match.

## Timing: same path as a match

The tutorial never touches `LocalAuthority`, `DuelClock`, `ShotReport` or `SwipeDraw` code. It plays the
authority's part for one private `DuelClient` on a private `LoopbackTransport`:

- at the hold it broadcasts a `RoundStartMsg` and polls it at frame start (presses before that are ignored);
- when the hold ends it broadcasts a `DrawCue`; `PollCue(frameStart, director.PresentLatencyNs)` stamps
  `CuePresentedNs` and the director presents DRAW that same frame;
- presses are dequeued from the same `SwipeDraw` queue and classified by `DuelClient.Press` (Early before the cue
  photons, Shot = inputNs - CuePresentedNs, TooLate after the no-shot window); `client.Tick` closes the window.

Practice reports are drained and dropped: no authority, no bot shot, no all-time best, no recording.

## Detection

- **Thumb on the holster**: `SwipeDraw.FingerResting` (a touch / mouse track that began inside the holster zone and
  has not crossed the swipe threshold, or a legacy touch inside the zone). `DuelHud.SetResting` makes the holster
  glow while it is true in steps 1-2.
- **Early swipe**: a press the client classifies `PressResult.Early`, or a Shot under the anticipation floor.
- **Lift during the hold**: `FingerResting` false for longer than `tutorialLiftGraceSeconds` (0.2 s, touch jitter).
- **App backgrounded** (device only, `DuelDirector.VoidForLifecycle`): a hold or DRAW in flight goes back to
  `Regrip` ("THUMB BACK ON THE HOLSTER") via `TutorialFlow.Interrupt`, with no fail card, sound or haptic; the
  practice round is dropped. Other steps carry on.

## Code

| File | Role |
|---|---|
| `Code/Core/TutorialFlow.cs` | Engine-free step machine (`TutorialStep`, `TutorialFail`, `TutorialTuning`); `Revision` bumps on every transition. Tests: `Tests/EditMode/TutorialFlowTests.cs` |
| `Code/Runtime/Tutorial/TutorialDirector.cs` | Feeds the flow (thumb, presses, buttons), presents each transition once, owns the private client / transport, persists `TutorialDone`, calls back |
| `Code/Runtime/Tutorial/TutorialCard.cs` | The paper card: panel_paper under the HIGH NOON badge, animated paw, title, two lines, step dots, SKIP, TAP TO START (btn_large). Whole-pixel drop-in and shake; none under Reduced Motion |
| `Code/Runtime/DuelDirector.Tutorial.cs` | The seam: `AttachTutorial(card)`, `StartTutorial(Action<bool> onDone, bool replay)`, `Pres.Tutorial` tick, stage hooks reusing `EnterHold`, `PresentDraw`, `PresentHeroDraw`, `PresentHeroFoul` |
| `Code/Runtime/Tutorial/ReviewDirector.Tutorial.cs` | Bridge hook |

`PawGame.BeginDuel` runs the practice before a solo match only when `TutorialDirector.ShouldAutoRun` (bridge mode
`on`; the first-run lesson is the comic now), then calls `StartMatch`. Settings > Practice (REPLAY) calls
`PawGame.ReplayTutorial`, which runs it and returns to the title. On the first run the match intro is marked shown (the cats are already on their marks when
the match starts); a Settings replay leaves it alone. Card layout: centred on `layout.Cue.x`, top one pixel below the hud_badge art (canvas y 268..310 by default,
the ready card 250..310), so it never covers the badge's monitor, the opponent (hat top y 313), the guns or the
holster.

Tunables (client-only, `Resources/Data/feel_config.json`): `tutorialGripSeconds`, `tutorialHoldMinSeconds`,
`tutorialHoldMaxSeconds`, `tutorialLiftGraceSeconds`, `tutorialFailSeconds`, `tutorialVerdictSeconds`,
`tutorialSlowRetries`.

## Bridge

`ReviewDirector.Tutorial(mode)`:

- `on` run it at every StartDuel (review runs included), `off` / `auto` never (default), `reset` auto + forget done
  (the next PLAY / PVP shows the HOW TO DUEL comic again)
- `run` the Settings replay now (tutorial, then title)
- `grip` / `lift` / `real` simulate the thumb on / off the holster (or back to real input)
- `skip` SKIP, `start` TAP TO START (ready card only)

Swipes: `ReviewDirector.SimulateSwipe()` (the real input queue). `Status()` includes
`tutorial=<Step>(n/3),attempts:a,fail:F,reaction:0.312s tutorialDone=0|1 tutorialMode=auto|on|off`.

Example: use `Tutorial("on")` + `StartDuel()` or `Tutorial("run")`,
then `Tutorial("grip")`, wait ~4 s for DRAW (`Status`), `SimulateSwipe()`, `Tutorial("start")`.
