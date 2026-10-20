# M5: cheap plausibility checks for ghost duels

A ghost duel can't be made cheat-proof on the server alone. `match-start` hands the client the **seed**, so the client knows every hold length in advance. It also hands over the **opponent's reactions**, because the client has to animate the ghost. A modified client can therefore submit a perfectly consistent recording that beats the ghost by 5 ms every round. The §4.3 checks (bounds, `input − cue == reaction`, seed-consistent cue timing, deadline) only catch clumsy tampering.

The goal for M5 is to make cheating **expensive and statistically visible**. Every check below is a SQL query or a few lines of TypeScript over data we already store. The order is cost against payoff.

## A. Per-submit checks (in `validateAndScore`, reject or shadow-flag)

1. **Wall-clock budget.** The recording's span (`last input or cue − first countdownStartNs`) must be at most the server-measured `submitted_at − issued_at` plus 2 s. It must also be at least the sum of this seed's `cueOffsetNs` for the attempts played, minus slack. This catches recordings fabricated faster than real time, and it costs one subtraction.
2. **Ranked input source.** Require `inputSource = "touch"` and `frameQuantized = false`. Mouse, keyboard and frame-quantized input are casual only.
3. **Raw samples (ranked).** Require `samples` around the threshold crossing. Recompute the interpolated crossing from them, check that it equals `inputNs` within 1 µs, and check the geometry: Δy reaches the threshold for that device's screen height, and the swipe started in the holster band. Also check that sample spacing matches a real digitizer (≈4.2 / 8.3 / 16.7 ms multiples, not exactly regular to the nanosecond). Fabricating believable samples is real work for a cheater.
4. **Margin against a known opponent.** Count rounds won by less than 20 ms against the ghost's known reaction. Honest players produce this rarely (the gap distribution is wide). A cheater who targets "ghost − ε" produces it almost every round.

## B. Per-account rolling statistics (nightly SQL over `recordings.audit`, flag only)

5. **Too fast:** a median shot reaction under 150 ms over at least 15 shots. Simple visual reaction time with a swipe is about 180–300 ms.
6. **Too steady:** a standard deviation under 8 ms over at least 15 shots. Humans sit around 20–40 ms.
7. **Hugging the anticipation line:** more than 20% of shots between 100 and 130 ms. These are tuned guesses just above the foul limit.
8. **No foreperiod effect** (later, needs at least 40 shots): honest reactions are slightly slower after short holds than after long ones. A client that knows the cue shows no correlation between hold length and reaction.
9. **Margin ratio** (rule 4 aggregated): at least 60% of won rounds won by under 20 ms across at least 5 matches.

## C. Containment (cheap, and it protects honest players)

10. **Quarantine instead of instant pooling.** New accounts (fewer than 5 completed matches) and any recording that trips A4 or B5–B9 are stored but not served to others until they clear.
11. **Shadow flags.** Flagged accounts keep playing casual against house and flagged ghosts only. Don't return the reason, so cheaters can't tune against the thresholds. The `flagged` column already exists; add `shadow boolean`.
12. **Account age and linking for ranked** (already in §4.3) plus App Attest / Play Integrity tokens on `match-start` in M6. That's the only defence against a fully modified client that fakes samples well.

Thresholds belong in `server_config.ts` (not `duel_config.json`, since the client must not see them). Log every trip with the attempt data so the thresholds can be tuned against real distributions after launch.

## Status (M5, implemented)

| Check | Where | Effect |
|---|---|---|
| A1 wall clock | `validate_recording.ts` `checkWallClock` | reject `wall_clock` (counts towards the flag) |
| A2 ranked input source | `validateAndScore` (ranked) | reject `input_source` |
| A3 raw samples | `checkSamples` (ranked, whenever samples are present; mandatory with `PN_REQUIRE_SAMPLES=true`) | reject `samples` |
| A4 margin vs ghost | `plausibility.ts` `matchSignals`: >= 2 won rounds by < 20 ms and >= 66 % of won rounds | hold the recording, best not on the board |
| B5 too fast, B6 too steady, B7 anticipation hug, B9 margin ratio | `rollStats` over the last 60 shots (`account_stats`), per submit, not nightly | shadow flag + hold |
| B8 foreperiod | `rollStats` (>= 40 shots) | logged every 10th match only |
| C10 quarantine | `recordings.quarantine_left` = 2, minus one per further clean match | not served to strangers until 0 |
| C11 shadow | `profiles.shadow`; `pn_pick_opponent` / `pn_leaderboard` | separate pool, off the public boards, never told |
| C12 attest | M6 | - |

Every trip is a `plausibility_log` row with its numbers. Thresholds: `PLAUSIBILITY` in `server_config.ts`.
