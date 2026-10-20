# Code review, pass 2, 20 Oct 2026

Scope: everything merged since pass 1 (`Docs/REVIEW_2026-10-20.md`): M4 online client, M5 friend rooms / ranked /
leaderboards / plausibility, M6 accounts, M7 shop + IAP + Peacemaker, M8 replay strip + LAST LIGHT, feel wave 2
(intro, badge, share card, daily bounty), the haptics rewrite, the iOS / Android plugins, all of `supabase/`
(functions, migrations 0001-0060, tests), `Packages/manifest.json` and the asmdefs. Branch `agent/review2`.

Severity: **High** = wrong results, data loss or an abuse path in normal use. **Medium** = a realistic failure or
abuse path. **Low** = hygiene, or only under unusual conditions.

How this was checked: TypeScript under `tsc --strict` plus `node Tools/Parity/run_node_tests.ts` (67 tests), SQL on a
real Postgres (`supabase/tests/pg/run.sh`: m5 + review2), `reference_vectors.py --check`, `sync_duel_config.sh --check`,
`check_migrations.sh`. No C# compiler is available here: the C# changes were checked by reading and must be compiled
and tested on the Mac (see "For the lead" at the end). The UI checks are arithmetic on the layout code, not renders.

## 1. Fixed in this branch

| # | Sev | Where | Fix |
|---|---|---|---|
| F1 | High | `supabase/migrations/0061_review2.sql:49`, `:86-91` | NOT VALID checks (0002 name hygiene + settings size, 0007 loadout, 0060 name shape) are re-checked on **every** UPDATE of a row, so a profile named before 0060 (lower case, short, reserved) failed its next stats update in `pn_complete_match` (every validated match → 500 → resend → 500 until `late`), its daily claim, purchases and equips. `pn_repair_profiles()` normalises / regenerates such names (and renames the player's pooled ghosts), repairs oversized settings, unowned loadouts and unknown characters; then every profile check is VALIDATEd. Test `tests/pg/review2.test.sql` rebuilds the old situation and shows the failure and the repair. |
| F2 | High | `supabase/functions/_shared/payments/binding.ts:59`, `grant.ts:39`, `0061_review2.sql:145`, `purchase-validate/index.ts` | A purchase could be **taken from its owner with a bare transaction id** (the App Store Server API answers for any id of our app; validating it from another account moved it there). Now a purchase moves only with device proof (StoreKit 2 JWS, an app receipt that verifyReceipt confirms holds it, a Google purchase token) or a matching account binding; else `409 owned_elsewhere` / `account_mismatch`. `pn_grant_entitlement(..., p_allow_transfer)`. Tests in `_shared/review2.test.ts` and the SQL test. |
| F3 | Medium | `Runtime/Shop/IapService.cs:116`, `:473`; `IStore.cs:53` | Purchases are bound to the account where Unity IAP 4.x allows it: `IAppleExtensions.SetApplicationUsername(<user uuid>)` (StoreKit copies a UUID into `appAccountToken`) and `IGooglePlayConfiguration.SetObfuscatedAccountId(sha256hex(user id))`, at init and before each purchase. `PurchaseBinding` shares a test vector with `binding.ts`. The server reads `appAccountToken` / `app_account_token` / `obfuscatedExternalAccountId`. |
| F4 | High | `Runtime/Shop/GunLocker.cs:94`, `:104`, `:118`; `Net/AccountDirector.cs:497` | Integrator concern: **account delete did not clear the GunLocker**. Worse, verification never followed the account: after DELETE, SWITCH or a new guest the old "verified" Peacemaker went into PvP recordings, `match-submit` rejected them as `loadout_not_owned`, and three such matches flagged the new account. Verification is now stored with its account (`pn_owned_verified_for`, adopted by the first account on old installs); DELETE / SWITCH forget it (guns stay for solo, RESTORE re-verifies) and reset the daily bounty state. Tests `Review2Tests`. |
| F5 | Medium | `Runtime/Net/SupabaseApi.cs:152`, `:294`, `:328` | The submit queue **dropped** a match on `503 busy`, `429` and on auth failures (`relink`, `auth_failed`), although the server had put the match back to `issued`. `ApiResult.RetryLater` (0, 401, 408, 429, 5xx) now queues / keeps it; only definitive 4xx drop it. Test `OnlyDefinitiveAnswersDropAQueuedSubmit`. |
| F6 | Medium | `Runtime/Net/SupabaseAuth.cs:200` | `EnsureSession` was re-entrant: two calls at launch with no valid token (bounty claim, queue flush, PVP) both signed up an anonymous user; the second overwrote the first, so a match could start under one user and be submitted under another (404). Single flight now; waiters reuse the session. |
| F7 | Medium | `supabase/functions/_shared/rate.ts:19`, `room-join/index.ts:38`, `0061_review2.sql:95` | No rate limits on room-join (anybody could sweep the 26^4 codes and grab friend slots), profile-update (each rename rewrites the player's ghosts), purchase-validate (two store round trips per call), daily-claim, leaderboard-read, profile-read, loadout-equip, account-delete. `rate_events` + `pn_rate_take` (advisory-locked sliding window), limits in `server_config.ts RATE`; wrong room codes are counted separately (12 per 10 min). |
| F8 | Medium | `Runtime/Net/FriendDuel.cs:151` | A 429 / 5xx poll while WAITING kicked the friend back to the keypad (and lost the room view). It now keeps waiting with backoff, like an offline poll. |
| F9 | Medium | `0061_review2.sql:211-218` | Clients could read their own `profiles.shadow` / `flagged` / `rejections` through PostgREST (a shadow-banned cheater could see it) and `matches.opponent_owner_id` / `opponent_snapshot.ownerId` (another player's auth id, pass 1 D4). Column grants now; the game does not use PostgREST. SQL test. |
| F10 | Medium | `Runtime/Shop/IapService.cs:166`, `:192`; `ShopDirector.cs:65` | Shop work could land in a live round: a 45 s purchase-validate retry or a deferred purchase (Ask to Buy, slow Google card) started JSON + a coroutine + TLS mid-duel. `IapService.Quiet` holds them until the duel is over. (Bounty, queue flush, room polls already only run on title / result screens.) |
| F11 | Medium | `Runtime/HapticsLab.cs:246` | The haptics lab (a debug screen) opened in release builds by holding the top-left corner for 1.5 s, which is where the SHOP button sits, and even over the shop / account cards. Development builds only now, never over those cards. |
| F12 | Medium | `supabase/scripts/check_migrations.sh`, `migrations.lock`, `supabase/README.md` | Integrator concern: migration numbering 0003 / 0007 / 0060 vs `supabase db push`. Kept the names (renaming applied migrations breaks history), documented the one-time `--include-all` case, and added a guard: locked files may never be renamed or reordered, and a new file must sort after the last applied one. |
| F13 | Low | `supabase/functions/_shared/recording.ts:18`, `:145`; `match_response.ts:26`; `seed.sql`; `0061_review2.sql:80`; `Core/NetMessages.cs:52` | Naming drift: the server persona id is now `pawtruth`, with `blackpaw` accepted as an alias in recordings and normalised in old snapshots and on the client; rows renamed by 0061; `profiles.character` constrained to whiskers / pawtruth (clients could write any 32-char string). Tests on both sides. |
| F14 | Low | `Runtime/Net/NativeSignIn.cs:148` (+ 3 proxy methods) | `OnNativeResult` (UnitySendMessage) and the `AndroidJavaProxy` callbacks are only called by name; `[Preserve]` keeps them under IL2CPP managed stripping. |
| F15 | Low | `supabase/functions/daily-claim/index.ts:19` | Pass 1 D7: a missing profile answered `500 db_error`; now `409 no_profile` like match-start. |
| F16 | Low | `supabase/functions/*/index.ts` (`readJson(req, 1024)`) | Functions without a payload accepted 64 KB bodies; now 1 KB (match-submit / purchase-validate keep 256 KB). |
| F17 | Low | `Docs/PAYMENTS.md` §6, `supabase/README.md`, `Docs/ACCOUNTS.md`, `Docs/ONLINE_CLIENT.md`, `Docs/DECISIONS.md` | Docs follow the code: purchase binding, rate limits, column grants, deploy order, migration rules, queue semantics, delete clearing, pawtruth id, SIWA revocation plan. |

Checked and found OK (no change): every Edge Function authenticates (`requireUser`) and uses the service role only
after that; every `SECURITY DEFINER` function pins `search_path` and is `service_role`-only (also asserted for the new
ones); RLS is on for every public table (asserted for all tables in `review2.test.sql`); room codes, names, recordings
and purchase requests are validated with size caps; no string-built SQL. Timing path: haptics, share, bounty, intro,
replay strip and LAST LIGHT read the authority's records or schedules only after the fact and never write timestamps,
ShotReports or authority state; `HapticMoments.DrawCue` takes the lead the DRAW sound already uses; the swipe haptic
fires after `DuelClient.Press`. No per-frame allocation found in the new Tick / Update paths (scripted scan of every
`Update` / `Tick*` / `Poll` body plus reading the bounty strip, friend clock, keypad, lab). Android: the haptics bridge
compiles against API 33+ (VibrationAttributes, VibratorManager behind SDK checks); C# hot calls use raw JNI with
cached method ids. iOS: CoreHaptics / AuthenticationServices are linked by post-build scripts under `#if UNITY_IOS`;
`PawSignIn.mm` refuses to compile without ARC; `PawShare.mm` is safe with or without ARC. Title layout at 1080x1920
(virtual 270x480) and 1179x2556 (270x585, safe 68..588): the PLAY stack (127 px) fits the 132 px reserve, the top row
(SHOP 62 px left, three 30 px icons right) does not overlap, the longest new status lines fit their cards (font_small:
"ANOTHER FRIEND GOT THERE FIRST." 160 px, "OWNED BY ANOTHER ACCOUNT" 134 px).

## 2. Deferred

| # | Sev | Where | Finding | Proposed fix |
|---|---|---|---|---|
| D1 | High (launch) | `Assets/Plugins/NuGet/*.dll.meta` | The MCP bridge's 40 NuGet DLLs (Roslyn, SignalR, System.Text.Json ...) are enabled for **Any** platform, so they ship in player builds (size; System.Memory / Buffers duplicates are a known IL2CPP conflict source). | For release builds remove `com.ivanmurzak.unity.mcp` from the manifest (or mark the DLLs Editor-only after checking the package has no runtime asmdef that needs them). Check a release IL2CPP build size before and after. |
| D2 | Medium (App Review) | `supabase/functions/account-delete`, `Plugins/iOS/PawSignIn.mm` | Account deletion does not revoke Sign in with Apple tokens (`/auth/revoke`), which Apple asks of apps that offer SIWA. | Plan in `Docs/ACCOUNTS.md`: one more Apple sheet on DELETE for Apple-linked accounts, `authCode` → `account-delete` exchanges and revokes, then deletes. |
| D3 | Medium | `Runtime/ShareService.cs:264`, `Docs/ACCOUNTS.md` | On Android the share card falls back to text (no FileProvider) and Google sign-in is unavailable (Credential Manager Gradle lines not added). Both fail silently. | Add Custom Main Manifest + Gradle template + `PawShare.androidlib` (DECISIONS feel wave 2) and the Credential Manager dependencies; smoke-test on a device. |
| D4 | Medium | `Runtime/Net/SupabaseAuth.cs` | Access + refresh tokens live in PlayerPrefs (plain NSUserDefaults / SharedPreferences, included in backups). | Keychain (iOS, `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`) / EncryptedSharedPreferences behind a small native seam; migrate the keys once. |
| D5 | Low | `Runtime/Net/SupabaseApi.cs` SubmitQueue | Queue entries are not tied to the user. After a SWITCH / new guest a queued submit goes out under the new user → 404 → dropped; the old account's ranked match then counts as abandoned. | Store the user id per entry; flush only when it matches, else keep until the deadline. |
| D6 | Low | `OnlineDirector.OnSubmitAnswer` | If the 200 of a submit is lost and the retry answers `409 already_submitted`, the card says ALREADY SENT without the verdict. | Return the stored result with the 409 (the row has it) and show it. |
| D7 | Low | `Runtime/Net/FriendDuel.cs:226` | Room expiry compares the device clock with the server's `expiresAt`; a skewed phone closes the room early or late. | Send `remainingSec` with each answer and count down locally. |
| D8 | Low | `pn_room_join` | A friend who joins and then cancels keeps the friend slot until expiry (`room_taken` for anybody else). | `room-leave` on CANCEL, or release a slot that has not polled for 30 s. |
| D9 | Low | `Plugins/iOS/PawSignIn.mm` (Google scope `openid email`) | The Google flow asks for the email, which Supabase stores in `auth.users`: it must then be declared in the privacy labels. | Ask for `openid` only if GoTrue accepts a token without email (test on the local stack); else declare it. |
| D10 | Low | `Runtime/ShareService.cs:293` | ACTION_SEND with EXTRA_STREAM through a chooser: some targets need `setClipData` to receive the read grant. | `intent.setClipData(ClipData.newRawUri("", uri))` with the FileProvider work (D3). |
| D11 | Low | 30 files without `.meta` (`ShareCard.cs`, `BountyState.cs`, `PawShare.mm`, `link.xml`, `ico_flame.png`, ...) | Every machine generates its own GUIDs; plugin / texture import settings come from defaults or the import rules. | Commit the metas Unity generates on the Mac (one commit, no code change). |
| D12 | Low | `Runtime/DuelDirector.cs:1347` `PipeFx` | Reflection probe for pipeline fields that do not exist: dead code that always returns false. | Delete it, or add `Desaturation` to `PixelPipeline` and call it directly. |
| D13 | Low | `0061` / `purchase-validate` | A refund reported by an account that does not own the purchase (and cannot prove it) is not recorded. | Store notifications (App Store Server Notifications V2, Play RTDN) as the refund source (PAYMENTS.md §6). |
| D14 | Info | `IapService.cs` (`#if PAWNOON_IAP`) | `SetApplicationUsername` / `IGooglePlayConfiguration.SetObfuscatedAccountId` are Unity IAP 4.x APIs used for the first time here; they compile only with the package on the Mac. Some 4.x versions read the Google id only at init (set at init too; on Google the purchase token is the proof anyway). | Compile on the Mac; check `appAccountToken` in a sandbox purchase's JWS (`purchases.raw.appAccountToken`). |
| D15 | Low | daily bounty | The server's day is `PN_DAY_TZ` (IST), the client shows local days (open since feel wave 2). | Send the device's UTC offset with daily-claim; the server uses it within ±14 h. |
| D16 | - | pass 1 | Still open from pass 1: D5 (pool uses counted at start), D6 (chunked body size), D8 (one BEST for Easy and Hard), D9 (`pn_difficulty` key name), D10 (dead heart glyph), D11 (result tag geometry), D12 (ECG uploads every frame), D13 (missing Easy overlay label), D14 (frame time from targetFrameRate). Closed since: D1 (M5 `pn_complete_match`), D2 (M4 `HostActing`), D3 (feel wave 2 `ResetGestures`), D4 and D7 (this pass). | As in pass 1. |

## 3. Next improvements (prioritised)

1. **Store compliance pack (launch blocker).** Apple: a `PrivacyInfo.xcprivacy` for the app (UserDefaults CA92.1 via PlayerPrefs; no tracking, so **no ATT prompt**), App Privacy labels: user id, gameplay content (recordings, reaction times), purchases, device model / OS / refresh rate, display name, email if D9 stays; all linked to the user, none used for tracking. Google Play Data safety form with the same list, "data deleted on request" (in-app DELETE ACCOUNT exists) plus the required **web** deletion link (a one-page form or mail address calling `account-delete` by support). Age rating questionnaire (simulated gun violence, cartoon).
2. **Sign in with Apple revocation on delete** (D2) and a support path for "I lost my guest": both are App Review questions for an app with accounts and purchases.
3. **Android parity** (D3): FileProvider share, Credential Manager Gradle lines, a release keystore + AAB in `PawMenu`, `targetSdk` 35, and one physical-device smoke run of haptics latency (HAPTICS.md "measure").
4. **CI** (pass 1 #1, still open): node tests, SQL tests, `reference_vectors.py --check`, `sync_duel_config.sh --check`, `check_migrations.sh` on every PR; game-ci EditMode nightly; **IL2CPP smoke builds for iOS and Android**, because `#if UNITY_IOS` / `UNITY_ANDROID` code (haptics, sign-in, share, IAP) is compiled nowhere else.
5. **Latency calibration** (feel + fairness, pass 1 #3): a 10-flash "tap on the flash" screen that sets `presentLatencyExtraMs` per device, and the measured DRAW haptic latency per platform (now that the haptic is part of the cue).
6. **First-draw tutorial round** (retention, pass 1 #8): the first round of the first match waits for a swipe after DRAW with the hint, no damage. "A plain click does not fire" is still the most likely first-minute failure.
7. **Ghost results inbox** (retention): `notifications` already holds "Stranger #1234 beat your 231 ms"; a badge on the profile button and a small list on the WANTED card, later push (APNs / FCM) for "your ghost won". Cheap, and it gives a reason to come back.
8. **Release build hygiene** (performance / size): D1 (NuGet DLLs out), Managed Stripping Medium with `link.xml` (pass 1 #5), ECG texture upload only on change (pass 1 D12), `-warnaserror` for the PawNoon asmdefs.
9. **Secure session storage** (D4) and a `user id`-tagged submit queue (D5): an account system that moves between phones needs both.
10. **Server-side purchase notifications** (D13): App Store Server Notifications V2 and Play RTDN into a `purchase-notify` function, so refunds take the Peacemaker away without waiting for a restore.

## For the lead (Mac)

- Compile + run EditMode (both assemblies). New / changed tests: `Tests/EditModeRuntime/Review2Tests.cs` (9 tests), `Tests/EditMode/RecordingBuilderTests.cs` (character alias, `ParsesTheReadmeMatchStartAnswer` now expects `pawtruth`). Existing `GunLockerTests` must stay green unchanged (verification without an account set behaves as before).
- Compile with `com.unity.purchasing` 4.12.2 resolved (PAWNOON_IAP): `IAppleExtensions.SetApplicationUsername`, `IGooglePlayConfiguration.SetObfuscatedAccountId`, `builder.Configure<IGooglePlayConfiguration>()`.
- Switch the build target to iOS and to Android once each and let the scripts compile (the `#if` paths changed only in `NativeSignIn.cs`, attributes only).
- Server: `supabase db push` (0061), then deploy all functions right away (`purchase-validate` needs the 12-argument `pn_grant_entitlement`); `supabase/scripts/check_migrations.sh` in CI.
- Bridge commands (Lead/cmd, one per line). Mock purchases are never server-verified, so per-account verification
  itself needs a live server (sandbox purchase, then DELETE ACCOUNT: `Status` must lose `(verified)` and RESTORE must
  bring it back). In the Editor:
  ```
  call PawNoon.Runtime.ReviewDirector.Bounty wins 3
  call PawNoon.Runtime.ReviewDirector.Bounty status
  call PawNoon.Runtime.ReviewDirector.Show delete
  call PawNoon.Runtime.ReviewDirector.Bounty status
  call PawNoon.Runtime.ReviewDirector.SetAutoTap 240
  call PawNoon.Runtime.ReviewDirector.StartGhostDuel 7
  call PawNoon.Runtime.ReviewDirector.Status
  call PawNoon.Runtime.ReviewDirector.Show haptics
  ```
  Tap DELETE, then YES, DELETE on the card `Show delete` opens: the second `Bounty status` reads `bounty=0/3 streak=0`
  (it was 3/3 paid). The ghost duel's `Status` ends with `agree=yes` after the match; `SetNetMock on` + `StartPvp` shows the rival card with `CAT: PAWTRUTH`.
  `Show haptics` still opens the lab from the bridge; on a release device build the corner gesture no longer does.
