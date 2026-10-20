# Paw Noon — Spec v2: Difficulty, Characters, Online PvP, Accounts, Payments

Status: **draft for implementation**. Owner: Varad. Architecture: lead (Fable). Implementation: Opus 5.5 agents, one milestone per agent, in the order in §9.
Everything here builds on the shipped game (see `Docs/ARCHITECTURE.md`, `Docs/TIMING.md`). Nothing in this spec may change how a reaction time is measured on the device.

---

## 0. Decisions already taken (change here, not in code)

| # | Decision | Why |
|---|---|---|
| D1 | Backend = **Supabase** (free tier): Postgres, Auth with Apple + Google sign-in, Edge Functions (TypeScript) for match logic and receipt validation, Realtime channels reserved for live duels later. The duel rules are a TypeScript port of `PawNoon.Core` with a parity test against the C# tests. | Everything launch needs is inside the free tier; no server to run. Nakama (self-hosted) was the earlier pick and remains the fallback if we outgrow Supabase. |
| D1b | PvP at launch is **recorded ("ghost") duels**: real players' reaction times, played asynchronously. **Live duels** (both phones online, server sends DRAW to both) are phase 2 on the same rules. | Works with any number of concurrent players, costs two HTTP calls per match, and the comparison of two locally measured reactions is identical either way. |
| D2 | **Reaction time is measured on the device, never by the server.** The server decides *when* to send DRAW, validates the report, and compares reactions. | This is the only way a 30 ms Delhi-to-Mumbai hop and a 180 ms Delhi-to-Frankfurt hop produce the same fair result. It is exactly the "fair ordering by reaction time" model from the literature (see §4.3) and already how our `ShotReport` works. |
| D3 | Difficulty (Easy / Hard) applies to **solo vs Pawtruth only**. Online PvP always uses Hard rules. | Two humans must play by the same rules. |
| D4 | Every playable character has a **back view** (you) and a **front view** (your opponent sees you). Launch roster: **Whiskers** (the white cat) and **Pawtruth** (the black cat, the bot persona formerly called Pawtruth). | PvP means your character is somebody's opponent. Pawtruth already has a front view; Whiskers already has a back view; we need the other two (§3). |
| D5 | Guests can play instantly (device login); Apple/Google sign-in is offered after the first match and **links** the same account. | Zero-friction first duel; progress is never lost. |
| D6 | First purchase: the **Peacemaker** revolver. It changes the **damage curve shape** (bigger reward for PERFECT/LIGHTNING, same for slow shots), plus its own look and sound. In **ranked** PvP guns are **normalised** (cosmetic only). | Paid power that stays fair where it matters. See §6. |

Open questions for Varad are in §10.

---

## 1. Difficulty: Easy and Hard

Hard = the game exactly as it is today (`duel_config.json`). Easy is an **overlay** file `duel_config.easy.json` merged on top at load; only the keys below change. Selected on the title screen (toggle under DUEL, remembered in PlayerPrefs). Players pick on first launch (card with both options); remembered after that.

| Key | Hard (today) | Easy | Effect |
|---|---|---|---|
| `damageMs` | 150,200,250,300,400,600,1000,1500 | **230,290,350,420,540,760,1100,1500** | same damage for ~+80 ms slower reactions |
| `gradeLightningMs / Perfect / Great / Good / Slow` | 200/250/320/420/700 | **280/340/420/540/800** | stamps feel earned |
| `botMeanStartMs / botMeanEndMs` | 300 / 220 | **420 / 340** | Pawtruth is beatable |
| `botSdStartMs / botSdEndMs` | 50 / 30 | **90 / 70** | he is also less consistent |
| `botFeintChance / botFeintMaxChance` | 0.35 / 0.85 | **0.15 / 0.40** | fewer fake-outs |
| `botTellChance` | 0.2 | **0.45** | he telegraphs more often |
| `noShotTimeoutSeconds` | 1.5 | **2.0** | more time before a NO SHOT |
| `holdMaxSeconds` | 4.0 | **3.0** | shorter panic |

Unchanged on purpose: `anticipationMs` 100 (anything under 100 ms is physiologically a guess), `tieWindowMs` 15, the countdown, the hold minimum.
Result card shows the difficulty badge. Leaderboard entries (§7) carry the difficulty; Easy never ranks.

Implementation: `DuelConfig.Load(hardJson, overlayJson)` using `PnJson` merge; `PawGame` passes the overlay when Easy is selected; a `DifficultyTests` EditMode test asserts the overlay changes only the whitelisted keys.

---

## 2. Character select

- New screen between title and duel: **CHOOSE YOUR CAT**. Two cards (portrait, name, one-line flavour), swipe or tap to pick, the pick is remembered.
- `Resources/Data/characters.json`:
  ```json
  { "whiskers": { "name": "WHISKERS", "back": "Characters/Whiskers", "front": "Characters/WhiskersFront", "portrait": "whiskers_portrait", "lines": "whiskers" },
    "blackpaw": { "name": "BLACKPAW", "back": "Characters/PawtruthBack", "front": "Characters/Bandit", "portrait": "bandit_portrait", "lines": "blackpaw" } }
  ```
- Solo: you play your pick; the opponent is the *other* cat as Pawtruth-style bot (the bot persona, banter lines and tunables come from `characters.json`, so Whiskers taunts too when he is the opponent).
- `DuelActor` already loads a character folder by name; the only change is which folder each slot loads. Positions keep using `heroBottomLeft` for slot 0 and `banditFeet` for slot 1 regardless of character; each character JSON carries its own pivot/foot anchor (already true).

---

## 3. Artwork needed (Varad generates, lead processes)

Same pipeline as before: magenta background, 270-grid pixel art, the same pose layout as the existing sheets so `build_v3.py` can cut them.

| # | Asset | Layout to copy | Used for |
|---|---|---|---|
| A1 | **Whiskers front view** master sheet: ready, draw, aim, fire, hit, down, win_spin, win_tip | `Tools/ArtGen/v3/src_master.png` (Pawtruth's 8 poses) | Whiskers as the opponent (PvP, and solo when you pick Pawtruth) |
| A2 | **Pawtruth back view**, 4 sheets: ready+draw, aim+fire, hit+down, win_spin+win_tip | `Tools/ArtGen/v3/hr_*.png` (hero 2-pose sheets) | playing as Pawtruth |
| A3 | Whiskers portrait is done; **Pawtruth portrait** is done | — | character cards |
| A4 | **Peacemaker revolver**: holster sprite (bottom-centre), draw-frame overlay for both back views, muzzle flash (bigger, bluish), shop card art | `Art/UI/holster_zone.png` size; muzzle in `Tools/ArtGen/fx_v3` | §6 |
| A5 | **Shop / character-select panel** frame and a coin icon | `Art/UI/panel_wood.png`, `btn_large.png` style | §2, §6 |
| A6 | Optional: an **online badge** (two crossed revolvers) for the PvP button and a **flag/ping** glyph | small, 16–24 px | §4 |

Nothing else is needed from you for §1, §4, §5 or §7: those are code.

---

## 4. Online PvP

### 4.0 Match model: recorded duels first, live duels later
A duel is two locally measured reaction times per round, compared. The opponent does not have to be online for that to be real player-vs-player, so launch uses **recorded (ghost) duels**:

1. Tap **PVP** → `POST /match/start` → server returns an opponent **recording**: `{seed, rounds:[{reactionNs, outcome}], character, loadout, displayName, mmr}` chosen from the pool (closest MMR, not yourself, not seen before).
2. Your phone plays the match with that `seed`, so the countdown and hold lengths are **identical** to what the opponent experienced. The opponent's cat draws at their recorded reaction each round (or fouls/no-shots as they did). Damage, ties, grades: the normal `Core` rules, run locally for presentation.
3. `POST /match/submit` with your recording. The Edge Function recomputes the whole match from both recordings (never trusts the client's result), writes the result, updates MMR, and queues a notification for the opponent ("Pawtruth from Pune beat your 231 ms"). Your recording joins the pool as somebody's next opponent.
4. First ever match (empty pool): the opponent is a seeded recording we ship (three house ghosts), labelled as such.

Live duels (phase 2): same rules, the server sends `DrawCue` to both phones over a Supabase Realtime channel and both submit `ShotReport`s; §4.3 validation applies unchanged. Nothing in the launch design blocks this.

### 4.1 Topology (launch)
```
 Phone (Unity)                                   Supabase (free tier)
 ┌───────────────────────┐   HTTPS (2 calls)    ┌──────────────────────────────┐
 │ DuelClient (C#, Core) │────────────────────►│ Edge Functions (TypeScript)  │
 │ GhostOpponent         │◄────────────────────│  match/start, match/submit,  │
 │ (plays the recording) │                     │  purchase/validate, account  │
 │ SupabaseApi.cs        │                     │ Postgres: profiles, recordings│
 └───────────────────────┘                     │  matches, ratings, purchases │
                                               │ Auth: device, Apple, Google  │
                                               └──────────────────────────────┘
```
The client code path stays the solo one: `DuelDirector` → `LocalAuthority` with slot 1 fed by `GhostOpponent : IOpponent` instead of `BotClient`. Same choreography, same timing.

### 4.2 Recording format
`Recording { version, seed, difficulty:"hard", character, loadout:{gun}, rounds:[{roundNumber, kind: shot|early|anticipation|noshot, reactionNs, cuePresentedNs, inputNs, inputSource, frameQuantized}], device:{model, os, refreshHz}, clientBuild, submittedAt }`. Raw swipe samples (the two OS touch samples around the threshold) are attached for ranked matches, for audit.

### 4.3 Fairness and anti-cheat (server side)
Accepted only if: every `reactionNs ≥ anticipationMs` (100 ms) and `≤ noShotTimeout`; round count and seed match the issued match; `inputNs − cuePresentedNs == reactionNs`; cue timestamps are monotonic and consistent with the seed's hold lengths; the recording arrives within `matchDuration + 60 s` of `match/start`; one submission per match id. Rejected = match void, logged with reason; three rejections = account flag, ranked disabled.
Ranked also requires a linked Apple/Google account and a client build on the allow-list. Guns: in ranked the server scores both recordings with the standard damage curve; in casual each player's own loadout applies.
For live duels later, the standard approach applies unchanged: order by the reaction time carried in the message, never by arrival time ([Fair-Ordering Service, NetGames '03](https://www.cs.ubc.ca/~krasic/cpsc538a/papers/game-netgame03.pdf)); `ClockSync.cs` already exists for that.

### 4.4 Matchmaking
- **Quick Duel:** pool query by MMR band (±150, widening), excluding own and recently played recordings; recordings expire from the pool after 30 days or 20 uses.
- **Friend Duel:** 4-letter room code → your recording is reserved for that friend; they play it next.
- **Ranked / Casual:** Glicko-lite MMR for ranked, weekly leaderboard `ranked_season_N`; casual unrated.
- Match = 3 rounds (configurable) scored by total damage dealt; HP carries across rounds as in solo.

### 4.5 Server implementation (Supabase)
- `supabase/functions/_shared/duel/*.ts`: port of `RoundSchedule`, `DamageCurve`, `MatchState`, `LocalAuthority`. **Parity test:** `Tools/Parity/vectors.json` is generated by the C# EditMode tests and replayed by `deno test`; CI fails on divergence.
- `supabase/functions/match-start`, `match-submit`, `purchase-validate`, `account-delete`, `daily-claim`.
- `supabase/migrations/*.sql`: `profiles`, `recordings`, `matches`, `ratings`, `purchases`, `notifications` with row-level security (a player reads only their own rows; recordings are read through the function, never directly).
- Local dev: `supabase start` (Docker) + `supabase functions serve`.

### 4.6 Client implementation (C#)
- `Code/Net/SupabaseApi.cs` (UnityWebRequest + JWT, retry, offline queue for submissions), `Code/Net/GhostOpponent.cs : IOpponent` (replays a recording through the same `IDuelTransport` seam the bot uses), `OnlineDirector.cs` (PVP button, opponent card, result + notification inbox).
- `BotClient` and `GhostOpponent` share `IOpponent`; `DuelDirector` does not know which one it has.
- Smoke test: `ReviewDirector` plays a seeded match, submits to local Supabase, a second seeded run plays that recording and the server's result equals the client's.

## 5. Accounts

- Supabase Auth: anonymous sign-in on first launch (guest, display name "Stranger #1234"). After the first solo match: sheet "Keep your bounty" → **Sign in with Apple** (iOS) / **Google Sign-In** (both), which **links** the identity to the same user (`linkIdentity`). Later installs on another phone authenticate with Apple/Google and get the same account.
- Profile row in `profiles`: display name, chosen cat, owned items, settings, stats (matches, wins, best reaction, lightning count).
- Deleting the account (App Store requirement): settings → "Delete account" → the `account-delete` function.
- No email/password. No custom auth code.

---

## 6. Payments

- Unity IAP (`com.unity.purchasing`) with App Store + Google Play. Products: `com.pawnoon.pixel.peacemaker` (non-consumable, ₹299 / $2.99 tier), later `coins_small/medium` (consumable).
- Flow: purchase → receipt → `purchase-validate` → the function verifies the receipt with Apple (App Store Server API) / Google (Play Developer API) → writes the entitlement to `purchases` and the profile → client refreshes the profile. Never trust the client's "I own it".
- Restore purchases button (required on iOS) → re-validate.
- **Peacemaker effect:** `damageValue` becomes `[48,42,34,26,16,8,4,2]` (top three bands up, slow bands unchanged), plus its own holster sprite, draw sound (`sfx_peacemaker_draw`), heavier gunshot, blue-white muzzle. Applies in solo and casual; in ranked the server uses the standard curve for everyone and only the look/sound differs.
- Shop screen: one card for now (Peacemaker), coin balance, restore button. Art: §3 A4–A5.

---

## 7. Progression and leaderboards (cheap, high retention)
- Leaderboards: `best_reaction_hard` (lowest ms, from server-validated recordings), `ranked_season_N`.
- Daily bounty: 3 solo wins → 20 coins (`daily-claim`). Streak counter on the title screen.
- Stats on the profile card (best reaction, lightning count, win rate).

---

## 8. More immersion (lead's recommendations, in priority order)

1. **Reaction replay card.** After each exchange, a 1-second pixel replay strip: the DRAW frame, your swipe moment, Pawtruth's shot, with a ms ruler. Cheap (we have every timestamp) and it's the shareable moment. Share button renders it as a PNG.
2. **Cat-specific banter and voice.** Each character gets its own line set and a 3-note "meow" synth signature on taunts (Whiskers higher, Pawtruth lower). Config only + 2 tiny SFX.
3. **Crowd and town reactions.** A saloon door creak and two townsfolk silhouettes that duck behind the barrels at "1", peek out after the exchange. A tumbleweed only on NO SHOT. Art: two 16-px silhouettes, 2 frames each.
4. **Sudden death.** When both cats are ≤ 20 HP the next exchange is "LAST LIGHT": the dawn plate crossfades in, the ECG goes flat-line-then-spike, damage is doubled. Already half built (`StreetScene.SetDawn`).
5. **Reload ritual.** Between exchanges the player spins the cylinder with a small circular swipe; finishing it within the countdown gives a "STEADY" buff: +1 damage band on the next shot. Optional, so it never gates play.
6. **Controller/haptic language.** A distinct haptic for each stamp (LIGHTNING = triple tap) and a soft haptic on each ECG beat (done). Real devices only.
7. **Match intro.** On first PvP match of a session: both cats walk in from opposite sides (2 s), name plates slam in. Uses existing idle frames + slide.
8. **Weather pass.** 1-in-6 matches at dusk, 1-in-10 in a dust storm (particles + 10% darker plate). Art: one dusk plate (we have dawn).

---

## 9. Milestones for the Opus 5.5 agents (each is one PR)

| M | Scope | Depends on | Done when |
|---|---|---|---|
| M1 | §1 Difficulty overlay + title toggle + tests | — | Easy/Hard both play through with `ReviewDirector`; tests green |
| M2 | §2 Character select + `characters.json` + bot persona from character | A1, A2 processed by lead | either cat playable vs the other |
| M3 | §4.5 Supabase: schema, TypeScript authority port + parity test, `match-start` / `match-submit` | — | `deno test` passes the C# vectors; local `supabase start` serves a match |
| M4 | §4.6 `GhostOpponent` + `SupabaseApi` + `OnlineDirector` + room codes + quick duel | M3 | a seeded match submits and is replayed as an opponent against local Supabase |
| M5 | §4.3 validator + §4.4 pool matchmaking / ranked / notifications | M4 | bad recordings rejected; ranked leaderboard updates; opponent gets the result |
| M6 | §5 Accounts (device → Apple/Google link, profile, delete) | M4 | sign-in round-trips on a real iPhone and Android |
| M7 | §6 Unity IAP + server validation + shop + Peacemaker | M6, A4–A5 | sandbox purchase grants the gun on both stores |
| M8 | §7 + §8.1, 8.2, 8.4 | M1 | replay card, banter sets, sudden death |

Rules for every milestone: no change to `Code/Core` timing or to `SwipeDraw`; all new tunables in JSON; EditMode tests for Core changes; the game must still run fully offline (solo) when the server is unreachable; pixel art only (see `TEAMMATE_SETUP.md` house rules).

---

## 10. Open questions for Varad

1. Supabase project region: **Mumbai** (ap-south-1) is the obvious pick for Indian players first. Confirm.
2. Peacemaker price tier (₹299 / $2.99 suggested) and whether coins exist at launch or only the one gun.
3. Flavour text for the Whiskers and Pawtruth cards (one line each), or I write them.
4. Rounds per PvP match: 3 (suggested) or 5.
