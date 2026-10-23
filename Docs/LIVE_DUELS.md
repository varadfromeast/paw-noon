# Live duels (phase 2, SPEC_V2 D1b)

Both phones are online at once, in a friend room or a quick match. The rules are the ghost-duel rules (§4.0): seed-driven
holds, 3 resolved rounds, at most 9 countdowns, KO / HP decides, LAST LIGHT, guns normalised in ranked. The ghost path
(match-start / match-submit / rooms) is untouched: live duels have their own tables, functions and client seam, and a
finished live match is written as two ordinary ghost-duel results, so everything downstream works unchanged.

Status: server side implemented and tested without a live Supabase (node + plain Postgres). The client is specified
here (§9) but not written.

| Piece | File |
|---|---|
| Pure state machine | `supabase/functions/_shared/live.ts` |
| I/O shell (load, transition, compare-and-set, broadcast, round driver) | `_shared/live_service.ts` |
| Realtime adapter (the only Realtime code) | `_shared/live_bus.ts` |
| Postgres store | `_shared/live_store.ts`, `supabase/migrations/0070_live.sql` |
| What a finished match writes | `_shared/live_finish.ts` → `pn_live_finish` |
| Edge Functions | `live-create`, `live-join`, `live-ready`, `live-report`, `live-round` |
| C# mirror (client, engine-free) | `Assets/PawNoon/Code/Core/LiveMatchState.cs` (`LiveRules`, `LiveMatchState`, `LiveReplay`) |
| Parity | `Tools/Parity/live_reference.py` → `Tools/Parity/live_vectors.json`, replayed by `live.test.ts` and `LiveMatchStateTests.cs` |
| Tests | `_shared/live.test.ts` (node), `supabase/tests/pg/live.test.sql` (Postgres, RLS) |

---

## 1. Model

**D2 still holds: the server never measures a reaction.** It owns the seed, decides when DRAW goes out, validates each
report, and compares the reactions the phones carry. A phone with 30 ms of latency and a phone with 300 ms get the same
fair result, because each reaction is `inputNs − cuePresentedNs` on that phone's own clock.

**Fair ordering (Fair-Ordering Service, NetGames '03).** Nothing is decided when a report arrives. An attempt is resolved
only when both slots have reported, or at the report deadline, and then strictly by the reactions in the reports. Arrival
order never matters (tested: `fair ordering` in live.test.ts, `fair_order_slow_phone_faster_reaction` in the vectors).

**Same rules, same code.** Resolution replays every attempt so far through `playRecordedMatch` (the TS port of
`LocalAuthority`). A live match is therefore scored exactly like the two players' recordings against each other, and
each pooled recording replays to the same result as a ghost.

**Better secrecy than ghost duels.** A ghost duel hands out the seed at match-start. A live match keeps the seed and the
cue time on the server until `match_over`. `round_start` carries no hold, `draw` carries no time, and the client view
has neither. A modified client cannot pre-fire on a known cue.

### Authority without a server process

Edge Functions are request/response, so the authority is the state in `live_matches.state` plus whichever invocation
touches it. Every action is:

```
load (state, version) -> pure transition at the server's now -> compare-and-set save -> broadcast what it caused
```

A conflicting save is re-read and re-applied (`LIVE_SAVE_ATTEMPTS = 6`, then `503 busy`). This covers two reports racing,
or a report racing the driver. Messages are broadcast only after their state is stored, so a phone never sees a message
for a state that was lost.

**The round driver** is the "live-round" authority. When the second `ready` starts a round, `live-ready` answers at once
and keeps running in the background (`EdgeRuntime.waitUntil`):

1. Sleep until the secret cue time.
2. Tick: DRAW is broadcast and `cueSentAtMs` is stored.
3. Sleep until the report deadline.
4. Tick: any missing report becomes a no-shot and the attempt resolves.

`cueSentAtMs` is stamped before the save and the broadcast, so it errs early by a few tens of ms. That only makes the
elapsed-time check more lenient and is absorbed by the soft-flag slack. The driver stops as soon as the round is no
longer its own (both reported, a void, a forfeit). One round costs at most about 7 s of countdown and hold plus 3 s of
report window. That is far inside the free plan's 150 s wall clock, and sleeping uses no CPU time.

**If the driver dies, nothing is lost.** Every transition first applies the deadlines that have passed (`advance`), so
any later call moves the match on: a report, or `live-round` from either phone. A late cue only makes the hold longer and
reveals nothing. Phones call `live-round` when a deadline they know has passed without news (§4).

---

## 2. State machine (live.ts, mirrored by LiveMatchState.cs and live_reference.py)

```
lobby --join--> ready --both ready--> countdown --cue due--> draw --both reported / deadline--> resolve
  |               |  ^                                                                          |
  |               |  +---------------------- next attempt (match not over) <--------------------+
  v               v  ready deadline: the missing player forfeits; nobody ready = abandoned
cancelled      finished <-- KO / double KO / 3 rounds / 9 attempts / forfeit / abandoned
```

Server times are integer ms (`nowMs`). Report fields are integer ns on the reporting phone's clock. `LIVE_RULES`:

| Rule | Value | Meaning |
|---|---|---|
| `rounds` / `maxAttempts` | 3 / 9 | as ghost duels (D7) |
| `friendLobbyMs` / `quickLobbyMs` | 600 000 / 30 000 | how long a lobby waits for its guest |
| `readyTimeoutMs` | 20 000 | the reconnection window N (§5) |
| `startLeadMs` | 800 | `round_start` goes out this long before "3" |
| report window | 1 550 + `networkGraceMs` 1 500 | `ceil(noShotTimeout + reportSlack)` + network, after DRAW was sent |
| `earlyGraceMs` | 1 000 | an Early may arrive this long after DRAW was sent (pressed before DRAW reached that phone) |
| `cueEarlyToleranceNs` / `cueLateToleranceNs` | 200 ms / 2 s | phone-side "3" to presented cue, against countdown + hold (+ the cue's own lateness) |

Transitions: `createLive`, `joinLive`, `readyLive(slot, attempt)`, `reportLive(slot, report)`, `forfeitLive(slot)`,
`tickLive`. The slot always comes from the authenticated user, never from the request body.

**Verdicts:** `Accepted`, `Duplicate` (a retried ready or report: idempotent, the client treats it as OK), `Stale`,
`WrongRound`, `Late`, `Implausible`, `Inconsistent`, `NotPlaying`, `Taken`. A refused report changes nothing, and that
slot times out as a no-shot.

**Round numbering.** `roundId = attempt + 1`. `roundNumber` is the number of non-void attempts so far plus 1, so a void
replays the same number (as in `LocalAuthority`).

### Messages (Realtime broadcast on `live:<matchId>`; every payload also carries `sentAtMs`)

| event | payload | when |
|---|---|---|
| `joined` | `attempt, readyInMs` | the guest took the slot |
| `ready` | `slot, attempt` | a player is ready (lets the other show OPPONENT READY) |
| `round_start` | `roundId, attempt, roundNumber, startAtMs, countdownBeats, beatNs, damageMultiplier` | both ready; "3" shows at `startAtMs` (server clock) |
| `draw` | `roundId, cueSeq` | the cue. Present it **on arrival** |
| `round_result` | `roundId, attempt, roundNumber, void, tie, winner, outcomes, reactionsNs, damage, grades, hp, roundWins, matchOver` | the attempt resolved |
| `match_over` | `winner, endReason, forfeitedBy, hp, roundWins, seed, attempts` | finished (the seed is revealed here) |
| `cancelled` | `endReason` | the lobby closed |

---

## 3. Lobby and ready handshake

- **Friend:** `live-create {kind:"friend"}` returns a 4-letter code (same alphabet and block list as ghost rooms; codes are
  unique among waiting live lobbies). The friend calls `live-join {code}`. A friend lobby is casual only.
- **Quick match:** `live-create {kind:"quick", mode}` first tries to claim a waiting quick lobby (`pn_live_quick`,
  row-locked with `skip locked`). The claim requires the same mode and the same pool (shadow or flagged players only
  meet each other, C11). The MMR band is ±150, widening by 20 per second the lobby has waited. If nothing matches, the
  caller hosts a quick lobby and waits up to 30 s. **On expiry the client falls back to a ghost duel** (match-start), so
  quick play always gets a duel.
- After the join, both phones subscribe to the channel and call `live-ready {attempt: 0}`. The second ready starts round
  1. The host subscribes right after `live-create`, then calls `live-round` once: a join that happened before the
  subscription shows up in that snapshot (`phase: ready`) instead of the missed `joined` broadcast. After every `round_result`, the phone presents the result (about 3 s) and sends `live-ready {attempt: n}`.
- Ranked live needs the ghost ranked gate (linked account, unflagged, allowed client build). The gun is the profile's
  equipped gun if owned (server-verified, never from the request).

## 4. Clock sync and timeouts

`ClockSync.cs` is fed for free. Every live-* answer carries `serverNowMs`. The phone stamps `t0` before the request and
`t3` after the answer, and adds the sample `(t0, t1 = t2 = serverNowMs·1e6, t3)`. Lobby, join and ready calls give
3 to 10 samples before round 1. The estimate is used for:

1. **"3" at the same instant on both phones.** The phone shows "3" at `startAtMs` mapped to its own clock. Without an
   estimate yet, it shows "3" when `round_start` arrives; the cue-offset tolerance accepts that.
2. **`rttMs` in every report**, which feeds the soft reaction-vs-RTT flags (§7).
3. **Never the reaction itself.** That stays local (D2).

| Timeout | Who enforces | Effect |
|---|---|---|
| lobby (30 s quick, 10 min friend) | any call, `pn_live_create`, the sweep | `cancelled` (`lobby_expired`) |
| ready (20 s after a join or a result) | the driver is gone by then, so the next call | one ready: the other **forfeits**. None: **abandoned** |
| cue | the driver; else any call after the due time | DRAW sent, late if the driver died |
| reports (DRAW + 3.05 s) | the driver; else any call | missing reports become no-shots |

The phone knows `deadlineInMs` for lobby, ready and draw (never the cue). When a deadline passes without a broadcast,
it calls `live-round {action:"sync"}`. If DRAW has not arrived 8 s after `startAtMs` (countdown 2.1 + hold max 4.0 +
margin), it calls the same.

## 5. Disconnects, forfeits, reconnection

- **Leaving:** `live-round {action:"forfeit"}`. In a lobby, the host closes it. In a match, the other player wins (ranked:
  rated as a loss for the leaver).
- **Dropping mid-round:** the rules apply. No report means a no-shot for that attempt, and the opponent's shot lands.
- **Reconnection within N = 20 s:** re-subscribe to the channel, call `live-round` for the snapshot, then
  `live-ready {attempt}`. If the snapshot shows DRAW already sent for a round the phone has not reported, the phone
  presents the cue now and may still report before the deadline. Its reaction is measured from its own presentation, so
  this is fair (D2). A friend who lost the answer to `live-join` calls it again and gets `rejoin`.
- **Past N:** the ready deadline forfeits the absent player. If both are gone, the match is **abandoned**: no result,
  nothing rated, nothing pooled. `pn_live_sweep` (run from pg_cron each minute) closes rows nobody touches for 10 minutes.
- **Presence** (optional, cosmetic): each phone `track`s once per join or reconnect, so the other can show OPPONENT
  RECONNECTING. The free plan allows 5 presence calls per client per 30 s. Authority decisions never use Presence.

## 6. How a live match is recorded (and feeds the ghost pool)

`pn_live_finish` writes, in ONE transaction guarded by the state version (like `pn_complete_match`):

- **Two `matches` rows**, one per player: `status completed`, `live_match_id`, the other player as `opponent_owner_id`
  and `opponent_snapshot`, `opponent_recording_id` set to the other's pooled recording, and a result view from that
  player's side. Stats, the daily bounty, history and "never meet the same ghost twice" therefore work unchanged.
- **Two pooled `recordings`** for a match played to the end (`rounds`, `ko`, `double_ko`; not forfeits). They use the
  match seed and the attempts each player played, including `inputOffsetNs` for early fouls. Quarantine (C10) and holds
  apply as in match-submit. Replaying the two recordings as ghosts gives the live result (tested).
- **Ranked:** both players rated once, each from the other's pre-match rating (Glicko-lite). The write is optimistic: a
  rating row that changed meanwhile answers `conflict`, nothing at all is written, and the service recomputes.
- Profile stats, the rolling plausibility window, the plausibility log, and `live_rounds` (every resolved attempt with
  the raw reports, inputs, flags and view, for audit).

## 7. Anti-cheat

**Per report** (the §4.3 checks, per round, in `reportLive`):

- the round exists and is current; one report per slot (a retry is `Duplicate`);
- `inputNs − cuePresentedNs = reactionNs` (±1 µs);
- `0 ≤ reaction ≤ noShotTimeout`;
- anticipation under 100 ms is a foul;
- **no shot before DRAW was sent** (`Implausible`);
- the cue offset matches the seed's countdown + hold: "3" → cue within −200 ms / +2 s, shifted by the cue's own
  lateness;
- an Early is before the cue and arrives within 1 s of DRAW;
- shots arrive before the report deadline.

`Implausible` and `Inconsistent` are logged (`plausibility_log`, `live_report`). `Implausible` also counts towards the
account flag (three = flagged, ranked off), as in §4.3: an honest phone cannot produce it. `Inconsistent` does not count,
because a long hitch on an honest phone can push its cue offset out of tolerance.

**Reaction vs time the server saw pass (hard).** `(now − cueSent) + reportSlack ≥ reaction`. A phone cannot claim a
reaction longer than the time since DRAW left the server. This is LocalAuthority's own rule.

**Reaction vs RTT (soft, server-only thresholds in `LIVE_PLAUS`).** For each accepted shot,
`excess = (received − cueSent) − reaction` is the latency the phone implicitly claims (DRAW down, presentation, report
up). Honest phones keep it near their RTT.

- `excess_latency`: `excess > 2·rttMs + 400 ms`. The reaction looks shaved below what the server saw pass.
- `rtt_inflated`: `rttMs > excess + 300 ms`. The phone claims more RTT than it showed, to buy headroom.

Flags never reject and are never shown (C11 style). Two or more flagged rounds in a match **hold** that player's
recording (never served) and drop the match best from FASTEST DRAW. Flags are logged as `live_rtt`.

**Rolling statistics** B5–B7 (too fast, too steady, anticipation hug) run over live shots too. They update
`account_stats` and can set the shadow flag. A4 / B9 (margin against a known ghost) does not apply, because nobody
knows the other's reaction in advance.

**Limits.** A fully modified client that fakes a consistent reaction and RTT is only caught statistically. The answer
is attest (C12, App Attest / Play Integrity on live-create), as for ghost duels.

## 8. Rate limits and free-tier budget

Per user (`RATE`, `pn_rate_take`):

| Kind | Limit |
|---|---|
| live-create | 20 per 10 min |
| live-join | 20 per min, plus 12 wrong codes per 10 min |
| live-ready | 40 per min |
| live-report | 40 per min |
| live-round | 40 per min |

A normal match uses about 2 + 2·attempts readies + 2·attempts reports. Three to five attempts is 14 to 22 calls for two
players, plus the occasional sync.

**Free plan limits.** Realtime limits are from supabase.com/docs/guides/realtime/limits, fetched 23 Oct 2026. Edge
Function limits are from supabase.com/docs/guides/functions/limits.md. Monthly quotas are **from memory, unverified**:
the pricing page could not be fetched from this sandbox, so check before relying on them.

| Limit (Free) | Value | What it means for live duels |
|---|---|---|
| Realtime concurrent connections | **200** | one socket per phone: about 100 simultaneous live matches (ghost duels use no socket) |
| Realtime messages per second | **100** | about 3 server broadcasts per attempt × 2 recipients = 6 per 6–9 s per match, under 1/s per match: fine to ~100 matches |
| Channel joins per second | 100 | 2 per match start |
| Presence messages per second | 20; presence calls 5 per client per 30 s | track once per join/reconnect only |
| Broadcast payload | 256 KB | our largest payload is under 1 KB |
| Edge Function wall clock / CPU | **150 s** / 2 s per request | the driver sleeps about 10 s per round; CPU is milliseconds |
| Edge Functions per project | 100 | +5 functions (16 in total) |
| Edge Function invocations / month | 500 000 (unverified) | about 22 per match: about 22 000 live matches a month |
| Realtime messages / month | 2 000 000 (unverified) | about 40 deliveries per match: about 50 000 matches |
| Database | 500 MB (unverified) | `live_rounds` is about 2 KB per attempt; prune after 30 days (open decision) |
| Project pause after 7 idle days (unverified) | | irrelevant once players exist |

The binding constraints are the **200 concurrent connections** and the **invocation quota**. Both are fine for launch.
Past them, the Pro plan (or Nakama, D1) is the next step.

## 9. Client (C#), to implement later; nothing in Runtime is written yet

The goal is the same as M4: `DuelDirector` should not care where slot 1 comes from. The local `LocalAuthority` keeps
running the choreography, and the server's messages drive it.

### Engine-free seams (new Core files)

```csharp
namespace PawNoon.Core
{
    public enum RealtimeStatus { Closed = 0, Joining, Joined, Errored }

    /// One message from the match channel, stamped on the phone's monotonic clock when the socket read it.
    public struct RealtimeMessage
    {
        public string evt;          // joined | ready | round_start | draw | round_result | match_over | cancelled | presence
        public string payloadJson;  // parse with PnJson
        public long receivedNs;     // IDuelClock time at socket read (DRAW is presented from the frame after this)
    }

    /// Supabase Realtime private channel `live:<matchId>` (Phoenix protocol over WebSocket, implemented in Runtime).
    /// Polled on the main thread at frame start; no callbacks into Core.
    public interface IRealtimeChannel
    {
        string Topic { get; }
        RealtimeStatus Status { get; }
        /// Opponent seen in Presence (cosmetic; authority decisions never use it).
        bool OpponentPresent { get; }
        void Subscribe(string topic, string accessToken);   // config: { private: true, broadcast: { self: false } }
        void Track(string presenceJson);                     // once per join / reconnect (5 calls / 30 s limit)
        void Unsubscribe();
        bool TryDequeue(out RealtimeMessage message);
    }

    /// The five HTTP calls (Runtime implements it over SupabaseApi.Call, UnityWebRequest).
    public interface ILiveApi
    {
        void Create(string kind, string mode, Action<LiveAnswer> done);   // kind: friend | quick
        void Join(string code, Action<LiveAnswer> done);
        void Ready(string matchId, int attempt, Action<LiveAnswer> done);
        void Report(string matchId, LiveReport report, Action<LiveAnswer> done);
        void Sync(string matchId, Action<LiveAnswer> done);               // live-round {action: "sync"}
        void Forfeit(string matchId, Action<LiveAnswer> done);            // live-round {action: "forfeit"}
    }

    public sealed class LiveAnswer
    {
        public bool ok;               // HTTP 200 and verdict Accepted | Duplicate
        public ApiError error;        // non-200 (NetMessages.cs)
        public string matchId, channel, code;
        public LiveVerdict verdict;
        public LiveView view;
        public long serverNowMs;
        public long t0Ns, t3Ns;       // stamped by the transport: one ClockSync sample per answer
    }

    /// live.ts liveView. Never contains the seed (until result) or the cue time.
    public sealed class LiveView
    {
        public LivePhase phase; public int you, attempt; public bool[] ready = new bool[2];
        public bool opponentJoined; public long deadlineInMs = -1;            // -1: none (countdown keeps it secret)
        public int roundId, roundNumber; public long startAtMs; public bool cueSent, reported;
        public double[] hp = new double[2]; public int[] roundWins = new int[2];
        public List<LiveAttemptView> history = new List<LiveAttemptView>();
        public LiveResult result; public int seed;                            // seed valid only with a result
    }

    /// Parsers for the broadcast payloads (round_start, draw, round_result, match_over) and LiveAnswer, like NetMessagesM5.cs.
    public static class LiveMessages { /* TryParseAnswer, TryParseRoundStart, TryParseRoundResult, TryParseMatchOver */ }
}
```

### `LiveOpponent : IOpponent` (Core, engine-free; drives slot 1 and paces the local authority)

```csharp
public sealed class LiveOpponent : IOpponent
{
    public LiveOpponent(DuelConfig playerCfg, IDuelTransport transport, IRealtimeChannel channel,
                        ILiveApi api, ClockSync sync, IDuelClock clock, string matchId, int youSlot);

    // IOpponent
    public DuelClient Client { get; }            // slot 1 on the shared transport, like GhostOpponent
    public void StartMatch(int seed);            // seed ignored: the server's is secret
    public void SetPlayerRoundWins(int wins);    // no-op
    public void PreAuthorityTick(long frameStartNs); // drain the channel; DRAW -> authority.ForceCueNow() through the host hook;
                                                 // round_result -> press slot 1 with the server-confirmed input before the tick
    public void Tick(long frameStartNs, long presentLatencyNs);
    public bool FoulPlanned { get; }             // always false (nobody stages a remote player's foul)
    public bool HostActing { get; }              // false: no feints, tells or banter for a real player

    // live-specific, read by the live match driver (Runtime, a sibling of OnlineDirector)
    public LivePhase Phase { get; }
    public bool TryTakeRoundStart(out long localStartNs, out double damageMultiplier); // "3" at startAtMs mapped through ClockSync
    public bool CueArrived(out long receivedNs);
    public bool TryTakeRoundResult(out LiveAttemptView server);   // the director reconciles HP from it (server wins)
    public bool TryTakeMatchOver(out LiveResult result);
    public void OnPlayerReport(ShotReport r, long countdownStartNs); // IDuelTap: posts live-report (with rttMs from sync)
    public void Ready(int attempt);              // after the result presentation
    public void Forfeit();
    public long DeadlineInMs { get; }            // for the sync call when a deadline passes without news
}
```

How the local authority is paced (no Core timing changes):

- In live mode the **authority's** config copy has `holdMinSeconds = holdMaxSeconds = 60`, so it never fires a cue by
  itself. `DRAW` arrival calls `ForceCueNow()`, and the cue is presented on the next frame through the normal path.
- The authority's `noShotTimeoutSeconds` is raised by `networkGraceMs` + 1 s, so slot 1 is not timed out before the
  server's `round_result` delivers its input. The **player's `DuelClient` keeps the real config**, so TOO LATE and the
  reaction measurement are unchanged.
- Slot 1 presses through its own `DuelClient`, exactly like `GhostOpponent`, from `round_result`:
  - a shot or anticipation at our presented cue plus the server-confirmed reaction;
  - an early 1 ns before our cue;
  - a no-shot as silence.

  The local authority then reproduces the server's result: same rules, proven by the live vectors. The director still
  copies HP from `round_result`, and logs (never shows) any disagreement, as M4 does.
- The player's report reaches the server through `TapTransport`'s `IDuelTap` (already the M4 recording seam) and
  `OnPlayerReport`. Nothing in `SwipeDraw` or `DuelClient` changes.
- `LiveMatchState` (Core) can be run on the broadcasts to double-check every `round_result`, the live twin of
  `RecordedMatchSim`'s `agree=yes`.

UI: no new art needed at first. The PVP card gets LIVE | GHOST; FRIEND gets LIVE ROOM. Cards: WAITING FOR A RIVAL (30 s,
then "NO RIVAL ONLINE, DUELING A GHOST"), OPPONENT READY, OPPONENT RECONNECTING m:ss, YOU WIN BY FORFEIT.

## 10. Testing without a live Supabase

- `node Tools/Parity/run_node_tests.ts` covers `live.test.ts`:
  - a normal match scored like the recordings;
  - fair ordering, tie window, fouls / anticipation / void replay;
  - both no-shot and single timeouts;
  - duplicate, late, stale and out-of-order messages, and the §4.3 checks;
  - soft flags, ready timeout / abandoned / lobby expiry, forfeits, reconnection, secrecy;
  - the finish plan (ranked, forfeit, holds);
  - the service with in-memory store, bus and clock (driver timing, compare-and-set conflicts, a dead bus, membership);
  - the vectors.
- `bash supabase/tests/pg/run.sh` covers `live.test.sql`:
  - codes, the join states, quick pairing (band widening, pools, modes, never yourself);
  - compare-and-set save, `pn_live_finish` atomicity (a stale rating writes nothing) and its full write set, the sweep;
  - RLS: members only, no secret columns, no writes, no `pn_live_*` for clients, the Realtime topic predicate.
- `python3 Tools/Parity/live_reference.py --check` checks that the vectors match the independent reference. The C#
  `LiveMatchStateTests` replays the same file in Unity.

## 11. Open decisions

1. **Quick-match fallback:** after 30 s with no live rival, a ghost duel, or keep waiting? Implemented server-side as
   lobby expiry. The fallback is client behaviour.
2. **Ranked live:** allowed in quick match with the ghost ranked gate. Should live and ghost ranked share one rating?
   They do now (same `ratings` row, same season).
3. **Forfeit in ranked counts as a loss even before round 1** (after both joined). This prevents dodging. OK?
4. **Room codes:** live lobbies have their own code space next to ghost rooms. One shared FRIEND keypad would need the
   client to try both, or a merged lookup.
5. **N = 20 s** reconnection window and the **3 s** network grace on reports: tune with real devices (Mumbai, D7).
6. **`live_rounds` retention:** keep 30 days for audit, then prune (pg_cron), or keep forever?
7. **pg_cron** for `pn_live_sweep()` every minute: needs enabling on the project (free plan supports it).
