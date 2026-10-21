# Paw Noon — code map

```
Assets/PawNoon/
  Code/Core/      PawNoon.Core (pure C#, noEngineReferences)   rules, timing, authority seam
  Code/Runtime/   PawNoon.Runtime (Unity)                       bootstrap, rendering, presentation
  Code/Editor/    PawNoon.Editor                                project setup, import rules, menu
  Code/Tests/EditMode/                                          NUnit tests for Core
  Plugins/iOS/PawHaptics.mm                                     Core Haptics bridge (UIKit fallback), Docs/HAPTICS.md
  Plugins/Android/PawHapticsBridge.java                         Android vibrator bridge (primitives / waveforms)
  Resources/Shaders/  PixelUpscale.shader, SpriteFx.shader
  Resources/Data/duel_config.json                               every tunable + layout
```

## Core (no UnityEngine)
| File | Role |
|---|---|
| `DuelConfig.cs` | All tunables (schedule, damage anchors, grades, adaptive bot, feints, timing calibration, juice). `FromJson` + `Sanitize`. |
| `PnJson.cs` | Tiny tolerant JSON reader (also used by Runtime for the art manifests). |
| `DamageCurve.cs` | Piecewise-linear damage, `Grade`, `Grading`. |
| `DuelClock.cs` | `IDuelClock`, `StopwatchClock` (exact ticks→ns), `ManualClock`, `TimeConv`. |
| `RoundSchedule.cs` | Seeded countdown beats + uniform hold (authority-private). |
| `Protocol.cs` | `RoundStartMsg`, `DrawCue`, `ShotReport`, `IDuelTransport`, `LoopbackTransport`. |
| `DuelClient.cs` | A duelist's measurement endpoint: presents cue, classifies presses, sends reports. |
| `BotModel.cs` | Normal sampling; `BotClient` (adaptive mean 300→220 ms / sd 50→30 over `botAdaptRounds` player round wins, rare foul from round 2) on the same client path; `BotActing` (presentation-only hold acting: 0–2 feints never within 350 ms of the cue, more when the player has been drawing early ("itch"), ~20 % late tell 150 ms before the cue). |
| `LocalAuthority.cs` | Round state machine (Standoff, Countdown, Hold, Draw, Resolving, Result), report validation, ordering + 15 ms tie window (both land, `isTie`, no round winner), kill-stops-second-shot, fouls/void/no-shot, events. |
| `MatchState.cs` | HP, round number, round wins, best/average reaction, `Finish`/`IsDraw` (double KO in a tie = drawn match, Winner −1). |
| `Banter.cs` | Per-character lines (`BanterLines`; data in `Resources/Data/characters.json`, built-in fallback per context), `BanterPicker` (context priority, chance, never the same line twice in a row), `BubbleGeometry` (bubble + tail layout, clamping). |
| `ClockSync.cs` | NTP-style offset/RTT estimator for future PvP. |

## Runtime
| File | Role |
|---|---|
| `PawGame.cs` | `[RuntimeInitializeOnLoadMethod]` bootstrap, builds everything, fixed per-frame order, modes (Title/Duel/MatchResult), lifecycle voiding (device only), F1..F6 in the editor. |
| `DuelDirector.cs` | Each frame: authority tick → bot → player cue/presses → authority tick → events → presentation (anims, FX, HUD, audio, haptics, result flow). Partials: `.Shots` (draw / foul / muzzle / impacts / tie), `.Acting` (banter bubble, feints, tell), `.Review` (review stills, `StatusLine`), `.LastLight`, `.Gun`. |
| `SwipeDraw.cs` | Holster swipe input: Input System touch / mouse events mapped to the stopwatch, threshold-crossing interpolation, `TimedPress` queue, review injection (lead-owned, SPEC_V2 §9). |
| `TimedPress.cs` | The `TimedPress` struct SwipeDraw queues (the old `PressInput` reader is gone). |
| `PixelPipeline.cs` | Virtual-res RT + display quad, visible/safe rects, shake/zoom/flash, PNG capture. |
| `PawTime.cs` | Presentation time (slow-mo, hit-stop, ambient freeze + eased ambient hold, review scale) + `Ease`. |
| `Layout.cs` | `GameLayout` (canvas, anchors, HUD positions from config/environment.json), `Px` canvas→world, `Pal` colours. |
| `ArtLib.cs` | Resources loading, placeholders + one warning, `SheetDef`, `SpriteCatalog` (ui.json/fx.json), `CharacterDef`. |
| `SpriteAnimator.cs` | Strip animation with fire events, partial plays (feints); `PawGfx` materials/helpers. |
| `DuelActor.cs` | Character renderer: anims, white flash, shake, decaying `Impulse` (recoil / knock-back), `PoseOffset`, optional anims (`tell`), muzzle/hit points in canvas px. |
| `BitmapText.cs` | `BitmapFont`, single-mesh `BitmapText` (outline/shadow quads, mono digits), `NumFmt`. |
| `PixelParticles.cs` | Pooled pixel FX: puffs, smoke, shells (gravity/bounce), sparks, debris, dust motes. |
| `StreetScene.cs` | Environment layers, heat shimmer, props, clouds, vulture, tumbleweeds, sun pulse. |
| `DuelHud.cs` | Portraits (hurt swap), names, HP bars (cropped fill + ghost drain), round sign, holster, swipe sign + `SwipeCue`. |
| `SwipeCue.cs` | The swipe-up teaching cue (ghost thumb, chevrons, stepped dotted trail; Grip / Wait / Go). |
| `EcgMonitor.cs` | Procedural heart monitor drawn into a small texture during the hold (uploads only when a pixel changes). |
| `DuelCues.cs` | 3/2/1 and DRAW pops, burst, live 6-decimal timer, stamps, messages, hosts the `EcgMonitor`. |
| `SpeechBubble.cs` | Pixel speech bubble: panel_paper 9-slice sized to font_small text + 6 px, 3-step tail, 1 px pop, clamped to the visible rect below the HUD. `Show(text, anchor, tailLeft, seconds)` / `Hide()`. |
| `Screens.cs` | Title, Settings, round `ResultPanel`, `MatchResultScreen`. |
| `PixelUi.cs` | `UiPointer` (canvas px), `UiKit`, `PixelButton`. |
| `PawAudio.cs` | SFX pool, music crossfade (Original/Classic), duck, stings, ambience, heartbeat, scheduled DRAW. |
| `PawHaptics.cs` | iOS plugin / Android VibrationEffect / no-op. |
| `PawSettings.cs` | PlayerPrefs settings + all-time best. |
| `ReviewDirector.cs` | Static review/automation API (Show, StartDuel, SimulateTap, SetAutoTap, SetTimeScale, Status, Seed). |

## Frame order (PawGame.Update, execution order −500)
1. `frameStart = stopwatch` → `PawTime.Begin` → `UiPointer.Update` → `SwipeDraw.BeginFrame`
2. Mode logic; in a duel `DuelDirector.Tick(frameStart)`: authority tick, bot tick (feints), player
   `PollRoundStart` / `PollCue` (DRAW shown this frame), presses → `DuelClient.Press` → report, authority tick,
   events → presentation.
3. Animators, particles, HUD, cues update (default order), `PixelPipeline.LateUpdate` pushes effects.

## Coordinates
Canvas px (top-left, y down; 360×640 per SPEC §9) → world `x − W/2, H/2 − y`, 1 unit = 1 px. All sprites are
created with `Sprite.Create` (PPU 1, pivot in cell px) and positioned on whole pixels. HUD lives in the same
grid; UI is laid out against `PixelPipeline.Safe`.

## Duel choreography (DuelDirector, presentation only)
Pawtruth's feints and tell are planned by the host from the authority's private schedule (`BotActing.Plan`), because
the bot is local; they never touch a reaction, report or rule, and the bot's shot still comes only from the `DrawCue`.

| Beat | What happens | Tunables (`duel_config.json`) |
|---|---|---|
| Hold | ambient eases to a stop, both cats freeze their breathing, push-in, vignette tightens, heartbeat, maybe one sweat drop on the hero; Pawtruth may feint (paw flick + spurs) or show the tell (`tell` anim, or ready + 1 px settle if the art is missing) | `holdAmbientScale`, `holdZoom`, `holdVignette`, `holdSweatChance`, `feintAnimSpeed`, `botFeint*`, `botItch*`, `botTell*` |
| DRAW | 1-frame presentation hit-stop, ambient released, white flash (not with Reduced Motion), live timer | `drawHitStopFrames` |
| Hero shot | actor recoil impulse, muzzle_side flash + sparks + smoke + 1 shell, warm screen pop, screen kick | `heroRecoilPx`, `recoilSeconds`, `shakeShot` |
| Pawtruth shot | muzzle_front + sparks + smoke, warm flash, small shake, darkening vignette pulse | `banditShotVignette`, `vignettePulseSeconds` |
| Hit | white flash + knock-back impulse on the victim, hit_puff/ring/sparks, hit-stop | `knockBanditPx`, `knockHeroPx`, `knockSeconds`, `hitStopMs`, `shakeHit` |
| Kill | `hit` reel under slow-mo, then `down`; dust + body-fall + haptic when the body lands | `killSlowMoScale`, `killSlowMoSeconds` |
| Tie ("TOO CLOSE!") | both impacts wait for the second trigger pull, then land together; earlier muzzle flash re-lit, slow-mo (`tieDesaturate` is not applied: `PixelPipeline` has no desaturation control), `stamp_too_close` if present else the "TOO CLOSE!" message, `sfx_lightning` + both hits; round panel gap line "±x.xx ms  TOO CLOSE" in sigOrange and both damages. Double KO: "STANDOFF" message, match result raised with deadHeat = true | `tieWindowMs`, `tieSlowMoScale`, `tieSlowMoSeconds` |
| Result | panel opens only after impacts, draws, falls, slow-mo and hit-stop are done, plus a settle | `resultSettleSeconds` |
| The opponent talks | before a round, 1.2 s before "3" (short standoffs are stretched), a one-liner next to his head for 1.6 s with a soft `sfx_ui_tap` and his 3-note meow signature (`sfx_meow_<cat>`, volume 0.6, pitch ±4 %); context by priority: match start, either under 30 HP ("last one"), player LIGHTNING, player drew early, he won / lost the last round (none after a tie); ~55 % per round, always at match start and "last one"; hidden at DRAW. Match won by Pawtruth: his match line over the result for 2 s | `banterChance`, `banterLeadSeconds`, `banterHoldSeconds`, `banterLowHp`, `banterMatchEndSeconds`, `characters.json` |

Optional art hook: `Art/Characters/Bandit/bandit_tell.png` (cell 117x120, pivot as bandit.json; frames = width / 117,
8 fps, non-looping, holds its last frame) or an `anims.tell` entry in bandit.json.
