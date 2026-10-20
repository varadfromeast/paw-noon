# Paw Noon — timing model

How a reaction time is measured, what the 6 decimals mean, and where the limits are.

## 1. One clock

All core timestamps are `long` nanoseconds on one monotonic clock: `PawNoon.Core.StopwatchClock`
(`Stopwatch.GetTimestamp()`; on iOS that is `mach_absolute_time`, on Android `clock_gettime(MONOTONIC)`).
Ticks are converted to ns with exact integer math (`TimeConv.TicksToNs`: `whole*1e9 + rem*1e9/freq`), so
nothing drifts and floating-point time never accumulates. Presentation time (`PawTime`: slow-mo, hit-stop,
review time scale) is separate and never feeds into a measurement. `Time.timeScale` is never touched.

## 2. Input timestamps (when you tapped)

- **With the Input System package** (`PAWNOON_INPUT_SYSTEM`, set by the asmdef `versionDefines`):
  - Touch: `EnhancedTouch.Touch.onFingerDown` → `touch.startTime`.
  - Mouse left button / keyboard Space: `InputSystem.onEvent`, read from the event (`ReadValueFromEvent`)
    → `eventPtr.time`.
  - These times are on the Input System clock (`InputState.currentTime`; on iOS/Android it is derived from
    the OS event timestamp: `UITouch.timestamp` / `MotionEvent.getEventTime`). They are mapped onto the
    stopwatch with an offset sampled **at the same moment** inside the callback:
    `offset = stopwatchNow − InputState.currentTime`, `tNs = eventTime·1e9 + offset`
    (clamped so an event is never later than "now"). Sampling in the callback rather than once per frame
    keeps the two clocks' sampling skew to a few µs.
- **Fallback (legacy Input Manager)**: the press is stamped with the frame-start stopwatch time and flagged
  `frameQuantized = true` (the round result panel then says "FRAME-TIMED INPUT"). Error: up to one frame
  (8.3 ms at 120 Hz, 16.7 ms at 60 Hz), always late.
- If the package is installed but delivers nothing (e.g. handling set to "Input Manager (Old)"), the legacy
  path takes over automatically.

## 3. Cue timestamp (when DRAW appeared)

The authority decides the cue at frame start (`LocalAuthority.Tick`) and broadcasts `DrawCue`. The local
client receives it **in the same frame, before rendering**, shows DRAW in that frame and stamps:

- `cueDecidedNs` = frame-start stopwatch time,
- `cuePresentedNs` = `cueDecidedNs + presentLatency`, where
  `presentLatency = presentLatencyFrames / targetFrameRate + presentLatencyExtraMs`
  (defaults 1 frame + 0 ms; both in `Resources/Data/duel_config.json` for per-device calibration).

To keep that estimate tight we run `QualitySettings.vSyncCount = 0`,
`Application.targetFrameRate = display refresh` (120 on ProMotion, never below 60) and
`QualitySettings.maxQueuedFrames = 1`.

The DRAW sound is scheduled on the DSP clock to the same instant:
`AudioSource.PlayScheduled(AudioSettings.dspTime + (cuePresentedNs − now)/1e9)` (sfx_draw has its transient at
sample 0). With a 256-frame DSP buffer at 48 kHz (5.3 ms) picture and sound agree to within roughly one
buffer.

## 4. Reaction, fouls, timeout

`reaction = inputNs − cuePresentedNs`, computed by the client on its own clock and stored exactly (ns).

- Press after "3" appears and before the cue is presented (or with a timestamp earlier than
  `cuePresentedNs`) → `Early` report → **TOO EARLY** foul.
- `reaction < anticipationMs` (100 ms) → **ANTICIPATION**, treated as a foul (human visual reaction can't
  be faster).
- No press within `noShotTimeoutSeconds` (1.5 s) of the cue → **NO SHOT**.
- Both duelists foul → round void and replayed.

The screen shows seconds with 6 decimals (truncated µs). The gap is shown in ms with 2 decimals.

## 5. Authority and PvP readiness

`LocalAuthority` (a server later) owns the RNG, the schedule and the cue decision. It never sends the cue
time in advance: clients only get `RoundStartMsg {roundId, roundNumber, countdownBeats, beatNs}` at the
countdown start and `DrawCue {roundId, cueSeq}` at the moment of the cue. Each client measures locally and
sends `ShotReport {roundId, slot, kind, reactionNs, cuePresentedNs, inputNs, inputSource, frameQuantized}`.
Network latency is therefore excluded from both reactions — the only fair model for a reflex duel.

The authority validates every report: wrong round / stale round / already settled / duplicate; shot before
the cue was sent; negative reaction; reaction above timeout + `reportSlackMs`; inconsistency between
`inputNs − cuePresentedNs` and `reactionNs` (> 1 µs); a report that arrives sooner after the cue than the
reaction it claims (+ slack). Order is decided by `reactionNs`. Shots whose reactions differ by at most `tieWindowMs` (15 ms, inclusive)
are a **tie**: both shots land whatever the HP (`RoundRecord.isTie`, both `Shot` events `simultaneous`, the
`RoundResolved` event has slot −1 and `simultaneous`), nobody gets the round win, and if both duelists reach 0 HP
the match is a **draw** (`MatchState.Over`, `Winner = −1`, `IsDraw`). The tie is decided only from the two
validated reactions on the authority, never client-side. A shot is committed (damage applied, possibly killing the opponent so the slower shot
never fires) once the other side has reported, is settled, or can no longer claim a faster/tied reaction
(`elapsed since cue ≥ reaction + tieWindow + commitGraceMs`). For networked play `commitGraceMs` becomes the
one-way latency bound.

The bot ("Pawtruth") is a `BotClient` that uses exactly the same `DuelClient` / transport / report path. It
reacts to the cue it receives; it can't know the cue time.

`ClockSync` (NTP 4-timestamp offset/RTT, keeps the lowest-RTT half, takes the median) is provided for later
server-side sanity checks of report timing against message arrival.

## 6. Honest precision

| Source | Typical size | Effect |
|---|---|---|
| Touch sampling (iPhone 120–240 Hz digitizer) | 4–8 ms quantization | reaction ±4 ms |
| Touch processing → OS timestamp | ~1–2 ms | constant offset, mostly cancels |
| Display: frame start → photons (estimate) | 1 frame assumed (8.3 ms @120 Hz); real 1–3 frames + panel response | constant bias, calibratable via `presentLatencyExtraMs` |
| Frame-quantized fallback (no Input System) | up to 1 frame, always late | flagged in the result |
| Clock conversion (InputState ↔ Stopwatch) | µs | negligible |
| Audio scheduling | ≤ 1 DSP buffer (5.3 ms) | DRAW sound vs picture |

So the 6 decimals are the exact arithmetic of exact timestamps. Real-world accuracy against the true photon →
finger-contact interval is about **±5–10 ms**, with a device-dependent constant bias (mostly display latency).
That is the same for both duelists on the same device, and is the reason for the calibration values.
