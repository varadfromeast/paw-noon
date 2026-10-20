# Paw Noon: haptics

Every haptic in the game, what it plays on each platform and why, how the DRAW haptic is timed, and how to judge
them on a phone.

## 1. Architecture

| Layer | File | Role |
|---|---|---|
| Design data | `Code/Core/HapticDesign.cs` | `HapticPreset` ids, `HapticPresets` (every moment as events: time, duration, intensity start/end, sharpness start/end), `HapticPolicy` (gating, heartbeat scale, DRAW lead, 0.05 quantization), `HapticRaster` (Android waveform). Pure C#, unit-tested. |
| Stamp language | `Code/Core/HapticPattern.cs` | Unchanged §8.6 table `{pulse, gapMs, ...}` + the frame `HapticSequencer`. |
| Moments | `Code/Runtime/HapticMoments.cs` | One method per game moment. The director, the intro, the buttons and the lab all call these. |
| Unity API | `Code/Runtime/PawHaptics.cs` | `Impact / Notify / Pattern / Prepare / Tick` (source-compatible) + `Transient(i, s)`, `Continuous(d, i, s)`, `ScheduleDrawCue(lead)`, `Preset(id, scale, delay)`, `Heartbeat(frenzy, skipped)`, `StopAll`, `Backend`. |
| iOS | `Plugins/iOS/PawHaptics.mm` | Core Haptics: one `CHHapticEngine` (`playsHapticsOnly`, auto-shutdown when idle, held up for 12 s after every `Prepare`), `resetHandler` (drops every player, restarts, re-warms) and `stoppedHandler`. One precompiled `CHHapticPatternPlayer` per preset and 0.05 scale level, started with `startAtTime:` on the engine clock. UIKit generators as the fallback. |
| Android | `Plugins/Android/PawHapticsBridge.java` | `VibratorManager.getDefaultVibrator()` (API 31+) / `VIBRATOR_SERVICE`. Effects cached per preset level: `Composition` primitives (API 30+, when supported) for tap-only presets, `createWaveform` with amplitudes (`hasAmplitudeControl`) or on/off, `createPredefined` (API 29+) for plain taps without amplitude control, `createOneShot` otherwise. All `vibrate()` calls run on a `HandlerThread` with `sendMessageAtTime` (ms scheduling, nothing blocks the Unity thread). `VibrationAttributes.USAGE_MEDIA` on API 33+ (Android's usage for game haptics). |
| Android build | — | The bridge is found by name over JNI. Minify is off today (`AndroidMinifyRelease: 0`). If R8 is turned on, add `-keep class com.pawnoon.haptics.** { *; }` to a custom proguard file. |
| Xcode | `Code/Editor/PawHapticsXcode.cs` | Links `CoreHaptics.framework` into UnityFramework (the `.mm` importer also lists it). |
| Debug | `Code/Runtime/HapticsLab.cs` | The haptics lab (section 5). |

**Gating.** Every call is dropped when the Settings Haptics toggle is off (turning it off also stops anything
playing), and on Android when the system *Touch feedback* setting (`Settings.System.HAPTIC_FEEDBACK_ENABLED`) is off.
That setting is re-read on every `Prepare()`, off the Unity thread.
**Reduced Motion** keeps the first pulse of a rhythm, as before: stamp patterns, the heart's lub-dub, the misfire, the
tie, the win taps and the Android notify look-alikes. Single gestures (DRAW, shot, hit, fall) stay whole, because
they are one event and not motion.

**No per-beat allocation.** A preset is compiled once per 0.05 scale level and replayed. `Prepare()` at duel start
warms every preset at full scale, the 11 heartbeat levels (0.30–0.80) and the LIGHTNING / PERFECT stamps. On iOS the
hot calls are blittable P/Invokes. On Android they are raw `AndroidJNI.CallStatic*Method` calls with reused `jvalue[]`
arrays and pooled `Message`s.

## 2. Inventory

Values are intensity / sharpness (0..1) and ms. "T" is a transient. "C" is a continuous event (iOS: a parameter
curve on intensity and sharpness; Android: 10 ms amplitude steps).

| Moment | Where | iOS (Core Haptics) | Android | Why |
|---|---|---|---|---|
| Countdown 3 / 2 / 1 | `DuelDirector.TickLive` | T 0.40/0.55, 0.55/0.60, 0.70/0.65 | Composition CLICK/TICK at that scale, or a 12–13 ms pulse | Grows with the tick volume (0.55 / 0.68 / 0.82). Crisp, never heavy, so DRAW stays the biggest hit. |
| Hold heartbeat | `HoldHeartBeat` (ECG R peak) | lub T 1.0/0.12 at 0, dub T 0.70/0.08 at 225, scaled 0.30→0.80 by frenzy; nothing below frenzy 0.35; a skipped beat +0.10 (cap 0.80) | the same at amplitude, or two full pulses on on/off motors | Matches `sfx_heartbeat` (lub 10 ms, dub 245 ms at pitch 1, about 225 at the frenzied pitch). Low sharpness reads as a thump, not a click. It stays subtle so the DRAW crack stands apart. |
| LAST LIGHT spike | `LastLightStandoffBeat` | T 1.0/0.45 + C 280 ms 0.75→0, sharp 0.20→0.05 | waveform: 15 ms full, then decaying steps | A defibrillator thoom on the spike after the flat line. |
| **DRAW cue** | `PresentDraw` | T **1.0/1.0** + C 110 ms 0.70→0, sharp 0.50→0.15, **started at photon time** on the engine clock | waveform: 8 ms full, 4 ms gap, decaying 110 ms; scheduled on the haptic thread | The strongest and sharpest event in the game (a test enforces that nothing else copies it). The crack lands with the picture and the sound. |
| Swipe registered | `PresentHeroDraw` (when the draw has a fire frame) | C 55 ms 0.12→0.35 + T 0.45/0.55 at 60 | 13 ms tap at 60 (the swish is too quiet for on/off motors) | The gun clearing leather, under `sfx_holster_draw` (peak ~70 ms). It confirms the input before the fire frame. |
| Hero shot | `HeroFired` (fire frame) | T 1.0/0.80 + C 70 ms 0.75→0 | waveform | The recoil, under the gun sound. |
| Hit dealt | `ApplyImpact` (victim = bandit) | T 0.60/0.95 at 45 | 9 ms tap, delayed 45 ms on the haptic thread | A dry "tock" just after the recoil peak, so the hit is felt as its own event. |
| Kill dealt | same, `killed` | T 0.75/0.90 at 45 + C 350 ms 0.50→0 | waveform | The rumble rides the kill slow-mo. |
| Hit taken | `ApplyImpact` (victim = hero) | T 1.0/0.25 + C 200 ms 0.90→0 | waveform | A low body blow, the opposite of your own crisp shot. |
| Killed | same, `killed` | T 1.0/0.20 + C 550 ms 1.0→0 | waveform | A long dull fade. |
| Body fall | `TickFalls` | T 0.75/0.20 + C 120 ms 0.50→0 | waveform | Dust and thud. |
| Too early (foul) | `PresentHeroFoul` | T 0.85/1.0 + T 0.35/0.70 at 70 | Composition or two pulses | Jam click + hammer, the same as `sfx_misfire` / `sfx_cock`. |
| Tie (TOO CLOSE) | `TieDrama` | T 1.0/0.95, T 1.0/0.95 at 60, C 250 ms 0.40→0 sharp 0.60→0.30 | waveform | Two guns, then a ring under the slow-mo. |
| Intro name plate | `MatchIntro.Plate` | T 0.55/0.75 (YOU ×0.9) | tap | A stamp, firm and short. |
| Stamps | `StampLanded` → `HapticMoments.Stamp` | LIGHTNING / PERFECT: one native pattern (heavy ×3 at 65 ms; medium + heavy at 85 ms) on the haptic clock. GREAT medium, GOOD/SLOW light (transients). TOO EARLY / NO SHOT: `UINotificationFeedbackGenerator` error / warning | LIGHTNING / PERFECT as one Composition or waveform. Notify look-alikes (success 2 taps rising, warning 2 falling, error 3) | The §8.6 table is unchanged. The multi-pulse stamps now keep exact rhythm (the haptic clock, not frame ticks). Reduced Motion: first pulse. |
| Match won / lost | `TickMatchEnd`, 0.10 s later (when the banner lands) | won: T 0.50, 0.75, 1.0 at 0/90/180 + C 300 ms swell; lost: T 0.60/0.15 + C 450 ms 0.55→0 | waveform | Rising vs sinking. A double KO counts as lost. |
| UI button press | `PixelButton.Tick` | T 0.40/0.80 | tap | Light and crisp. |
| Settings / difficulty toggles | `Screens.cs` (`PawHaptics.Impact(0)`, unchanged) | T 0.45/0.70 | tap | A touch fuller than a button press. |
| Bandit shot | `BanditFired` | none (by design) | none | The hit taken follows immediately. Two hits would blur. |

**Before this pass**, every moment was a `UIImpactFeedbackGenerator` light / medium / heavy (or a notification), and
Android was `createOneShot` 12 / 22 / 40 ms. DRAW fired at frame start, about one frame **before** the photons, and
hits were success / warning notifications.

**Fallback (iOS without Core Haptics or with the engine down):** the transients of a preset play on the UIKit
generators at their times (`impactOccurredWithIntensity`, light / medium / heavy by intensity). Continuous tails are
dropped. Delays use `dispatch_after` on the main queue, which is serviced between frames, so the fallback is
frame-quantized (8–17 ms).

## 3. Latency and the DRAW cue

The DRAW sound is scheduled at `dspTime + lead` with `lead = (cuePresentedNs − now) / 1e9`, which is about one display
frame (Docs/TIMING.md §3). `PresentDraw` hands the **same** `lead` to `PawHaptics.ScheduleDrawCue`:

```
delay = clamp(lead − DrawCueOutputLatencySeconds, 0, 0.25 s)        (HapticPolicy.DrawCueDelaySeconds)
iOS:     player.startAtTime(engine.currentTime + delay)             (Core Haptics clock)
Android: handler.sendMessageAtTime(uptimeMillis + round(delay ms))  (haptic thread)
```

| Backend | Assumed output latency (`DrawCueOutputLatencySeconds`) | Notes |
|---|---|---|
| iOS Core Haptics | **0 ms** | `startAtTime` schedules on the haptic server's own timeline. A transient's rise is about 2–4 ms. To be measured. |
| iOS UIKit fallback | 0 ms (plus frame quantization) | Generator latency is undocumented. The fallback only runs when the engine is down. |
| Android | **10 ms** | Vibrator HAL + LRA spin-up. ERM motors are slower (30 ms+). This varies by device. To be measured. |

The DRAW haptic adds **no work to the input path**. Input is timestamped in the Input System callback (SwipeDraw).
The haptic is one native call in `PresentDraw`, in place of the old `Impact(2)`, and it runs after the DRAW sound is
scheduled. Reaction measurement, `ShotReport`s and the DRAW presentation timing are untouched. `ahead` is computed
exactly as before.

**Fairness note.** A felt DRAW is a real cue, and touch reaction is faster than sight. With haptics off, a player
reacts to picture + sound only. Android lands later than iOS by its HAL latency. This was already true with the
old heavy tap at frame start (which was early). It is now consistent and documented. If PvP needs strict parity,
the option is a server rule ("DRAW haptic always on" or "always off").

## 4. Measuring on a device

1. **Slow-motion video (240 fps, 4.2 ms per frame).** Lay the test phone flat with a 1–2 g object (a paper clip or a
   rice grain) on the back near the Taptic Engine / motor, and film screen + object from a second phone. Run
   `ReviewDirector.Show haptics` and tap **DRAW CUE**. It schedules the cue one frame ahead, exactly like the duel.
   Better still, play a real duel: count frames between the DRAW picture appearing and the object's first motion.
2. A consistent offset becomes `PawHaptics.DrawCueOutputLatencySeconds` for that platform (positive = the haptic
   was late). It is a static field so it can be tuned live, and it can move to `duel_config.json` next to
   `presentLatencyExtraMs` once measured.
3. **Engine health:** `Status` reports `haptics=on|off`. The lab header shows `IOS COREHAPTICS RUNNING RESETS=n` (or
   `STARTING`, `STOPPED, UIKIT FALLBACK`, `UIKIT (NO CORE HAPTICS)`), or `ANDROID API n AMP PRIMITIVES` / `ON/OFF`,
   and `TOUCH FEEDBACK OFF` when the system disables haptics.

## 5. The haptics lab

`ReviewDirector.Show haptics` (bridge or editor), or on a device **hold a finger on the top-left corner of the
title screen for 1.5 s**. One button per haptic (28): 3 ticks, 4 heartbeats (frenzy 0.40, 0.70, max, skipped), DRAW
CUE (one frame ahead), swipe, shot, shot+hit, shot+kill, hit taken, killed, body fall, too early, too close, LAST
LIGHT, name plate, UI tap, the six stamps, win, lose. A haptic plays on **finger-down**, so nothing else is felt
first: lab buttons have no tap haptic of their own. The line under the header names what just played. **PLAY ALL**
walks through every haptic 1.1 s apart (tap to stop). **CLOSE** returns to the title. The lab respects the Haptics
toggle and Reduced Motion, which are shown in the header, so gating can be checked too.

Bridge commands (`Lead/cmd/*.txt`, one per line):

```
call PawNoon.Runtime.ReviewDirector.Show haptics
call PawNoon.Runtime.ReviewDirector.Haptic list
call PawNoon.Runtime.ReviewDirector.Haptic drawcue
call PawNoon.Runtime.ReviewDirector.Haptic heart70
call PawNoon.Runtime.ReviewDirector.Haptic lightning
call PawNoon.Runtime.ReviewDirector.Status
call PawNoonLead.LeadTests.Run
```

The editor logs each haptic (`[Haptics] draw cue x1.00 in 8.3 ms`, `[Haptics] heartbeat x0.55`, ...). Turn this off
with `PawHaptics.EditorLog = false`.

**What only a device can confirm:** the feel of every preset; the Core Haptics output latency (and whether the
engine clock's `startAtTime` lands as precisely as expected); behaviour across an engine reset (start a phone call or
Siri mid-duel); that the Touch feedback switch silences Android; Composition primitive support on the target
Android phones (Pixel 6+ and Galaxy S2x+ have it, most mid-range phones fall back to waveforms); and iOS **System
Haptics** off (Core Haptics keeps playing then; the in-game toggle is the switch).

## 6. Tests

- `Tests/EditMode/HapticTests.cs`: stamp table and sequencer (unchanged); every preset passes the authoring
  rules; DRAW is the unique strongest and sharpest event and starts at t = 0; pattern → preset conversion keeps the
  sequencer's exact pulse times; heartbeat scale (gate, 0.30→0.80, monotonic, skip, cap, NaN); gating truth table;
  DRAW lead maths (same lead as the audio, latency taken off, clamps, NaN, the 0.25 s cap, ms rounding);
  quantization round-trip; Android raster (leading silence becomes a delay, 65 ms LIGHTNING spacing, the DRAW
  crack / notch / decaying rumble, scale, on/off motors keep taps); Composition transients.
- `Tests/EditModeRuntime/PawHapticsGatingTests.cs`: toggle off silences and cancels patterns, Reduced Motion plays
  one pulse, patterns advance on the frame tick, the DRAW delay takes the backend latency off, and nothing throws
  on bad input.
