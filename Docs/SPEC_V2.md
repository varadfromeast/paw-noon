# Paw Noon — Spec v2: Difficulty, Characters, Online PvP, Accounts, Payments

Status: **draft for implementation**. Owner: Varad. Architecture: lead (Fable). Implementation: Opus 5.5 agents, one milestone per agent, in the order in §9.
Everything here builds on the shipped game (see `Docs/ARCHITECTURE.md`, `Docs/TIMING.md`). Nothing in this spec may change how a reaction time is measured on the device.

---

## 0. Decisions already taken (change here, not in code)

| # | Decision | Why |
|---|---|---|
| D1 | Backend = **Nakama** (open source, Go) with an **authoritative Go match handler** that is a line-for-line port of `PawNoon.Core.LocalAuthority`. Hosted on one small VM to start (Hetzner/Fly.io, ~$10/mo), Heroic Cloud later if we grow. | One server covers everything we need: Apple/Google sign-in, matchmaker, authoritative real-time matches, wallet, Apple/Google receipt validation, leaderboards, and it has an official Unity SDK. The alternative (Unity Gaming Services: Auth + Lobby + Relay + Cloud Code) needs 4 services and still leaves us writing the duel logic in Cloud Code. |
| D2 | **Reaction time is measured on the device, never by the server.** The server decides *when* to send DRAW, validates the report, and compares reactions. | This is the only way a 30 ms Delhi-to-Mumbai hop and a 180 ms Delhi-to-Frankfurt hop produce the same fair result. It is exactly the "fair ordering by reaction time" model from the literature (see §4.3) and already how our `ShotReport` works. |
| D3 | Difficulty (Easy / Hard) applies to **solo vs Blackpaw only**. Online PvP always uses Hard rules. | Two humans must play by the same rules. |
| D4 | Every playable character has a **back view** (you) and a **front view** (your opponent sees you). Launch roster: **Whiskers** (the white cat) and **Blackpaw**. | PvP means your character is somebody's opponent. Blackpaw already has a front view; Whiskers already has a back view; we need the other two (§3). |
| D5 | Guests can play instantly (device login); Apple/Google sign-in is offered after the first match and **links** the same account. | Zero-friction first duel; progress is never lost. |
| D6 | First purchase: the **Peacemaker** revolver. It changes the **damage curve shape** (bigger reward for PERFECT/LIGHTNING, same for slow shots), plus its own look and sound. In **ranked** PvP guns are **normalised** (cosmetic only). | Paid power that stays fair where it matters. See §6. |

Open questions for Varad are in §10.

---

## 1. Difficulty: Easy and Hard

Hard = the game exactly as it is today (`duel_config.json`). Easy is an **overlay** file `duel_config.easy.json` merged on top at load; only the keys below change. Selected on the title screen (toggle under DUEL, remembered in PlayerPrefs). Default for a new install: **Easy**.

| Key | Hard (today) | Easy | Effect |
|---|---|---|---|
| `damageMs` | 150,200,250,300,400,600,1000,1500 | **230,290,350,420,540,760,1100,1500** | same damage for ~+80 ms slower reactions |
| `gradeLightningMs / Perfect / Great / Good / Slow` | 200/250/320/420/700 | **280/340/420/540/800** | stamps feel earned |
| `botMeanStartMs / botMeanEndMs` | 300 / 220 | **420 / 340** | Blackpaw is beatable |
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
    "blackpaw": { "name": "BLACKPAW", "back": "Characters/BlackpawBack", "front": "Characters/Bandit", "portrait": "bandit_portrait", "lines": "blackpaw" } }
  ```
- Solo: you play your pick; the opponent is the *other* cat as Blackpaw-style bot (the bot persona, banter lines and tunables come from `characters.json`, so Whiskers taunts too when he is the opponent).
- `DuelActor` already loads a character folder by name; the only change is which folder each slot loads. Positions keep using `heroBottomLeft` for slot 0 and `banditFeet` for slot 1 regardless of character; each character JSON carries its own pivot/foot anchor (already true).

---

## 3. Artwork needed (Varad generates, lead processes)

Same pipeline as before: magenta background, 270-grid pixel art, the same pose layout as the existing sheets so `build_v3.py` can cut them.

| # | Asset | Layout to copy | Used for |
|---|---|---|---|
| A1 | **Whiskers front view** master sheet: ready, draw, aim, fire, hit, down, win_spin, win_tip | `Tools/ArtGen/v3/src_master.png` (Blackpaw's 8 poses) | Whiskers as the opponent (PvP, and solo when you pick Blackpaw) |
| A2 | **Blackpaw back view**, 4 sheets: ready+draw, aim+fire, hit+down, win_spin+win_tip | `Tools/ArtGen/v3/hr_*.png` (hero 2-pose sheets) | playing as Blackpaw |
| A3 | Whiskers portrait is done; **Blackpaw portrait** is done | — | character cards |
| A4 | **Peacemaker revolver**: holster sprite (bottom-centre), draw-frame overlay for both back views, muzzle flash (bigger, bluish), shop card art | `Art/UI/holster_zone.png` size; muzzle in `Tools/ArtGen/fx_v3` | §6 |
| A5 | **Shop / character-select panel** frame and a coin icon | `Art/UI/panel_wood.png`, `btn_large.png` style | §2, §6 |
| A6 | Optional: an **online badge** (two crossed revolvers) for the PvP button and a **flag/ping** glyph | small, 16–24 px | §4 |

Nothing else is needed from you for §1, §4, §5 or §7: those are code.

---

## 4. Online PvP

### 4.1 Topology
```
 iPhone/Android (Unity)                 Nakama (Go, one VM, Postgres)
 ┌──────────────────────┐   WebSocket   ┌─────────────────────────────┐
 │ DuelClient (C#, Core)│◄────────────►│ Match handler "duel" (Go)   │
 │ SwipeDraw, ClockSync │   JSON msgs   │  = port of LocalAuthority   │
 │ NakamaTransport      │               │ Matchmaker, Auth, Wallet,   │
 │  : IDuelTransport    │               │ IAP validation, Leaderboard │
 └──────────────────────┘               └─────────────────────────────┘
```
- The client code path is **identical** to solo: `DuelDirector` → `DuelClient` → `IDuelTransport`. Today the transport is `LoopbackTransport` and the authority runs locally; online, `NakamaTransport` sends the same structs over the socket and the authority runs on the server. The bot never exists online.
- Messages (JSON, op-codes): `RoundStart`, `DrawCue`, `ShotReport`, `RoundResult` (new: authority → clients, the resolved `RoundRecord`), `MatchEnd`, `Ping/Pong` (for `ClockSync`), `Emote`.

### 4.2 A round, online
1. Server: `RoundStart` to both (countdown beats + beat length). Each phone runs its own 3-2-1 on arrival. The hold length is drawn **on the server** and is never sent.
2. Server: at the cue instant sends `DrawCue` to both. Each phone presents DRAW on arrival and stamps `cuePresentedNs` on its own clock (frame + latency estimate, as today).
3. Player swipes → `ShotReport{reactionNs, cuePresentedNs, inputNs}` → server.
4. Server resolves with the existing rules: faster reaction wins the exchange, damage from the curve, ties within 15 ms, fouls for early/anticipation. Sends `RoundResult`. Both phones play the choreography from the result (the hit, HP drain, stamps, banter).
5. **Waiting rule:** the round resolves when both reports are in, or `noShotTimeout + reportSlack` after the cue. A missing report = NO SHOT, exactly like today.

What the opponent sees: their cat's draw animation plays when the server result arrives (≈ one RTT after the shot). With both reactions ~250 ms and RTT ~80 ms this is not noticeable; the choreography is already result-driven (`DuelDirector` renders events, not inputs).

### 4.3 Fairness and anti-cheat (server side)
A report is **accepted** only if all hold:
- `reactionNs ≥ anticipationMs` (100 ms) and `≤ noShotTimeout`;
- `cuePresentedNs` is consistent with the server's cue send time: `|(cuePresentedNs − offset) − cueSentNs| ≤ RTT/2 + presentSlack` where `offset` comes from the client's `ClockSync` samples the server also keeps;
- the report **arrives** no earlier than `cueSentNs + RTT/2 + reactionNs − slack` (you cannot have reacted before the cue could reach you) and no later than `cueSentNs + RTT + reactionNs + reportSlackMs`;
- one report per slot per round; round id matches.
Rejected report = foul for that slot (shown as "MISFIRE"), logged with reason. Three rejections in a match = match forfeited + account flag.
This is the standard approach for reaction games over the Internet: order by *reaction time carried in the message*, not by arrival time, so a player on a slow link is not penalised ([Fair-Ordering Service, NetGames '03](https://www.cs.ubc.ca/~krasic/cpsc538a/papers/game-netgame03.pdf)). Our clock sync is NTP-style (`ClockSync.cs`), sampled 8× in the lobby and once per round via `Ping/Pong`.
Players with RTT > 250 ms or jitter > 60 ms are matched only with each other (matchmaker property `region`, `rttBand`) and see a yellow ping glyph.

### 4.4 Matchmaking and lobby
- **Quick Duel:** Nakama matchmaker, query `+properties.region:<same> +properties.mode:ranked|casual`, min 2 / max 2, 20 s timeout, then widen region. Ticket properties: `region` (from server-measured RTT to our one server for now), `mmr` (ranked), `mode`.
- **Friend Duel:** 4-letter room code (`match.create` + code → match id map stored in Nakama storage, 10 min TTL). This is what small reaction-duel apps already do (e.g. a room code shared out of band).
- Match = best of 5 exchanges or first to 0 HP, same as solo. Disconnect: 10 s grace with the countdown paused; after that the absent player forfeits.
- Ranked: Glicko-lite MMR stored in Nakama storage, leaderboard "ranked_season_N" weekly; casual: no rating.

### 4.5 Server implementation (Go)
- `server/duel/authority.go`: port of `LocalAuthority`, `RoundSchedule`, `DamageCurve`, `MatchState`. **Parity test:** `Tools/Parity/vectors.json` is generated by the C# EditMode tests (seed → sequence of reports → expected events) and replayed by `go test`; CI fails on any divergence.
- `server/duel/match.go`: Nakama `MatchHandler` (tick 60/s during a round, 5/s in lobby), presence join/leave, the §4.3 validator, result broadcast.
- `server/rpc/*.go`: `rpc_create_room`, `rpc_join_room`, `rpc_validate_purchase`, `rpc_claim_daily`.
- `docker-compose.yml` (nakama + postgres) for local dev; one `deploy.sh` for the VM. Config in `server/local.yml`.

### 4.6 Client implementation (C#)
- `Code/Net/NakamaTransport.cs : IDuelTransport` (Nakama Unity SDK via UPM `com.heroiclabs.nakama-unity`), `NetSession.cs` (socket lifecycle, reconnect), `OnlineDirector.cs` (lobby/room UI, ping glyph, forfeit flow).
- `DuelDirector` gains `RoundResult` handling (today it reads the local authority's events directly; wrap that in an `IDuelEvents` source so solo and online feed the same choreography).
- Smoke test: two Editor instances (ParrelSync or a second clone) against local docker Nakama play a full match with `ReviewDirector` auto-tap.

---

## 5. Accounts

- Nakama auth: `AuthenticateDevice` on first launch (guest, display name "Stranger #1234"). After the first solo match: sheet "Keep your bounty" → **Sign in with Apple** (iOS) / **Google Sign-In** (both), which **links** to the same Nakama account (`LinkApple`, `LinkGoogle`). Later installs on another phone authenticate with Apple/Google and get the same account.
- Profile stored in Nakama storage `profile`: display name, chosen cat, owned items, settings, stats (matches, wins, best reaction, lightning count).
- Deleting the account (App Store requirement): settings → "Delete account" → `rpc_delete_account` (Nakama `AccountDeleteId`).
- No email/password. No custom auth code.

---

## 6. Payments

- Unity IAP (`com.unity.purchasing`) with App Store + Google Play. Products: `com.pawnoon.pixel.peacemaker` (non-consumable, ₹299 / $2.99 tier), later `coins_small/medium` (consumable).
- Flow: purchase → receipt → `rpc_validate_purchase` → Nakama validates with Apple/Google (built in) → writes `owned.peacemaker=true` to storage and the wallet → client refreshes the profile. Never trust the client's "I own it".
- Restore purchases button (required on iOS) → re-validate.
- **Peacemaker effect:** `damageValue` becomes `[48,42,34,26,16,8,4,2]` (top three bands up, slow bands unchanged), plus its own holster sprite, draw sound (`sfx_peacemaker_draw`), heavier gunshot, blue-white muzzle. Applies in solo and casual; in ranked the server uses the standard curve for everyone and only the look/sound differs.
- Shop screen: one card for now (Peacemaker), coin balance, restore button. Art: §3 A4–A5.

---

## 7. Progression and leaderboards (cheap, high retention)
- Leaderboards: `best_reaction_hard` (lowest ms, solo Hard, server-verified reports only once online exists; until then client-submitted and labelled "unverified"), `ranked_season_N`.
- Daily bounty: 3 solo wins → 20 coins (`rpc_claim_daily`). Streak counter on the title screen.
- Stats on the profile card (best reaction, lightning count, win rate).

---

## 8. More immersion (lead's recommendations, in priority order)

1. **Reaction replay card.** After each exchange, a 1-second pixel replay strip: the DRAW frame, your swipe moment, Blackpaw's shot, with a ms ruler. Cheap (we have every timestamp) and it's the shareable moment. Share button renders it as a PNG.
2. **Cat-specific banter and voice.** Each character gets its own line set and a 3-note "meow" synth signature on taunts (Whiskers higher, Blackpaw lower). Config only + 2 tiny SFX.
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
| M3 | §4.5 Go server: authority port + parity test + docker-compose | — | `go test` passes the C# vectors |
| M4 | §4.6 `NakamaTransport` + `OnlineDirector` + room codes + quick duel | M3 | two Editors play a full match via local Nakama |
| M5 | §4.3 validator + §4.4 matchmaker/ranked + forfeit/reconnect | M4 | bad reports rejected; ranked leaderboard updates |
| M6 | §5 Accounts (device → Apple/Google link, profile, delete) | M4 | sign-in round-trips on a real iPhone and Android |
| M7 | §6 Unity IAP + server validation + shop + Peacemaker | M6, A4–A5 | sandbox purchase grants the gun on both stores |
| M8 | §7 + §8.1, 8.2, 8.4 | M1 | replay card, banter sets, sudden death |

Rules for every milestone: no change to `Code/Core` timing or to `SwipeDraw`; all new tunables in JSON; EditMode tests for Core changes; the game must still run fully offline (solo) when the server is unreachable; pixel art only (see `TEAMMATE_SETUP.md` house rules).

---

## 10. Open questions for Varad

1. Region/hosting: single server in **Mumbai** (your players are in India first) or Frankfurt/Singapore too from day one? One region is enough for the first thousand players.
2. Peacemaker price tier (₹299 / $2.99 suggested) and whether coins exist at launch or only the one gun.
3. Character names: "Whiskers" for the white cat? Any flavour text you want on the cards.
4. Should Easy be the default for new installs (recommended) or should players choose on first launch?
