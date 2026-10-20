# Online client (M4 + M5): recorded ghost PvP, friend duels, ranked, leaderboards

This is the Unity side of SPEC_V2 §4.0–4.6. The server side (M3) is `supabase/` (HTTP contract in
`supabase/README.md`). Solo play never depends on anything here. If the server is unreachable or not set
up, PVP explains that in pixel text, and PLAY works as always.

## Flow

```
Title: [trophy] PLAY / PVP + FRIEND (same wood sign) / EASY|HARD / BEST [gear]
  tap PVP
    -> PVP card: CASUAL | RANKED [FIND RIVAL]       (M5; RANKED greyed + "SIGN IN TO RANK" until linked)
    -> card FINDING A RIVAL ...  [CANCEL]          (>= findMinSeconds, network in parallel)
       POST /match-start {mode, clientBuild}       (guest JWT, anonymous sign-in on first use)
    -> card YOUR RIVAL: name, CAT + RATING, RANKED + YOUR RATING | CASUAL  [DUEL]   (auto-starts after 4.5 s)
    -> PawGame.StartPvpDuel(seed, ghost, rules, name)
         config forced to Hard (the EASY/HARD toggle is hidden), DuelDirector slot 1 = GhostOpponent,
         authority seed = the server's seed, RecordedMatchRules (3 resolved rounds, 9 attempts, KO,
         ghost exhausted; HP decides)
       the match plays exactly like solo: same choreography, same timing, same LocalAuthority
       RecordingBuilder listens on the TapTransport: one entry per countdown attempt, built from
       the player's real ShotReports + countdownStartNs
    -> match result screen + strip "VS <NAME>" / "SENDING..."
       POST /match-submit {matchId, recording}
         200      -> "SERVER: YOU WIN" (the server's own recompute; the local result is only logged if different)
                     ranked: third line "RATING 1214 (+12)"; host of a room: "GHOST SENT TO ROOM HOWL"
         offline  -> "SAVED, WILL SEND" (queued on disk, sent on the next launch / next successful call)
         422      -> "NOT COUNTED: TOO LATE" / "NOT COUNTED: REJECTED"
       NEW RIVAL (the REMATCH button) -> FINDING A RIVAL again;  MENU -> title, solo rules restored
```

Every error becomes a short card (NO SIGNAL, NO RIVALS IN TOWN, COULD NOT SIGN IN, TELEGRAPH IS DOWN,
GARBLED WIRE, SIGN IN TO RANK, RANKED IS LOCKED, EASY PARTNER, ONLINE IS NOT SET UP). Raw exceptions and error codes
only reach the log.

### Friend duels (M5)

```
  tap FRIEND (net_config "friendDuel": true, on in the shipped file)
    -> FRIEND DUEL card: [CREATE ROOM] [JOIN ROOM]
  host:   CREATE ROOM -> POST /room-create -> YOUR ROOM: the code in four big tiles (font_code), CLOSES IN 9:58
            [SHARE]  TextShare.Handler (native share sheet, set by a plugin) or the clipboard -> "COPIED! ..."
            [DUEL]   an ordinary casual quick duel: its recording is reserved for the friend (server side)
  friend: JOIN ROOM -> 4 letters on the A-Z pixel keypad (no OS keyboard; the Mac keyboard also types in the Editor)
            [JOIN] -> POST /room-join {code}
               202 waiting -> WAITING card: the code, "WAITING FOR <HOST>...", CLOSES IN m:ss [CANCEL];
                              polls every pollAfterSec (3 s) until expiresAt (10 min after room-create)
               200 ready   -> YOUR FRIEND card (same rival card) -> the match against the host's ghost, then
                              match-submit as usual (casual, the host's seed)
               4xx         -> back to the keypad with one red line (NO ROOM WITH THAT CODE, ROOM CLOSED, ...)
```

### Leaderboards (M5)

The trophy (top-left, mirrors the gear) opens a paper panel with two tabs: WEEKLY RANKED (`ranked_YYYY_WW`, rating,
at least 5 rated duels this week) and FASTEST DRAW (`best_reaction_hard`, all time). Top 50 in 12 px rows, drag to scroll
by whole rows (no clipped rows, so no masks), a 2 px scrollbar, your row highlighted in the list and always pinned at the
bottom ("87. YOU 1214", or "YOU 2/5 DUELS" before you qualify). Both boards load once per opening; every string is built
when a board arrives, so scrolling allocates nothing. Maths: `Runtime/LeaderboardMath.cs` (tested).

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
| `Code/Runtime/Net/OnlineScreens.cs` | `OnlineCard` (finding / rival / message), `OnlineResultCard` (VS + verdict + M5 rating / room line) |
| `Code/Core/RoomCode.cs` (M5) | `RoomCode` (validation), `RoomCodeEntry` (keypad buffer, allocation-free), `KeypadLayout` (7 x 4 grid maths) |
| `Code/Core/NetMessagesM5.cs` (M5) | `RoomCreated`, `RoomJoinAnswer`, `LeaderboardBoard`, `JwtClaims` (is_anonymous) |
| `Code/Runtime/Net/SupabaseApiM5.cs` (M5) | `MatchStart(mode)`, `RoomCreate`, `RoomJoin`, `LeaderboardRead` on top of `SupabaseApi.Call` |
| `Code/Runtime/Net/PvpCard.cs` (M5) | CASUAL / RANKED toggle + FIND RIVAL |
| `Code/Runtime/Net/FriendCard.cs`, `FriendDuel.cs` (M5) | room cards (menu / created / join / waiting) and their flow, polling, mock |
| `Code/Runtime/Net/M5Widgets.cs` (M5) | `M5Card` frame, `CodeTiles`, `PixelKeypad`, `M5Fonts.Code` |
| `Code/Runtime/Net/LeaderboardScreen.cs`, `Runtime/LeaderboardMath.cs` (M5) | the board |
| `Code/Runtime/Net/TextShare.cs` (M5) | share seam: `Handler` (native plugin) or `GUIUtility.systemCopyBuffer` |
| `Code/Runtime/Net/NetMockM5.cs` (M5) | mock rooms and boards |
| art (M5) | `Art/Fonts/font_code` (Jersey 15, OFL, `Tools/ArtGen/ui/gen_font_code.py`), `Art/UI/ico_trophy`, `ico_share` (`gen_icons_m5.py`); previews `Docs/M5/m5_screens.png` (`m5_mockups.py`) |
| `Resources/Data/net_config.json` | committed placeholder (see below) |
| hooks | `DuelDirector.SetOpponent`, `PawGame.StartPvpDuel`, `TitleScreen` PVP/FRIEND (+ M5 trophy, `UiAction.Leaderboard`, one line in `PawGame.Update`), `MatchResultScreen.SetRematchLabel/ContentBottom/ButtonsTop`, `DuelHud.SetName`, `SupabaseAuth.IsLinked` (M5 seam for M6) |
| tests | `Tests/EditMode/GhostOpponentTests.cs`, `RecordingBuilderTests.cs`, M5: `RoomCodeTests.cs`, `NetMessagesM5Tests.cs`, `Tests/EditModeRuntime/LeaderboardMathTests.cs` |

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
  "friendDuel": true,
  "mock": false
}
```

- The URL and anon key are under Dashboard > Project Settings > API. The anon key is public by design.
  **Never** put the service-role key here.
- `clientBuild`: empty means `Application.version`. Ranked only accepts builds listed in `PN_RANKED_BUILDS`.
- `mode`: the PVP card's default pick (`casual` or `ranked`) until the player chooses; the pick is then remembered in
  PlayerPrefs `pawnoon.pvpMode`. Ranked needs a linked Apple/Google account: the toggle reads `SupabaseAuth.IsLinked`
  (the access token's `is_anonymous` claim; M6 does the linking) and the server answers `403 link_required` for a guest
  unless `PN_RANKED_ALLOW_GUESTS=true` on a local stack.
- `friendDuel`: shows FRIEND next to PVP (friend duels by room code, M5). On in the shipped file.
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

- M5: with mock on, rooms and leaderboards are fake too (`NetMockM5.cs`): CREATE ROOM gives HOWL, DUST, SPUR...;
  JOIN answers waiting twice (6 s), then the ghost of "Pardner Pete"; codes `XXXX` / `OLDY` / `MINE` show the not found
  / closed / own-room errors. The boards are 50 rows each (ranked: you are 87th, pinned; fastest: you are 12th,
  highlighted). Ranked mock matches answer "RATING 1214 (+12)" (-11 on a loss).
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
call PawNoon.Runtime.ReviewDirector.StartPvp              # FIND RIVAL with the PVP card's pick, skipping the card (server unless mock is on)
call PawNoon.Runtime.ReviewDirector.StartPvp ranked       # ... in ranked (needs a linked account on a real server)
call PawNoon.Runtime.ReviewDirector.SetNetMock off
call PawNoon.Runtime.ReviewDirector.NetSignOut            # next online call signs in as a new guest
```

M5 (every `Show` below runs on the mock, no server needed):

```
call PawNoon.Runtime.ReviewDirector.Show pvp              # PVP card (RANKED greyed unless linked)
call PawNoon.Runtime.ReviewDirector.SetLinked on          # treat the account as linked (off = guest, auto = the token)
call PawNoon.Runtime.ReviewDirector.Show pvp              # PVP card with RANKED available
call PawNoon.Runtime.ReviewDirector.Show friend           # FRIEND DUEL card (CREATE ROOM / JOIN ROOM)
call PawNoon.Runtime.ReviewDirector.Show room             # YOUR ROOM: code HOWL, SHARE, DUEL, CLOSES IN 10:00
call PawNoon.Runtime.ReviewDirector.Show join             # keypad, "HO" typed (type two more letters, then JOIN)
call PawNoon.Runtime.ReviewDirector.Show waiting          # WAITING for Pardner Pete; turns into the rival card after three 3 s polls
call PawNoon.Runtime.ReviewDirector.Show leaderboard      # WEEKLY RANKED, you pinned at 87.
call PawNoon.Runtime.ReviewDirector.Show fastest          # FASTEST DRAW, you highlighted at 12.
call PawNoon.Runtime.ReviewDirector.Status                # ... linked=no pvpMode=casual card=pvp(casual) | friend=Waiting(HOWL) host="..." | board=ranked:Ready(50 rows, first 0, you 87.)
```

`Status` reports `online=mock|live|off net=<state> auth=none|guest|stale|apple|google|mock-apple|mock-google queued=<n>`, followed by the M6 account fields (`Docs/ACCOUNTS.md`) and the M5 fields `linked=yes|no pvpMode=casual|ranked`.

### Smoke test against local Supabase (M4 "done when")

1. `supabase start && supabase db reset && supabase functions serve --env-file supabase/functions/.env.local`
   with `PN_ALLOW_INJECTED=true`. Put the local URL and anon key in `net_config.json`, then Play.
2. `SetAutoTap 230`, then `StartPvp`. You face a house ghost. After the match, `Status` shows
   `verdict="SERVER: ..." agree=yes`. Your recording is now in the pool.
3. `NetSignOut`, `SetAutoTap 260`, `StartPvp`. (M5: new recordings are quarantined for 2 of the owner's further
   matches; set `PN_QUARANTINE_MATCHES=0` in `.env.local` for this step.) The new guest is matched by MMR and may get your first
   recording or a house ghost (repeat if needed). The rival card shows "Stranger #xxxx". The ghost replays
   your first run's reactions, and `agree=yes` again.

### Friend duel smoke test (M5, local Supabase)

1. Host: `SetAutoTap 230`, tap FRIEND > CREATE ROOM (note the code), then DUEL. Finish the match: the strip says
   "GHOST SENT TO ROOM <CODE>".
2. `NetSignOut` (the friend is a new guest), FRIEND > JOIN ROOM, type the code, JOIN. If you join before step 1 ends you
   see WAITING; it turns into YOUR FRIEND when the host's match is submitted. The match replays the host's run on the
   host's seed; `Status` shows `room=<CODE>` and, after the match, `agree=yes`.

## Known limits

- The opponent always looks like the bandit art (Blackpaw), and the player's recording says `character:
  "whiskers"`, until character select (M2) lands. The rival card shows the recorded cat's name.
- The local presentation always uses the standard damage curve. In casual a Peacemaker ghost would score
  differently on the server, and the server's verdict is what the card shows. Nobody owns the gun before M7.
- Abandoning a PvP match (there is no in-match menu) leaves it open. The next match-start expires it,
  which counts as a loss in ranked.
- Raw swipe `samples` (ranked audit) are not sent yet: that needs `SwipeDraw` to keep the OS samples around the
  threshold crossing (a lead-owned file). The server checks them whenever present and can require them with
  `PN_REQUIRE_SAMPLES=true`; the recording should then also carry `device.screenHeightPx`.
- SHARE copies to the clipboard until a native share plugin sets `TextShare.Handler` (no external packages in M5).
- The host is not told live when the friend joins; they get the usual ghost notification row when the friend's match
  is submitted (notification inbox UI is later).
