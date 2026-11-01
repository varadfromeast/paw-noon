# First-run tutorial

Playtests: players swiped before DRAW, did not read the heartbeat as "wait", and missed the small swipe sign.
The tutorial teaches the three beats on the real duel stage (same cats, holster, SwipeCue, heart monitor, DRAW
cue, timer and jam) before the first solo match, and can be replayed from Settings.

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

`PawGame.BeginDuel` runs the tutorial before a solo match when `TutorialDirector.ShouldAutoRun` (first run, not a
review run), then calls `StartMatch`. Settings > Tutorial (REPLAY) calls `PawGame.ReplayTutorial`, which runs it and
returns to the title. On the first run the match intro is marked shown (the cats are already on their marks when
the match starts); a Settings replay leaves it alone. Card layout: centred on `layout.Cue.x`, top one pixel below the hud_badge art (canvas y 268..310 by default,
the ready card 250..310), so it never covers the badge's monitor, the opponent (hat top y 313), the guns or the
holster.

Tunables (client-only, `Resources/Data/feel_config.json`): `tutorialGripSeconds`, `tutorialHoldMinSeconds`,
`tutorialHoldMaxSeconds`, `tutorialLiftGraceSeconds`, `tutorialFailSeconds`, `tutorialVerdictSeconds`,
`tutorialSlowRetries`.

## Bridge

`ReviewDirector.Tutorial(mode)`:

- `on` run it at every StartDuel (review runs included), `off` never, `auto` default, `reset` auto + forget done
- `run` the Settings replay now (tutorial, then title)
- `grip` / `lift` / `real` simulate the thumb on / off the holster (or back to real input)
- `skip` SKIP, `start` TAP TO START (ready card only)

Swipes: `ReviewDirector.SimulateSwipe()` (the real input queue). `Status()` includes
`tutorial=<Step>(n/3),attempts:a,fail:F,reaction:0.312s tutorialDone=0|1 tutorialMode=auto|on|off`.

Example: `Tutorial("reset")`, `StartDuel()` is a review run so use `Tutorial("on")` + `StartDuel()` or `Tutorial("run")`,
then `Tutorial("grip")`, wait ~4 s for DRAW (`Status`), `SimulateSwipe()`, `Tutorial("start")`.
