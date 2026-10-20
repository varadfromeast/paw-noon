# Online client (M4): recorded ghost PvP

This is the Unity side of SPEC_V2 §4.0–4.6. The server side (M3) is `supabase/` (HTTP contract in
`supabase/README.md`). Solo play never depends on anything here. If the server is unreachable or not set
up, PVP explains that in pixel text, and PLAY works as always.

## Flow

```
Title: PLAY / PVP (same wood sign) / EASY|HARD / BEST
  tap PVP
    -> card FINDING A RIVAL ...  [CANCEL]          (>= findMinSeconds, network in parallel)
       POST /match-start {mode, clientBuild}       (guest JWT, anonymous sign-in on first use)
    -> card YOUR RIVAL: name, CAT, (HOUSE GHOST) RATING  [DUEL]   (auto-starts after 4.5 s)
    -> PawGame.StartPvpDuel(seed, ghost, rules, name)
         config forced to Hard (the EASY/HARD toggle is hidden), DuelDirector slot 1 = GhostOpponent,
         authority seed = the server's seed, RecordedMatchRules (3 resolved rounds, 9 attempts, KO,
         ghost exhausted; HP decides)
       the match plays exactly like solo: same choreography, same timing, same LocalAuthority
       RecordingBuilder listens on the TapTransport: one entry per countdown attempt, built from
       the player's real ShotReports + countdownStartNs
    -> match result screen + strip "VS <NAME>" / "SENDING..."
       POST /match-submit {matchId, recording}
         200      -> "SERVER: YOU WIN  +12" (the server's own recompute; the local result is only logged if different)
         offline  -> "SAVED, WILL SEND" (queued on disk, sent on the next launch / next successful call)
         422      -> "NOT COUNTED: TOO LATE" / "NOT COUNTED: REJECTED"
       NEW RIVAL (the REMATCH button) -> FINDING A RIVAL again;  MENU -> title, solo rules restored
```

Every error becomes a short card (NO SIGNAL, NO RIVALS IN TOWN, COULD NOT SIGN IN, TELEGRAPH IS DOWN,
GARBLED WIRE, RANKED IS LOCKED, ONLINE IS NOT SET UP). Raw exceptions and error codes only reach the log.

### How the ghost plays (matches `recorded_match.ts`)

- Attempt k of our match (the k-th countdown, void replays included) plays recording attempt k.
- `shot` / `anticipation`: the ghost's DuelClient presses at **our** presented cue + the recorded reaction.
  It never knows the cue in advance. An anticipation is a shot under 100 ms, and the authority fouls it.
- `early`: the press lands at "3" + `inputOffsetNs`, or 1 s when the offset is unknown, which is the
  server harness default. If our cue arrives first, the press is placed 1 ns before our presented cue, so
  it is always TOO EARLY.
- `noshot`: the ghost stays silent and the authority times it out.
- A recorded player gets no host-staged feints, tells or banter (`IOpponent.HostActing = false`).
- PvP never voids a round when the app is backgrounded (`VoidForLifecycle` is a no-op), because the
  server could not see that void. The player records a no-shot instead. The ghost's due press is
  delivered before the authority ticks (`PreAuthorityTick`), so a long frame cannot turn it into a
  no-shot.

This was checked against the server's own TypeScript authority. A TS mirror of the frame-driven client
was run over 3000 random recordings and player plans, with background-length frames included, and the
winner, HP, end reason, attempt count, round numbers and zero rejected reports all agreed with
`playRecordedMatch` every time. The EditMode tests check the same things in C# against
`RecordedMatchSim`, the C# twin of the server's recompute.

## Files

| File | What |
|---|---|
| `Code/Core/IOpponent.cs` | slot-1 seam: `Client`, `StartMatch`, `SetPlayerRoundWins`, `PreAuthorityTick`, `Tick`, `FoulPlanned`, `HostActing`. `BotClient` implements it with thin forwards. |
| `Code/Core/GhostOpponent.cs` | `AttemptKind`, `GhostAttempt`, `GhostOpponent` (replays a recording through its own DuelClient) |
| `Code/Core/RecordedMatch.cs` | `RecordedMatchRules` (end checks, HP winner, wire names), `RecordedMatchSim` (server recompute in C#) |
| `Code/Core/TapTransport.cs` | pass-through `IDuelTransport` with an `IDuelTap` observer (round starts, reports) |
| `Code/Core/RecordingBuilder.cs` | the player's recording from real ShotReports, JSON per `recording.schema.json` |
| `Code/Core/NetMessages.cs` | `MatchStartInfo`, `MatchVerdict`, `ApiError` parsers |
| `Code/Core/JsonOut.cs` | small JSON writer (exact integers) |
| `Code/Runtime/Net/NetConfig.cs` | loads `Resources/Data/net_config.json` |
| `Code/Runtime/Net/SupabaseAuth.cs` | anonymous guest session, PlayerPrefs, refresh; M6 Apple / Google linking (`Docs/ACCOUNTS.md`) |
| `Code/Runtime/Net/SupabaseApi.cs` | `NetHost` (coroutines), `Http.PostJson`, `SupabaseApi` (timeout, 1 retry, re-auth on 401), `SubmitQueue` |
| `Code/Runtime/Net/OnlineDirector.cs` | the flow above, `NetMock` (fake match-start + built-in recording) |
| `Code/Runtime/Net/OnlineScreens.cs` | `OnlineCard` (finding / rival / message), `OnlineResultCard` (VS + verdict) |
| `Resources/Data/net_config.json` | committed placeholder (see below) |
| hooks | `DuelDirector.SetOpponent`, `PawGame.StartPvpDuel`, `TitleScreen` PVP/FRIEND, `MatchResultScreen.SetRematchLabel/ContentBottom/ButtonsTop`, `DuelHud.SetName` |
| tests | `Tests/EditMode/GhostOpponentTests.cs`, `Tests/EditMode/RecordingBuilderTests.cs` |

## Pointing the game at a Supabase project

Edit `Assets/PawNoon/Resources/Data/net_config.json`:

```json
{
  "supabaseUrl": "https://<project-ref>.supabase.co",
  "anonKey": "<anon public key>",
  "clientBuild": "1.0.0+42",
  "mode": "casual",
  "timeoutSeconds": 10,
  "findMinSeconds": 1.2,
  "friendDuel": false,
  "mock": false
}
```

- The URL and anon key are under Dashboard > Project Settings > API. The anon key is public by design.
  **Never** put the service-role key here.
- `clientBuild`: empty means `Application.version`. Ranked only accepts builds listed in `PN_RANKED_BUILDS`.
- `mode`: `casual` (default) or `ranked`. Ranked needs a linked Apple/Google account (M6), or
  `PN_RANKED_ALLOW_GUESTS=true` on a local stack.
- `friendDuel`: shows FRIEND next to PVP. Room codes are M5, so for now it only shows a "coming soon" card.
- While `supabaseUrl` or `anonKey` still contains `YOUR-`, online play is off.

**Local stack** (`supabase start`, see `supabase/README.md`): use the `API URL` and `anon key` from
`supabase status`. In the Editor that is `http://127.0.0.1:54321`. A phone needs the Mac's LAN IP, for
example `http://192.168.1.20:54321`. Plain http also needs Player Settings > Other Settings > **Allow
downloads over HTTP** set to "Allowed in development builds" (or "Always allowed" while testing).
Otherwise UnityWebRequest refuses the request and PVP shows NO SIGNAL. For injected input (auto-tap /
`SimulateSwipe`) the functions need `PN_ALLOW_INJECTED=true` in `supabase/functions/.env.local`.

Stored on the device:
- PlayerPrefs keys `pn_auth_access`, `pn_auth_refresh`, `pn_auth_expires`, `pn_auth_user`.
- `Application.persistentDataPath/pn_submit_queue.json`. This holds undelivered match-submits, at most 20.
  The server accepts a recording until the match deadline (match duration + 60 s). A later one is answered
  `422 late` (void, not counted against the player) and is dropped from the queue.

## Mocking (no server)

- `net_config.json` `"mock": true`, or the bridge `SetNetMock on`: PVP uses a fake match-start with a
  random seed and the built-in recording "Marshal Mittens" (`NetMock.FakeRecording`: 268.4 ms shot, early
  foul 2.6 s after "3", 301.1 ms, 247.9 ms, a no-shot, then spare shots for void replays). The verdict is
  `RecordedMatchSim`, which is exactly what the server would compute. The strip says
  "MOCK SERVER: ...".
- `StartGhostDuel <seed>` skips the cards and plays that seed against the fake recording at once.

## Bridge commands (Lead/cmd)

```
call PawNoon.Runtime.ReviewDirector.StartGhostDuel 7      # ghost match on seed 7, no network
call PawNoon.Runtime.ReviewDirector.SetAutoTap 240        # hands-free: shoot 240 ms after each cue
call PawNoon.Runtime.ReviewDirector.Status                # ... online=mock net=Playing ... ghost=2/9 recorded=2 ... pvp=attempts:2,resolved:2,end:none
call PawNoon.Runtime.ReviewDirector.Status                # after the match: net=Result local=you verdict="MOCK SERVER: YOU WIN" serverEnd=rounds agree=yes
call PawNoon.Runtime.ReviewDirector.LastRecording         # the recording JSON that was (or would be) submitted
call PawNoon.Runtime.ReviewDirector.SetNetMock on         # PVP button -> mock
call PawNoon.Runtime.ReviewDirector.StartPvp              # same as tapping PVP (server unless mock is on)
call PawNoon.Runtime.ReviewDirector.SetNetMock off
call PawNoon.Runtime.ReviewDirector.NetSignOut            # next online call signs in as a new guest
```

`Status` reports `online=mock|live|off net=<state> auth=none|guest|stale|apple|google|mock-apple|mock-google queued=<n>`, followed by the M6 account fields (`Docs/ACCOUNTS.md`).

### Smoke test against local Supabase (M4 "done when")

1. `supabase start && supabase db reset && supabase functions serve --env-file supabase/functions/.env.local`
   with `PN_ALLOW_INJECTED=true`. Put the local URL and anon key in `net_config.json`, then Play.
2. `SetAutoTap 230`, then `StartPvp`. You face a house ghost. After the match, `Status` shows
   `verdict="SERVER: ..." agree=yes`. Your recording is now in the pool.
3. `NetSignOut`, `SetAutoTap 260`, `StartPvp`. The new guest is matched by MMR and may get your first
   recording or a house ghost (repeat if needed). The rival card shows "Stranger #xxxx". The ghost replays
   your first run's reactions, and `agree=yes` again.

## Known limits

- The opponent always looks like the bandit art (Blackpaw), and the player's recording says `character:
  "whiskers"`, until character select (M2) lands. The rival card shows the recorded cat's name.
- The local presentation always uses the standard damage curve. In casual a Peacemaker ghost would score
  differently on the server, and the server's verdict is what the card shows. Nobody owns the gun before M7.
- Abandoning a PvP match (there is no in-match menu) leaves it open. The next match-start expires it,
  which counts as a loss in ranked.
- Raw swipe `samples` (ranked audit) are not sent yet. The field is optional.
