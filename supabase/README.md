# Paw Noon backend (Supabase)

Milestones M3 + M5 of `Docs/SPEC_V2.md`: Postgres schema, Auth, and Edge Functions for **recorded ("ghost") PvP duels**
(D1, D1b), plus account deletion, the daily bounty, and the purchase-validation contract. M5 adds friend duels by
room code, casual / ranked with weekly seasons and leaderboards, the plausibility checks of `Docs/M5_PLAUSIBILITY.md`,
and the one-transaction match completion (`pn_complete_match`, review D1).

- Hosted project: **Supabase, region South Asia (Mumbai), `ap-south-1`** (D7). You choose the region when you
  create the project in the dashboard. It is not a `config.toml` setting and cannot be changed later.
- The duel rules are a TypeScript port of `PawNoon.Core` in `functions/_shared/duel/`. They are checked
  against the C# with `Tools/Parity/vectors.json` (see [Parity](#parity)).
- The server never trusts a client result. `match-submit` recomputes the whole match from both recordings.

```
supabase/
  config.toml                  local stack + function settings (verify_jwt, anonymous sign-in, Apple/Google)
  migrations/0001_init.sql     tables, indexes, RLS, triggers, pn_* service helpers
  migrations/0002_review_hardening.sql  profile input checks, flagged owners leave the pool, submitting index
  migrations/0003_m5.sql       rooms, quarantine / shadow pool, account_stats, plausibility_log, pn_room_*,
                               pn_complete_match (D1), pn_leaderboard
  migrations/0007_m7_payments.sql   purchases.item, profiles.equipped_gun, pn_grant_entitlement (M7)
  migrations/0060_m6_accounts.sql        names only via profile-update, name shape check, after-link hook (linked_at)
  migrations/0061_review2.sql  review pass 2: old rows repaired + checks validated, character ids, rate_events +
                               pn_rate_*, purchases only move with device proof (pn_grant_entitlement p_allow_transfer)
  migrations/0070_live.sql     phase 2 LIVE duels: live_matches, live_rounds, matches.live_match_id, RLS + Realtime
                               channel policies, pn_live_create / quick / join / save / finish (one transaction) / sweep
  scripts/check_migrations.sh  migration order guard + migrations.lock (new migrations must sort after the applied ones)
  tests/pg/run.sh              SQL tests on a REAL Postgres (throwaway initdb cluster, no Docker): live, m5, review2 .test.sql
  seed.sql                     three house ghosts
  scripts/sync_duel_config.sh  copies Assets/.../duel_config.json into functions/_shared/ (--check for CI)
  functions/
    _shared/
      duel/                    THE PORT: rng.ts (System.Random), config.ts, schedule.ts, damage.ts,
                               match_state.ts, authority.ts (LocalAuthority), recorded_match.ts,
                               loadouts.ts, load_config.ts, parity.ts, duel.test.ts
      duel_config.json         byte copy of the game's rules (keep in sync!)
      recording.ts             Recording type + strict parser       schemas/*.json  JSON Schemas
      validate_recording.ts    §4.3 checks + authoritative recompute
      glicko.ts, ratings.ts    Glicko-lite MMR (+ weekly season carry-over, planGame for pn_complete_match)
      season.ts                ISO-week seasons (YYYYWW), board names ranked_YYYY_WW
      match_issue.ts           the one issue path shared by match-start and room-join (match_response.ts: response)
      ranked.ts, rooms.ts      ranked gate (link_required), room-code rules
      plausibility.ts          A4 margin vs ghost, B5-B9 rolling stats, hold / shadow / quarantine decision
      auth.ts, db.ts, http.ts, server_config.ts, match_types.ts, rows.ts, server.test.ts, m5.test.ts
      display_name.ts, profile.ts, account.test.ts   (M6; names share Tools/Parity/display_names.json with C#)
      live.ts                  LIVE duels: the pure state machine (Docs/LIVE_DUELS.md); live_service.ts (load -> transition ->
                               compare-and-set -> broadcast, round driver), live_bus.ts (Realtime REST broadcast adapter),
                               live_store.ts (pn_live_*), live_finish.ts (what a finished match writes), live_http.ts, live.test.ts
      payments/                M7: der.ts + jws.ts (Apple JWS chain, WebCrypto only), apple.ts, google.ts,
                               receipt.ts (Unity IAP receipts), validate.ts, payments.test.ts + fixtures/
    match-start/ match-submit/ room-create/ room-join/ leaderboard-read/ account-delete/ daily-claim/ purchase-validate/ profile-read/ profile-update/ loadout-equip/
    live-create/ live-join/ live-ready/ live-report/ live-round/      (phase 2 live duels)
```

---

## Run locally

Requirements: Docker, [Supabase CLI](https://supabase.com/docs/guides/cli) >= 2.x, Deno 2 (only for tests).

```bash
cd <repo root>
supabase start                       # Postgres, Auth, REST, Edge Runtime, Studio (http://127.0.0.1:54323)
supabase db reset                    # applies migrations/ and seed.sql (house ghosts)
cp supabase/functions/.env.example supabase/functions/.env.local
supabase functions serve --env-file supabase/functions/.env.local
```

`supabase status` prints `API URL` (http://127.0.0.1:54321) and the `anon key`. Get a guest session the same
way the game does (anonymous sign-in):

```bash
API=http://127.0.0.1:54321
ANON=<anon key from supabase status>
JWT=$(curl -s -X POST "$API/auth/v1/signup" -H "apikey: $ANON" -H "Content-Type: application/json" -d '{}' \
      | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
```

Every function call then sends `Authorization: Bearer $JWT` and `apikey: $ANON`.

### Tests

```bash
deno test --allow-read --allow-env supabase/functions/_shared/   # parity + validator + Glicko + M5 (rooms, seasons, plausibility)
node Tools/Parity/run_node_tests.ts                     # same tests without Deno (Node >= 22.18): 87 tests (20 live)
supabase/tests/pg/run.sh                                # SQL: pn_complete_match atomicity, rooms, quarantine, leaderboards, live (RLS)
supabase/scripts/sync_duel_config.sh --check            # rules copy in sync
supabase/scripts/check_migrations.sh                    # migration files in applied order (db push would refuse a gap)
python3 Tools/Parity/reference_vectors.py --check       # independent Python reference agrees with vectors.json
python3 Tools/Parity/live_reference.py --check          # live duels: the independent reference agrees with live_vectors.json
```

`supabase/tests/pg/run.sh` needs only Postgres 15+ binaries (`initdb`, `pg_ctl`, `psql`; on a Mac
`brew install postgresql@16`). It creates a throwaway cluster in a temp dir, loads `tests/pg/auth_stub.sql` (the bits of
the Supabase stack the migrations need: `auth.users`, `auth.uid()`, the three roles), every migration, `seed.sql`, then
runs `tests/pg/*.test.sql`. `PN_PG_URL=<url>` runs against an existing empty database instead; against `supabase start`
use `PN_PG_URL=postgresql://postgres:postgres@127.0.0.1:54322/postgres PN_PG_SKIP_SCHEMA=1` after `supabase db reset`.
The Edge Functions themselves are thin over these SQL functions and the pure TypeScript above; end-to-end, use the
Unity smoke test in `Docs/ONLINE_CLIENT.md` against `supabase start`.

## Deploy to the Mumbai project

1. Dashboard: new project, region **South Asia (Mumbai)**. Note the project ref.
2. Auth > Providers: enable **Anonymous sign-ins** and **Manual linking**, and configure **Apple** (Services ID, team, key)
   and **Google** (web + iOS + Android client IDs). Disable email sign-up (§5: no email/password). Step by step,
   including the Apple Developer and Google Cloud consoles: `Docs/ACCOUNTS.md`.
3. From the repo root:

```bash
supabase login
supabase link --project-ref <ref>
supabase migration list                                   # first time: see what the project already has (below)
supabase db push                                          # migrations
psql "<connection string from Dashboard > Connect>" -f supabase/seed.sql   # house ghosts (idempotent)
supabase secrets set PN_RANKED_BUILDS="1.0.0+42" PN_DAY_TZ=Asia/Kolkata
supabase functions deploy match-start match-submit room-create room-join leaderboard-read account-delete daily-claim purchase-validate profile-read profile-update loadout-equip
supabase functions deploy live-create live-join live-ready live-report live-round        # phase 2 live duels
```

Live duels also need: Realtime enabled with **private channels** (Dashboard > Realtime > Settings: turn off "Allow public
access", so the `realtime.messages` policies of 0070 apply), and optionally pg_cron running `select public.pn_live_sweep();`
every minute (closes lobbies and matches nobody touches; see `Docs/LIVE_DUELS.md` §5).

The house ghosts are required in production: without them the first player gets `503 no_opponent`.

**Migration numbering.** The milestones numbered their migrations 0003 (M5), 0007 (M7), 0060 (M6) and 0061 (review 2)
so they could be written in parallel. `db push` applies whatever the remote history lacks, but refuses a local
migration that sorts **before** the newest applied one. On a fresh project that never happens. If the project already
ran some of them out of order (e.g. 0007 was pushed before 0003 existed), `supabase migration list` shows the gap;
push once with `supabase db push --include-all` (0003, 0007 and 0060 touch disjoint objects, and 0061 must run after
all of them). Never rename an applied migration. New migrations: `supabase migration new <name>` (timestamped, so it
sorts last), then `supabase/scripts/check_migrations.sh --update` and commit `scripts/migrations.lock`.

**Deploy order for review pass 2.** `db push` (0061 replaces `pn_grant_entitlement` with the 12-argument version)
and then deploy the functions right away; between the two, `purchase-validate` answers 500 and the client keeps the
purchase pending and retries (no harm). 0061 also repairs old profile rows and validates the NOT VALID checks; on a
large table run it off-peak (`VALIDATE CONSTRAINT` takes a SHARE UPDATE EXCLUSIVE lock, reads stay open).

### Environment

| Variable | Where | Meaning |
|---|---|---|
| `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `SUPABASE_ANON_KEY` | injected by the Edge Runtime | the functions use the service role after authenticating the caller |
| `PN_SEASON` | secret, normally **unset** | M5: seasons are weekly (ISO week, `ratings.season` = YYYYWW, board `ranked_YYYY_WW`). A positive integer pins one fixed season instead (board `ranked_season_N`) |
| `PN_QUARANTINE_MATCHES` | secret | further clean matches before a new recording is served to strangers (default 2; `0` on a local stack) |
| `PN_REQUIRE_SAMPLES` | secret | `true`: ranked recordings must carry raw swipe samples (A3). Leave unset until the client sends them |
| `PN_RANKED_BUILDS` | secret | comma-separated client builds allowed in ranked, `*` = any, empty = ranked closed |
| `PN_DAY_TZ` | secret | calendar day of the daily bounty, default `Asia/Kolkata` |
| `PN_RANKED_ALLOW_GUESTS` | **local only** | `true` lets guests play ranked (no Apple/Google on `supabase start`) |
| `PN_ALLOW_INJECTED` | **local only** | `true` accepts `inputSource: "injected"` (ReviewDirector smoke test) |
| `SUPABASE_AUTH_EXTERNAL_APPLE_*`, `..._GOOGLE_*` | shell env for `supabase start` | local OAuth (optional) |
| `APPLE_BUNDLE_ID`, `APPLE_ISSUER_ID`, `APPLE_KEY_ID`, `APPLE_PRIVATE_KEY` | secret | App Store Server API (purchase-validate), see `Docs/PAYMENTS.md` |
| `APPLE_SHARED_SECRET` | secret, optional | verifyReceipt password (fallback path) |
| `GOOGLE_PACKAGE_NAME`, `GOOGLE_SERVICE_ACCOUNT_JSON` | secret | Play Developer API (purchase-validate) |
| `PN_IAP_ALLOW_SANDBOX`, `PN_GOOGLE_ACKNOWLEDGE` | secret, optional | default `true` both |

Server tunables that are not secrets (3 rounds, 9 max attempts, +60 s submit slack, pool 30 days / 20 uses,
3 rejections to flag, daily 3 wins / 20 coins, `ROOM`, `LEADERBOARD`, `PLAUSIBILITY` thresholds) are in
`functions/_shared/server_config.ts`. The plausibility thresholds are never sent to clients.

---

## Database

| Table | Rows | Client access (RLS) |
|---|---|---|
| `profiles` | one per auth user (trigger on `auth.users`): name, cat, owned items, coins, stats, daily streak, rejections/flag, `linked_at` (M6) | select own (0061: not `shadow`, `flagged`, `rejections`); update own `character`, `settings`, `equipped_gun` only (`display_name` only through `profile-update` since 0060) |
| `recordings` | the ghost pool (+ house ghosts) | **none**: served by `match-start` |
| `matches` | issued/completed/void matches, opponent snapshot, server result, MMR before/after | select own (0061: not `opponent_owner_id` / `opponent_snapshot` / `opponent_recording_id`) |
| `ratings` | Glicko-lite per (user, season) | select own |
| `purchases` | validated store transactions (unique per store transaction) | select own |
| `notifications` | e.g. "Stranger #1234 beat your 231 ms" | select own; update own `read_at` only |
| `rate_events` (review 2) | one row per rate-limited call (user, kind, time), pruned per user on use | none |
| `rooms` (M5) | friend-duel rooms: code, host, status open / recorded / used / expired / cancelled, reserved recording | **none**: room-create / room-join |
| `account_stats` (M5) | rolling plausibility window per account (last 60 shots + margin counters) | none |
| `plausibility_log` (M5) | every tripped check with its data (reject / hold / shadow / log) | none |
| `live_matches` (0070) | live duels: membership, status, the state machine (`state`, `version`: secret seed and cue time) | members select status/result columns only (never `state`, `seed`, auth ids) |
| `live_rounds` (0070) | one row per resolved live attempt (raw reports, inputs, soft flags, view) | members select the view columns only (never `reports` / `flags`) |

M5 columns: `profiles.shadow` (C11 shadow flag, set by the rolling checks, cleared by ops only), `recordings.quarantine_left`
(C10: further clean matches of the owner before strangers are served it) and `recordings.held` (tripped a check: kept for
audit, never served), `matches.room_id`.

Every write goes through a function with the service role. `pn_pick_opponent`, `pn_register_rejection`,
`pn_record_match_stats`, `pn_claim_daily`, `pn_room_create`, `pn_room_join`, `pn_room_unclaim`, `pn_complete_match` and
`pn_leaderboard`, and since 0061 `pn_rate_take` / `pn_rate_count` / `pn_rate_note` and `pn_repair_profiles`, are
`SECURITY DEFINER` helpers executable by `service_role` only. `pn_grant_entitlement` / `pn_set_equipped_gun` /
`pn_recompute_item` (0007, 0061) likewise.

**Rate limits (0061).** Per user, sliding window (`server_config.ts` `RATE`, `pn_rate_take`): room-join 40/min (a
waiting friend polls 20/min) and 12 wrong codes per 10 min, profile-update 12/h, profile-read 30/min, purchase-validate
30/10 min, daily-claim 12/min, leaderboard-read 30/min, loadout-equip 20/min, account-delete 6/h. Over the limit:
`429 rate_limited` with `retryAfterSec`. match-start (6/min) and room-create (5/10 min) keep their own counters. A
database error in the limiter lets the call through (logged).

**Pool rules (M5).** `pn_pick_opponent` serves a recording only when `quarantine_left = 0` and not `held`. Flagged
(3 rejections) and shadow accounts form a separate pool: they meet house ghosts and each other, nobody else meets them,
and they are left off the public leaderboards (their own `you` row still shows the rank they would have).

**Atomic completion (review D1).** `pn_complete_match(match, result, rating, recording, plaus)` writes the player's and the
ghost owner's ratings (optimistic: each carries the `matches` count it was computed from; a changed row answers
`conflict` and nothing at all is written), the quarantine step, the pooled recording, the room reservation, the match row,
profile stats, the rolling plausibility state + log and the owner's notification, in one transaction guarded by
`status = 'submitting'`. A deleted ghost owner only skips the owner's rating and notification (savepoint). match-submit
only computes, retries a `conflict` up to 4 times, and otherwise answers `503 busy` with the match back in `issued`.

---

## How a recorded match is scored (client M4 must match this)

`functions/_shared/duel/recorded_match.ts`, mirrored in C# by `ParityVectorsTests.PlayMatch`:

1. Both players use `new System.Random(seed)` in `LocalAuthority`, so countdown **attempt k** has the same hold
   for both. Every `StartCountdown` consumes one schedule, void attempts included.
2. `recording.rounds[k]` is the k-th **attempt** in play order. A void attempt (both fouled) is recorded and
   repeats `roundNumber`. Attempt k of the player is played against attempt k of the opponent recording.
3. Each attempt is resolved by the real `LocalAuthority` rules: shot order by reaction, 15 ms inclusive tie
   window (both shots land), anticipation under 100 ms, TOO EARLY foul, no-shot, the faster shot kills and
   stops the slower one, HP carries over, and both fouling voids the attempt.
4. The match ends (checked before each attempt, in this order) on: KO / double KO, **3 resolved rounds**,
   9 attempts, or the **opponent recording running out of attempts** (`opponent_exhausted`, possible only
   when you void an attempt the ghost did not). The player's recording must stop exactly there.
5. Winner: KO winner, else more HP left (= more total damage dealt), equal = draw.
6. Damage: ranked uses the standard curve for both; casual uses each side's gun (`loadouts.ts`, Peacemaker =
   `damageValuePeacemaker` in `duel_config.json`, `[48,42,34,26,16,8,4,2]`). The client does the same
   (`Guns.Effective`, `LocalAuthority.SetGun`); `vectors.json` match cases carry `guns` + `ranked`.

Client requirements this implies: in PvP, backgrounding the app must not call `VoidRound` (record a no-shot
instead), and the ghost's early foul is replayed at `inputOffsetNs` after "3" when present.

---

## HTTP contract

Base URL: `https://<ref>.supabase.co/functions/v1` (local: `http://127.0.0.1:54321/functions/v1`).
All functions: `POST`, JSON body, headers `Authorization: Bearer <user access token>` and `apikey: <anon key>`.
Errors always have the body `{ "error": "<code>", "message": "...", ...extra }` (`schemas/error.schema.json`).
Common errors: `401 unauthorized`, `400 bad_json`, `405 method_not_allowed`, `413 body_too_large`, `429 rate_limited`,
`500 internal|db_error`. Bodies are capped at 1 KB for the functions that take no payload (64 KB was the old default),
256 KB for match-submit and purchase-validate.

### `POST /match-start`

Issues a match against a ghost (closest MMR, never yourself, never one you have faced, ±150 widening;
otherwise a house ghost on a fresh seed). Any match you left open is expired, and in ranked it counts as a loss.

Request (`schemas/match-start.request.schema.json`). M5: `mode` is optional and defaults to `casual`.
```json
{ "mode": "casual", "clientBuild": "1.0.0+42" }
```

```bash
curl -s -X POST "$API/functions/v1/match-start" -H "Authorization: Bearer $JWT" -H "apikey: $ANON" \
     -H "Content-Type: application/json" -d '{"mode":"casual","clientBuild":"1.0.0+42"}'
```

200 (`schemas/match-start.response.schema.json`):
```json
{
  "matchId": "9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10",
  "mode": "casual",
  "season": null,
  "difficulty": "hard",
  "seed": 1,
  "rounds": 3,
  "maxAttempts": 9,
  "gunsNormalised": false,
  "issuedAt": "2026-10-20T10:00:00.000Z",
  "deadlineAt": "2026-10-20T10:04:33.000Z",
  "you": { "mmr": 1200, "rd": 350 },
  "opponent": {
    "recordingId": "00000000-0000-4000-8000-000000000002",
    "displayName": "Marshal Mittens",
    "character": "pawtruth",
    "loadout": { "gun": "standard" },
    "mmr": 1200,
    "isHouse": true,
    "rounds": [
      { "roundNumber": 1, "kind": "shot", "reactionNs": 268400000 },
      { "roundNumber": 2, "kind": "shot", "reactionNs": 301100000 },
      { "roundNumber": 3, "kind": "shot", "reactionNs": 247900000 }
    ]
  }
}
```
Errors: `400 bad_request`; ranked: `403 link_required` (a guest: sign in with Apple / Google; `reasons` lists everything
that blocks, `link_account` first) or `403 ranked_not_allowed` (linked, but `reasons: ["account_flagged" | "client_build"]`);
`409 no_profile`; `429 rate_limited` (`retryAfterSec`, more than `MATCH.maxStartsPerMinute` starts in a minute);
`503 no_opponent`. Each start also voids this player's matches stuck in `submitting` for over
`MATCH.staleSubmitMinutes` (`void_reason: "stale_submit"`, not counted towards the flag).

### `POST /match-submit`

Request (`schemas/match-submit.request.schema.json`, recording = `schemas/recording.schema.json`). Example for the
match above (seed 1: holds 1.746 s, 1.332 s, 2.401 s; cue presented one 120 Hz frame after it was due):
```json
{
  "matchId": "9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10",
  "recording": {
    "version": 1, "seed": 1, "difficulty": "hard", "character": "whiskers", "loadout": { "gun": "standard" },
    "rounds": [
      { "roundNumber": 1, "kind": "shot",  "reactionNs": 231456789, "cuePresentedNs": 8854339085,
        "inputNs": 9085795874, "countdownStartNs": 5000000000, "inputSource": "touch", "frameQuantized": false },
      { "roundNumber": 2, "kind": "early", "reactionNs": 0, "cuePresentedNs": 0,
        "inputNs": 18804339085, "countdownStartNs": 15854339085, "inputSource": "touch", "frameQuantized": false },
      { "roundNumber": 3, "kind": "shot",  "reactionNs": 264000000, "cuePresentedNs": 30804269723,
        "inputNs": 31068269723, "countdownStartNs": 26294904350, "inputSource": "touch", "frameQuantized": false }
    ],
    "device": { "model": "iPhone15,2", "os": "iOS 18.1", "refreshHz": 120 },
    "clientBuild": "1.0.0+42",
    "submittedAt": "2026-10-20T10:04:12Z"
  }
}
```

`character` is `"whiskers"` or `"pawtruth"`; `"blackpaw"` (the persona's id before the rename) is accepted as an alias
and stored as `pawtruth` (0061 renamed old rows; match-start answers always say `pawtruth`).

Recording field rules: `*Ns` are integer ns on the phone's monotonic clock. `shot`/`anticipation`:
`reactionNs = inputNs - cuePresentedNs` (within 1 µs), `0 <= reactionNs <= noShotTimeout`, and `anticipation`
exactly when it is under 100 ms. `early`: `cuePresentedNs` 0 when no cue was shown yet (then `reactionNs` 0),
else the press is before it and `reactionNs` is the negative difference. `noshot`: `inputNs` 0, `reactionNs` 0.
`countdownStartNs` (when "3" appeared, > 0) is required on every attempt: it anchors the hold-length check, so a
recording cannot skip that check by leaving it out (missing = `400 bad_recording`).
`samples` (raw touch samples) is optional and kept for ranked audit.

200 (`schemas/match-submit.response.schema.json`). This is the server's own recompute:
```json
{
  "matchId": "9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10",
  "status": "completed",
  "mode": "casual",
  "winner": "opponent",
  "endReason": "rounds",
  "hp": { "you": 18.044, "opponent": 39.45481467999999 },
  "roundWins": { "you": 1, "opponent": 2 },
  "rounds": [
    { "attempt": 1, "roundNumber": 1, "holdNs": 1746005752, "void": false, "tie": false, "winner": "you",
      "you":      { "outcome": "Shot", "reactionNs": 231456789, "damage": 32.22518532, "grade": "Perfect" },
      "opponent": { "outcome": "Shot", "reactionNs": 268400000, "damage": 27.792, "grade": "Great" },
      "hpAfter": { "you": 72.208, "opponent": 67.77481467999999 } },
    { "attempt": 2, "roundNumber": 2, "holdNs": 1332231932, "void": false, "tie": false, "winner": "opponent",
      "you":      { "outcome": "Foul", "reactionNs": null, "damage": 0, "grade": "None" },
      "opponent": { "outcome": "Shot", "reactionNs": 301100000, "damage": 23.912, "grade": "Great" },
      "hpAfter": { "you": 48.296, "opponent": 67.77481467999999 } },
    { "attempt": 3, "roundNumber": 3, "holdNs": 2401032040, "void": false, "tie": false, "winner": "opponent",
      "you":      { "outcome": "Shot", "reactionNs": 264000000, "damage": 28.32, "grade": "Great" },
      "opponent": { "outcome": "Shot", "reactionNs": 247900000, "damage": 30.252, "grade": "Perfect" },
      "hpAfter": { "you": 18.044, "opponent": 39.45481467999999 } }
  ],
  "rating": null,
  "recordingId": "5d0b1c9e-1f7e-4d53-a1a6-6f3f8b9d2c44"
}
```
In ranked, `rating` is `{ "season": 202643, "before": 1200, "after": 1162, "delta": -38, "rd": 290 }` (`delta` =
`after - before` of the rounded values, so the client can print "RATING 1162 (-38)") and the ghost's
owner is rated too. The owner of a non-house ghost gets a `notifications` row (`kind: "ghost_result"`,
`payload: { matchId, mode, result: "won"|"lost"|"draw", from: {displayName, character}, yourBestNs,
theirBestNs, mmrDelta, message }`).

Errors:
- `400 bad_request | bad_recording` (shape). The match stays open, so the client may fix and resend before the deadline.
- `404 match_not_found`, `409 already_submitted` (`matchStatus`). There is one submission per match id.
- `500 db_error | internal`: M5 (D1): everything is written in one transaction, so a failure means nothing was written;
  the match goes back to `issued` and the client may resend before `deadlineAt`. Failures that only concern the ghost's
  owner (their rating, their notification) are skipped inside the transaction and do not fail the call.
- `503 busy`: the rating rows kept changing under concurrent submits (4 tries); nothing was written, the match is back in
  `issued`, resend.
- `422 recording_rejected` with `reason`, `matchStatus: "void"`, `flagged`. The match is void and the reason is logged.
  Reasons (§4.3): `late` (after `deadlineAt`, does **not** count towards the flag), `loadout_not_owned`, `version`,
  `seed_mismatch`, `difficulty`, `round_count`, `round_numbering`, `reaction_bounds`, `kind_mismatch`,
  `inconsistent_reaction`, `non_monotonic`, `cue_timing`, `input_source`, and M5: `wall_clock` (A1: the recording spans
  more phone time than the server saw pass since match-start, +2 s, or less than its own countdowns and holds need),
  `input_source` in ranked for anything but touch with OS timestamps (A2), `samples` (A3, ranked: raw swipe samples that
  do not reproduce `inputNs`, start outside the holster band, go sideways, or are not spaced like a real digitizer;
  required only with `PN_REQUIRE_SAMPLES=true`, checked whenever present). Three counted rejections flag the account
  (ranked disabled).

M5 response field: `room: { "code": "HOWL", "status": "recorded" }` when this recording was reserved for the friend of
the player's open room. The plausibility checks that do not reject (A4 margin against the ghost, B5-B9 rolling
statistics) never change the response: a tripped match only holds the recording, leaves the best reaction off the
leaderboard and may set the shadow flag (see `Docs/M5_PLAUSIBILITY.md` and `plausibility_log`).

### Live duels: `live-create`, `live-join`, `live-ready`, `live-report`, `live-round` (phase 2)

Both phones online; the server sends DRAW over the Realtime channel `live:<matchId>` and resolves each attempt by the
reactions the phones report. Full protocol, messages, timeouts and the client seam: **`Docs/LIVE_DUELS.md`**. Every answer
is `{ matchId, channel, verdict, ok, view, serverNowMs }` (+ `code`, `kind`, `mode` from live-create); `view` never
contains the seed (until the result) or the cue time.

| Call | Body | Notes |
|---|---|---|
| `live-create` | `{ kind: "friend" \| "quick", mode?, clientBuild }` | friend: a 4-letter code (casual). quick: joins the closest waiting lobby or hosts one for 30 s. Ranked: the ranked gate (403 `link_required` / `ranked_not_allowed`) |
| `live-join` | `{ code }` | 404 `room_not_found`, 410 `room_expired` \| `room_used`, 409 `own_room` \| `room_taken`; again after a lost answer = the same match |
| `live-ready` | `{ matchId, attempt }` | the second ready starts the round (round driver runs in the background) |
| `live-report` | `{ matchId, report: { roundId, kind: "Shot" \| "Early", reactionNs, cuePresentedNs, inputNs, countdownStartNs, rttMs? } }` | 400 `bad_report`; verdict `Implausible` / `Inconsistent` is logged, `Implausible` counts towards the flag |
| `live-round` | `{ matchId, action?: "sync" \| "forfeit" }` | snapshot + applies passed deadlines (reconnection, a dead driver); forfeit |

Errors as everywhere: `404 match_not_found` (also for a match you are not in), `429 rate_limited`, `503 busy`.

### `POST /room-create` (M5)

Body `{}`. Opens a friend-duel room for 10 minutes and reserves the caller's **next** recording (the next match they
complete, issued after this call, any mode) for the one friend who joins. A new room cancels the caller's room that is
still waiting; a room that already holds a recording stays playable until it expires.
```json
{ "code": "HOWL", "status": "open", "expiresAt": "2026-10-20T10:10:00.000Z", "ttlSec": 600 }
```
Codes are 4 letters without I and O and never a word on `ROOM.blocked`. Errors: `409 no_profile`, `429 rate_limited`
(5 rooms per 10 minutes), `503 busy` (no free code).

### `POST /room-join` (M5)

Request `{ "code": "HOWL" }` (case-insensitive). The first caller holds the room's friend slot.
- `202` while the host has not recorded yet; poll again every `pollAfterSec` until `expiresAt`:
  ```json
  { "status": "waiting", "code": "HOWL", "hostName": "Pardner Pete", "expiresAt": "2026-10-20T10:10:00.000Z", "pollAfterSec": 3 }
  ```
- `200` once it is in: a **match-start response** (issued through the same code path: throttle, abandon penalty,
  deadline, snapshot) with `"status": "ready"`, `"mode": "casual"`, the host's seed and recording as the opponent, and
  `"room": { "code": "HOWL" }`. Submit it with `match-submit` like any match. Calling again after a lost answer returns the
  same match while it is still open.

Errors: `400 bad_request` (not 4 letters), `404 room_not_found`, `410 room_expired` (closed before the host recorded),
`410 room_used`, `409 own_room`, `409 room_taken` (another friend holds it), `409 no_profile`, `429 rate_limited`.

### `POST /leaderboard-read` (M5)

Request `{ "board": "ranked" }` (default; this week), `"ranked_2026_42"` (a past week) or `"best_reaction_hard"`.
```json
{ "board": "ranked_2026_43", "kind": "ranked", "unit": "mmr", "season": 202643, "closesAt": "2026-10-26T00:00:00.000Z",
  "minMatches": 5, "total": 412,
  "top": [ { "rank": 1, "displayName": "Old Tom", "character": "pawtruth", "value": 1702, "matches": 30, "you": false } ],
  "you": { "rank": 87, "value": 1214, "matches": 9, "eligible": true } }
```
Top 50; ties share a rank. Ranked lists players with at least `minMatches` rated matches this week (`you.rank` is null
until then). `best_reaction_hard`: `unit: "ns"`, `value` = the fastest server-validated shot (matches that tripped a
plausibility check do not count), all time, `you` null before the first shot. Weekly seasons start on Monday 00:00 UTC
from last week's rating pulled 25 % towards 1200 with RD at least 200.

### `POST /account-delete`

```bash
curl -s -X POST "$API/functions/v1/account-delete" -H "Authorization: Bearer $JWT" -H "apikey: $ANON" \
     -H "Content-Type: application/json" -d '{"confirm":"DELETE"}'
```
200 `{ "deleted": true, "userId": "<uuid>" }`. This hard-deletes the auth user. Cascades remove the profile, the user's
recordings, matches, ratings, purchases and notifications. Errors: `400 confirm_required`, `500 delete_failed`.

### `POST /profile-read` (M6)

Body `{}`. The caller's profile card (`schemas/profile.response.schema.json`):
```json
{ "userId": "5d0b1c9e-...", "displayName": "Stranger #0042", "character": "whiskers", "isGuest": true, "providers": [],
  "linkedAt": null,
  "stats": { "matches": 12, "wins": 7, "winRate": 0.583, "bestReactionNs": 198412000, "lightning": 3 },
  "rating": { "season": 1, "mmr": 1234, "rd": 120, "matches": 4 },
  "coins": 20, "dailyStreak": 2, "ownedItems": [], "nameRules": { "min": 3, "max": 14, "chars": "A-Z 0-9 space" } }
```
`stats` are the server-validated PvP counters that `match-submit` adds to after every completed match (solo duels are
offline and not counted). `rating` is the current ranked season (1200 / 350 / 0 when unrated). Errors: `409 no_profile`.

### `POST /profile-update` (M6)

Body `{ "displayName"?: string, "character"?: "whiskers" | "pawtruth" }`, at least one. The name is normalised
(trimmed, runs of spaces collapsed, upper-cased) and must then be 3-14 characters of `A-Z 0-9 space` and not reserved
(house ghost names; names starting STRANGER, ADMIN, MODERATOR, PAWNOON, PAW NOON, SUPPORT, SYSTEM, HOUSE GHOST). Same
rules and test vectors as the game's keypad (`Tools/Parity/display_names.json`). The new name is also written onto
the player's pooled ghost recordings. 200 = the full profile (same shape as `profile-read`).
Errors: `400 bad_display_name` with `reason: too_short | too_long | bad_chars | reserved`, `400 bad_request`,
`409 no_profile`.

```bash
curl -s -X POST "$API/functions/v1/profile-update" -H "Authorization: Bearer $JWT" -H "apikey: $ANON" \
     -H "Content-Type: application/json" -d '{"displayName":"billy the cat"}'     # -> "displayName": "BILLY THE CAT"
```

### `POST /daily-claim`

Body `{}`. It pays `20` coins once per `PN_DAY_TZ` day after `3` **server-validated wins** that day, and keeps a streak
(it resets when a day is missed).
```json
{ "claimed": true, "alreadyClaimed": false, "winsToday": 3, "winsRequired": 3, "reward": 20,
  "coins": 40, "streak": 2, "day": "2026-10-20", "timeZone": "Asia/Kolkata" }
```
`claimed: false` with `alreadyClaimed: false` means not enough wins yet. Calling it is idempotent.

### `POST /purchase-validate`

Validates a store purchase and grants the item (SPEC_V2 §6). The game sends Unity IAP's `Product.receipt` as is:
```json
{ "store": "app_store", "productId": "com.pawnoon.pixel.peacemaker", "restore": false,
  "receipt": "{\"Store\":\"AppleAppStore\",\"TransactionID\":\"2000000812345678\",\"Payload\":\"MIIT...\"}" }
```
Alternatives: `signedTransaction` (StoreKit 2 JWS), `transactionId`, `appReceipt` for `app_store`; `purchaseToken` for
`google_play`. Apple is checked by (1) the client's JWS verified against Apple Root CA - G3, else (2) the App Store Server
API v2 `GET /inApps/v1/transactions/{id}` (production, then sandbox) with its signed answer verified the same way, else
(3) `verifyReceipt` (deprecated by Apple, used only when the API key is not configured). Google: Play Developer API
`purchases.products.get` with a service-account token, then `:acknowledge`. Details: `Docs/PAYMENTS.md`.

200:
```json
{ "entitlements": [ { "item": "peacemaker", "productId": "com.pawnoon.pixel.peacemaker", "store": "app_store",
                      "transactionId": "2000000812345678", "environment": "sandbox", "status": "valid" } ],
  "outcome": "granted",
  "profile": { "ownedItems": ["peacemaker"], "coins": 0, "equippedGun": "peacemaker" } }
```
`outcome`: `granted` | `already_owned` (same purchase again: idempotent) | `transferred` (the purchase was on another
account and this device proved it owns it, or it is bound to this account: it moves here, the old account loses the item) | `pending` | `refunded` (store status, nothing granted).
`transactionId` is the canonical key: Apple's original transaction id, Google's order id. A fresh purchase equips the gun;
a restore does not change the loadout.

Errors: `409 account_mismatch | owned_elsewhere` (review pass 2: the purchase belongs to another account and the
request does not prove this device owns it; see `Docs/PAYMENTS.md` §6); `400 bad_request | unknown_product | bad_receipt`; permanent `422 invalid_receipt | wrong_app | product_mismatch |
not_in_receipt | not_found | fake_store | sandbox_not_allowed` (the client finishes the store transaction without the
item); transient `503 store_unavailable | store_not_configured` (the client keeps the transaction pending and retries).

### `POST /loadout-equip`

`{ "gun": "peacemaker" }` -> `200 { "equippedGun": "peacemaker", "ownedItems": ["peacemaker"] }`; `403 not_owned`;
`400 bad_request`. Clients may also update `profiles.equipped_gun` directly (RLS own row); a check constraint keeps it
to `standard` or an owned item.

---

## Parity

`Tools/Parity/vectors.json` holds seeds with raw `Random.Next()` values, hold lengths and cue offsets, the damage
curve and grades, 27 single exchanges (tie window edges, anticipation boundary, fouls, voids, no-shot, kill-stops,
the authority's implausible-report path), and 10 three-round recorded matches (void replay, KO, double KO, ghost
exhausted, HP draw, max attempts).

- **Source of truth:** `Assets/PawNoon/Code/Tests/EditMode/ParityVectorsTests.cs` runs the real C# Core. When its
  output differs from the committed file (numbers to 1e-9), it rewrites `vectors.json` and fails, so
  `git diff Tools/Parity/vectors.json` shows what changed. `PN_PARITY_REWRITE=1` forces a rewrite.
- **Replay:** `functions/_shared/duel/duel.test.ts` (`deno test`). CI fails on any divergence and when
  `functions/_shared/duel_config.json` is not a byte copy of the game's file.
- **Independent reference:** `Tools/Parity/reference_vectors.py` is a separate Python port of the same C#, written
  without the TypeScript. It produced the first committed `vectors.json` and must agree with both.

When you change `duel_config.json`: run `supabase/scripts/sync_duel_config.sh`, run the Unity EditMode tests (they
regenerate the vectors), then commit the config, the copy and the vectors together.
