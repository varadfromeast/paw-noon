# Paw Noon backend (Supabase)

Milestone M3 of `Docs/SPEC_V2.md`: Postgres schema, Auth, and Edge Functions for **recorded ("ghost") PvP duels**
(D1, D1b), plus account deletion, the daily bounty, and the purchase-validation contract.

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
  migrations/0007_m7_payments.sql   purchases.item, profiles.equipped_gun, pn_grant_entitlement (M7)
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
      glicko.ts, ratings.ts    Glicko-lite MMR
      auth.ts, db.ts, http.ts, server_config.ts, match_types.ts, rows.ts, server.test.ts
    payments/                  M7: der.ts + jws.ts (Apple JWS chain, WebCrypto only), apple.ts, google.ts,
                               receipt.ts (Unity IAP receipts), validate.ts, payments.test.ts + fixtures/
    match-start/ match-submit/ account-delete/ daily-claim/ purchase-validate/ loadout-equip/
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
deno test --allow-read supabase/functions/_shared/      # parity + validator + Glicko
supabase/scripts/sync_duel_config.sh --check            # rules copy in sync
node Tools/Parity/run_node_tests.ts                     # same tests without Deno (Node >= 22.18)
python3 Tools/Parity/reference_vectors.py --check       # independent Python reference agrees with vectors.json
```

## Deploy to the Mumbai project

1. Dashboard: new project, region **South Asia (Mumbai)**. Note the project ref.
2. Auth > Providers: enable **Anonymous sign-ins** and **Manual linking**, and configure **Apple** (Services ID, team, key)
   and **Google** (web + iOS + Android client IDs). Disable email sign-up (§5: no email/password).
3. From the repo root:

```bash
supabase login
supabase link --project-ref <ref>
supabase db push                                          # migrations
psql "<connection string from Dashboard > Connect>" -f supabase/seed.sql   # house ghosts (idempotent)
supabase secrets set PN_SEASON=1 PN_RANKED_BUILDS="1.0.0+42" PN_DAY_TZ=Asia/Kolkata
supabase functions deploy match-start match-submit account-delete daily-claim purchase-validate loadout-equip
```

The house ghosts are required in production: without them the first player gets `503 no_opponent`.

### Environment

| Variable | Where | Meaning |
|---|---|---|
| `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `SUPABASE_ANON_KEY` | injected by the Edge Runtime | the functions use the service role after authenticating the caller |
| `PN_SEASON` | secret | ranked season number (`ratings.season`, leaderboard `ranked_season_N`), default 1 |
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
3 rejections to flag, daily 3 wins / 20 coins) are in `functions/_shared/server_config.ts`.

---

## Database

| Table | Rows | Client access (RLS) |
|---|---|---|
| `profiles` | one per auth user (trigger on `auth.users`): name, cat, owned items, coins, stats, daily streak, rejections/flag | select own; update own `display_name`, `character`, `settings` only |
| `recordings` | the ghost pool (+ house ghosts) | **none**: served by `match-start` |
| `matches` | issued/completed/void matches, opponent snapshot, server result, MMR before/after | select own |
| `ratings` | Glicko-lite per (user, season) | select own |
| `purchases` | validated store transactions (unique per store transaction) | select own |
| `notifications` | e.g. "Stranger #1234 beat your 231 ms" | select own; update own `read_at` only |

Every write goes through a function with the service role. `pn_pick_opponent`, `pn_register_rejection`,
`pn_record_match_stats` and `pn_claim_daily` are `SECURITY DEFINER` helpers executable by `service_role` only.

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
Common errors: `401 unauthorized`, `400 bad_json`, `405 method_not_allowed`, `413 body_too_large`, `500 internal|db_error`.

### `POST /match-start`

Issues a match against a ghost (closest MMR, never yourself, never one you have faced, ±150 widening;
otherwise a house ghost on a fresh seed). Any match you left open is expired, and in ranked it counts as a loss.

Request (`schemas/match-start.request.schema.json`):
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
    "character": "blackpaw",
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
Errors: `400 bad_request`; `403 ranked_not_allowed` with `reasons: ["link_account" | "account_flagged" | "client_build"]`;
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
In ranked, `rating` is `{ "season": 1, "before": 1200, "after": 1162, "delta": -38, "rd": 290 }` and the ghost's
owner is rated too. The owner of a non-house ghost gets a `notifications` row (`kind: "ghost_result"`,
`payload: { matchId, mode, result: "won"|"lost"|"draw", from: {displayName, character}, yourBestNs,
theirBestNs, mmrDelta, message }`).

Errors:
- `400 bad_request | bad_recording` (shape). The match stays open, so the client may fix and resend before the deadline.
- `404 match_not_found`, `409 already_submitted` (`matchStatus`). There is one submission per match id.
- `500 db_error | internal`: if nothing was written yet the match goes back to `issued`, so the client may resend
  before `deadlineAt`; if a rating was already written it is voided as `server_error` (never counted). A retry that
  then gets `409` with `matchStatus: "void"` must not be resent. Failures that only concern the ghost's owner (their
  rating, their notification) or that happen after the match is completed (stats) are logged and do not fail the call.
- `503 busy`: the rating row kept changing under concurrent submits; the match is voided as `server_error`.
- `422 recording_rejected` with `reason`, `matchStatus: "void"`, `flagged`. The match is void and the reason is logged.
  Reasons (§4.3): `late` (after `deadlineAt`, does **not** count towards the flag), `loadout_not_owned`, `version`,
  `seed_mismatch`, `difficulty`, `round_count`, `round_numbering`, `reaction_bounds`, `kind_mismatch`,
  `inconsistent_reaction`, `non_monotonic`, `cue_timing`, `input_source`. Three counted rejections flag the account
  (ranked disabled).

### `POST /account-delete`

```bash
curl -s -X POST "$API/functions/v1/account-delete" -H "Authorization: Bearer $JWT" -H "apikey: $ANON" \
     -H "Content-Type: application/json" -d '{"confirm":"DELETE"}'
```
200 `{ "deleted": true, "userId": "<uuid>" }`. This hard-deletes the auth user. Cascades remove the profile, the user's
recordings, matches, ratings, purchases and notifications. Errors: `400 confirm_required`, `500 delete_failed`.

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
account: it moves here, the old account loses the item) | `pending` | `refunded` (store status, nothing granted).
`transactionId` is the canonical key: Apple's original transaction id, Google's order id. A fresh purchase equips the gun;
a restore does not change the loadout.

Errors: `400 bad_request | unknown_product | bad_receipt`; permanent `422 invalid_receipt | wrong_app | product_mismatch |
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
