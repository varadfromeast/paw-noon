# Accounts (M6): guest, Sign in with Apple / Google, profile, delete

This is SPEC_V2 §5 (and the profile card from §7). It builds on the M4 online client (`Docs/ONLINE_CLIENT.md`) and
the M3 backend (`supabase/README.md`). Solo play never waits on any of it. With no server configured the game stays
fully offline, and in the Editor every screen runs against a mock.

## What the player sees

```
first solo match ends -> result screen -> 1.4 s later, once per install:
  KEEP YOUR BOUNTY                      (panel_wood, bottom thumb zone)
    YOUR BEST: 0.198 s
    [  SIGN IN WITH APPLE  ]            iOS only (and the Editor)
    [  G SIGN IN WITH GOOGLE ]
    [ LATER ]                           "shown" counts as spent, whatever is tapped
Title: [cat] [gear] top right
  cat -> WANTED poster (profile card, panel_paper)
           STRANGER #0042 / WHISKERS   GUEST
           RATING 1234  BEST 0.198 s  DUELS 12  WINS 7 (58%)  LIGHTNING 3
           [EDIT NAME] -> pixel keypad (A-Z, SPACE, DEL, 0-9 in their own block), 3-14 chars, server-validated
           [ACCOUNT]   -> the account sheet
  gear -> Settings -> [ACCOUNT] -> ACCOUNT sheet
           name, GUEST ON THIS PHONE | LINKED WITH APPLE, the link buttons while a guest,
           [DELETE ACCOUNT] -> DELETE ACCOUNT? [KEEP IT][DELETE] -> LAST CHANCE [CANCEL][YES, DELETE]
                               -> account-delete -> local state cleared -> ACCOUNT DELETED -> title
```

Link results on the sheet's status line: LINKED! YOUR BOUNTY IS SAFE. / NO SIGNAL. TRY AGAIN. / COULD NOT SIGN IN.
TRY AGAIN. / NOT AVAILABLE ON THIS PHONE / LINKING IS OFF ON THE SERVER. Cancelling the Apple / Google sheet
simply returns to the buttons. If the Apple ID or Google account already belongs to another player (the same person
on a new phone), a card asks ALREADY WANTED, THAT ACCOUNT HAS A BOUNTY: [STAY GUEST] / [SWITCH].

Guest name: the server makes `Stranger #1234` (0001_init.sql), and the client shows it upper case. Before the first
profile answer the client shows `STRANGER #<n>` from a random number kept in PlayerPrefs `pn_guest_no`.

Previews without Unity: `python3 Tools/ArtGen/accounts/mock_account_ui.py /tmp/m6` renders every card with the real
sprites and fonts and the same layout maths.

## How linking works (exact endpoints)

| Step | Call |
|---|---|
| Guest (M4) | `POST /auth/v1/signup` `{}` (anon key), which gives `user.is_anonymous = true` |
| Refresh | `POST /auth/v1/token?grant_type=refresh_token` `{refresh_token}` |
| Native token | iOS Apple: `ASAuthorizationAppleIDProvider` with `nonce = sha256(raw)`. iOS Google: `ASWebAuthenticationSession` to `accounts.google.com/o/oauth2/v2/auth` (iOS client, PKCE S256, `nonce = sha256(raw)`, scope `openid email`), then the code is exchanged at `oauth2.googleapis.com/token`. Android Google: Credential Manager `GetSignInWithGoogleOption(serverClientId = web client).setNonce(sha256(raw))` |
| **Link** | `POST /auth/v1/token?grant_type=id_token` with headers `apikey: <anon>` and `Authorization: Bearer <guest access token>`, body `{"provider":"apple"\|"google","id_token":"…","nonce":"<raw>","link_identity":true}`. This is what supabase-js `auth.linkIdentity({ provider, token, nonce })` sends. It needs Auth > **Manual linking** on |
| Switch to the existing account | The same grant **without** the bearer and without `link_identity` (= `signInWithIdToken`). The client reuses the session the link attempt already returned when there is one |

The client checks the answer (`LinkAnswer.Classify`, unit-tested) before storing anything:
- 200 with the **same user id** means linked. The session is stored and `is_anonymous` is now false.
- 422 `identity_already_exists`, or a 200 for an **older, different** user, means the identity is taken. The client
  shows the switch prompt.
- A 200 for a **different user created in the last 120 s** means the server ignored `link_identity` (an Auth version
  that predates id-token linking, or linking is off) and made a fresh account. The client does **not** keep that
  session and shows LINKING IS OFF ON THE SERVER.

**Fallback if id-token linking is unavailable on the hosted Auth version:** OAuth linking, which supabase-js
`linkIdentity({ provider })` uses for redirects. Call `GET /auth/v1/user/identities/authorize?provider=apple&redirect_to=com.pawnoon.pixel://auth-callback&skip_http_redirect=true`
with the guest bearer. It answers `{ "url": … }`. Open that URL in `ASWebAuthenticationSession` (iOS) or a Custom Tab
(Android) with callback scheme `com.pawnoon.pixel`. The redirect carries `#access_token=…&refresh_token=…` for the
same user id. `config.toml` already allows that redirect (`additional_redirect_urls`). It needs the Apple **Services
ID + secret** and the Google **web** client secret (both below). It is not implemented, because the id-token path is
the current Supabase API. Choose the fallback only if the smoke test below fails with LINKING IS OFF ON THE SERVER
while Manual linking is on.

### Guest stats after linking: nothing to migrate

Linking adds a row to `auth.identities` for the **same** `auth.users` row and flips `is_anonymous` to false. Every
game table (`profiles`, `matches`, `recordings`, `ratings`, `purchases`, `notifications`) is keyed by that user id, so
the bounty, MMR, ghosts and match history stay without any copy step. Three things back this up:
1. The client refuses any link answer whose user id differs from the guest's (above).
2. `SupabaseAuth` logs `linked apple to <id> (same user id, guest stats kept)`.
3. The **after-link hook** `on_auth_user_linked` (0060_m6_accounts.sql) fires on `is_anonymous true -> false` and
   only stamps `profiles.linked_at`. That stamp is for analytics and to make sure the guest cleanup never touches a
   linked player. A real "after link" migration would go in this trigger if one were ever needed (for example,
   merging two accounts), but none is needed today.

Check it on the real project after the first device link:
```sql
select id, is_anonymous from auth.users where id = '<uid from the log>';            -- false
select provider from auth.identities where user_id = '<uid>';                        -- apple
select display_name, matches_played, wins, linked_at from public.profiles where id = '<uid>';  -- unchanged + stamped
```

Switching to another existing account leaves the guest behind on the server (an anonymous user that is never used
again). The cleanup SQL is in the migration's footer. Run it by hand or from pg_cron when it matters.

## Backend (M6)

- `profile-read` returns the profile card: name, cat, guest or linked, providers, the server-validated PvP stats
  (`matches_played`, `wins`, `best_reaction_ns`, `lightning_count`, which match-submit adds to after every completed
  match), the current season's MMR, coins and owned items.
- `profile-update {displayName?, character?}` is the **only** way to rename. 0060 revokes the client's direct
  `UPDATE (display_name)` and adds a NOT VALID shape check. The rules are in `functions/_shared/display_name.ts`,
  mirrored in C# `PawNoon.Core.DisplayName`, and both run the vectors in `Tools/Parity/display_names.json`:
  normalise (trim, collapse spaces, upper-case ASCII), then 3–14 characters of A–Z 0–9 space, then not reserved
  (house ghost names, and names starting STRANGER / ADMIN / MODERATOR / PAWNOON / PAW NOON / SUPPORT / SYSTEM /
  HOUSE GHOST). A refusal is `400 bad_display_name` with `reason`. A new name is also written onto the player's
  pooled ghost recordings.
- `account-delete` (M3) is unchanged. The client sends `{"confirm":"DELETE"}` only after the two-step confirm.
- Deploy: `supabase db push` (0060) and `supabase functions deploy profile-read profile-update`.

## Console setup (Varad)

Placeholders: `<ref>` is the Supabase project ref, `<team>` is the Apple Team ID. The bundle ID and the Android
package are both `com.pawnoon.pixel`.

### Apple Developer (developer.apple.com > Certificates, Identifiers & Profiles)

1. **App ID capability.** Identifiers > `com.pawnoon.pixel` > tick **Sign In with Apple** > Edit > "Enable as a
   primary App ID" > Save. Regenerate or refresh the provisioning profiles. With automatic signing, Xcode does this
   on the next build.
   The Xcode side is automatic: `Code/Editor/PawSignInPostBuild.cs` adds the capability (entitlement
   `com.apple.developer.applesignin = [Default]`) and links `AuthenticationServices.framework` on every iOS build.
2. **Services ID** (only for the web/OAuth fallback and for Apple sign-in on Android or the web later): Identifiers >
   + > Services IDs > identifier `com.pawnoon.pixel.signin`, description "Paw Noon Sign in" > Continue > Register.
   Open it, tick Sign In with Apple > Configure: primary App ID `com.pawnoon.pixel`, Domains `<ref>.supabase.co`,
   Return URLs `https://<ref>.supabase.co/auth/v1/callback` > Save.
3. **Key:** Keys > + > name "Paw Noon SIWA", tick **Sign in with Apple** > Configure > primary App ID
   `com.pawnoon.pixel` > Save > Continue > Register > **Download the .p8 once**. Note the **Key ID** and the
   **Team ID**. Keep the .p8 in the team password manager. It never goes in the repo or the app.
4. **Client secret** (OAuth fallback only): a JWT signed with the .p8 (iss = Team ID, sub = Services ID, aud =
   `https://appleid.apple.com`, max 6 months). Supabase's Apple provider page links a generator. **It expires every 6
   months**, so put a calendar reminder to rotate it.

Native iOS sign-in (the path the game uses) needs only step 1 plus the bundle ID in Supabase (below). Steps 2–4 are
for the fallback and for the token revocation open question.

### Google Cloud Console (console.cloud.google.com, one project for Paw Noon)

1. **OAuth consent screen** (Google Auth Platform > Branding / Audience): External; app name "Paw Noon", support
   email, logo optional; scopes `openid` and `.../auth/userinfo.email` only (no sensitive scopes, so no verification
   review). Publish to **In production**. In "Testing" only listed test users can sign in.
2. **Clients > Create client**, three kinds:
   - **Web application** "Paw Noon Supabase": Authorized redirect URI `https://<ref>.supabase.co/auth/v1/callback`.
     This gives a **client ID + secret**. The ID goes into `auth_config.json` `googleWebClientId` (Android asks for
     tokens for this audience) and Supabase. The secret goes into Supabase only.
   - **iOS** "Paw Noon iOS": bundle ID `com.pawnoon.pixel` (App Store ID / Team ID optional). This gives a **client
     ID** (no secret) for `auth_config.json` `googleIosClientId`. No Info.plist URL scheme is needed:
     ASWebAuthenticationSession catches `com.googleusercontent.apps.<id>:/oauthredirect` by itself.
   - **Android**, **one client per signing certificate**, all with package `com.pawnoon.pixel`:
     - debug (Unity's debug key):
       `keytool -list -v -keystore ~/.android/debug.keystore -alias androiddebugkey -storepass android -keypass android`
       (on the Mac that built the APK) → SHA-1
     - your upload/release keystore: `keytool -list -v -keystore <release.keystore> -alias <alias>` → SHA-1
     - **Play App Signing**: Play Console > Paw Noon > Test and release > Setup > App integrity > App signing key
       certificate → SHA-1 (Play re-signs the app, so this one is required for store builds)
     Android clients have no secret and are not referenced in code. Credential Manager matches package + SHA-1.
3. Nothing to download (no google-services.json, no Firebase).

### Supabase Dashboard (Authentication)

- **Sign In / Providers > Anonymous sign-ins**: on. **Manual linking** ("Allow manual linking"): **on**. Linking
  fails without it. **Email**: sign-ups off.
- **Apple**: enabled. **Client IDs**: `com.pawnoon.pixel,com.pawnoon.pixel.signin` (the bundle ID is the audience
  of native iOS tokens; the Services ID is only for the OAuth fallback). Secret Key (for OAuth): the JWT from
  Apple step 4. Leave "Skip nonce check" off: the game always sends a nonce.
- **Google**: enabled. **Client IDs**: `<web client id>,<ios client id>` (web first: Android tokens have the web
  audience, iOS tokens the iOS audience). **Client Secret**: the web client's secret. "Skip nonce check" can be off
  in production because both platforms send a nonce. The local `config.toml` keeps `skip_nonce_check = true` for
  Google so that hand-made test tokens work.
- **URL Configuration > Redirect URLs**: add `com.pawnoon.pixel://auth-callback` (OAuth fallback only).
- Local stack: `SUPABASE_AUTH_EXTERNAL_APPLE_CLIENT_ID`, `..._APPLE_SECRET`, `..._GOOGLE_CLIENT_ID`,
  `..._GOOGLE_SECRET` in the shell before `supabase start` (see `config.toml`).

### The game

`Assets/PawNoon/Resources/Data/auth_config.json`:
```json
{ "googleWebClientId": "<web client id>.apps.googleusercontent.com",
  "googleIosClientId": "<ios client id>.apps.googleusercontent.com",
  "apple": true, "google": true }
```
Client IDs are public. Never put a secret or the .p8 here. While a value still contains `YOUR-`, that button is hidden
on devices.

### Android build (Gradle): not added yet, on purpose

The C# calls Credential Manager through `AndroidJavaObject`, so the game compiles and runs without these libraries.
The Google button simply stays hidden (and logs `Credential Manager / googleid not in this build`). To turn it on:

1. Player Settings > Android > Publishing Settings > Build: tick **Custom Main Gradle Template**. Unity creates
   `Assets/Plugins/Android/mainTemplate.gradle`.
2. In its `dependencies {` block, before `**DEPS**`, add exactly:
   ```gradle
       implementation 'androidx.credentials:credentials:1.5.0'
       implementation 'androidx.credentials:credentials-play-services-auth:1.5.0'
       implementation 'com.google.android.libraries.identity.googleid:googleid:1.1.1'
   ```
   (AndroidX is already on in Unity 6's `gradleTemplate.properties`: `android.useAndroidX=true`. Min SDK 26 is fine.)
3. If **Minify** is on (Release), tick **Custom Proguard File** and add to `Assets/Plugins/Android/proguard-user.txt`:
   ```
   -if class androidx.credentials.CredentialManager
   -keep class androidx.credentials.playservices.** { *; }
   -keep class androidx.credentials.** { *; }
   -keep class com.google.android.libraries.identity.googleid.** { *; }
   ```
   (the last two because the classes are reached by JNI reflection, which R8 cannot see).

## Device smoke test (M6 "done when")

1. Hosted project with migrations through 0060 and functions deployed. Providers set as above. `net_config.json`
   and `auth_config.json` filled in.
2. Fresh install, then play one solo match. KEEP YOUR BOUNTY appears once. Tap SIGN IN WITH APPLE (iPhone) or
   SIGN IN WITH GOOGLE (Android). It shows LINKED! YOUR BOUNTY IS SAFE. The log line has the same user id as before.
   `Status` shows `auth=apple`.
3. Profile card: LINKED: APPLE, and the stats from earlier PvP matches are still there. EDIT NAME `billy the cat`
   is saved as BILLY THE CAT, while `stranger 1` is refused with THAT NAME IS TAKEN.
4. Delete the app and reinstall (or use a second phone). Open the account sheet, then Apple or Google. ALREADY WANTED
   appears. Tap SWITCH and you get the same name and stats back.
5. Settings > ACCOUNT > DELETE ACCOUNT > DELETE > YES, DELETE. ACCOUNT DELETED appears. On the title, BEST shows --
   and the profile shows a new STRANGER. In the database the user and all its rows are gone.

## Editor and bridge (no Apple, Google or server)

In the Editor with `net_config.json` not set up (or `SetNetMock on`), accounts are **mocked**. The native sheet
waits 0.7 s and returns a fake token. The link, the name change and the deletion are simulated locally, and
`Status` says `accounts=mock`.

```
call PawNoon.Runtime.ReviewDirector.Show bounty        # KEEP YOUR BOUNTY over the title (does not spend the once-flag)
call PawNoon.Runtime.ReviewDirector.Show profile       # WANTED poster
call PawNoon.Runtime.ReviewDirector.Show account       # Settings > ACCOUNT
call PawNoon.Runtime.ReviewDirector.Show keypad        # name keypad over the profile
call PawNoon.Runtime.ReviewDirector.Show delete        # first delete confirm
call PawNoon.Runtime.ReviewDirector.Show switch        # ALREADY WANTED prompt
call PawNoon.Runtime.ReviewDirector.MockLink apple     # simulate a successful link (through the open sheet's UI if one is up)
call PawNoon.Runtime.ReviewDirector.MockLink google
call PawNoon.Runtime.ReviewDirector.MockLink taken     # the next link finds the account in use -> switch prompt
call PawNoon.Runtime.ReviewDirector.MockLink cancel    # the next native sheet is cancelled (or: fail)
call PawNoon.Runtime.ReviewDirector.MockLink reset     # unlinked guest again, KEEP YOUR BOUNTY re-armed
call PawNoon.Runtime.ReviewDirector.Status             # ... auth=none|guest|stale|apple|google|mock-apple|mock-google ...
                                                       #     account=closed|profile|sheet|bounty|keypad|confirm:<ask>
                                                       #     accounts=mock|live|off bounty=fresh|offered[(due)] name="..." last=<event>
```

To see the real once-only flow: `MockLink reset`, then `StartDuel` + `SetAutoTap 230`. When the match ends,
KEEP YOUR BOUNTY appears after 1.4 s and `Status` shows `bounty=offered`.

## Stored on the device

PlayerPrefs: `pn_auth_access|refresh|expires|user` (M4) + `pn_auth_anon`, `pn_auth_providers`, `pn_auth_mock`,
`pn_profile_cache` (last profile JSON), `pn_guest_no`, `pn_bounty_offered`, `pn_mock_name` (mock only).
DELETE ACCOUNT clears all of these, plus the submit queue and the all-time best, the daily bounty state
(`pawnoon.bounty`) and the shop's server verification of owned guns (review pass 2: guns stay owned for solo; PvP
uses the standard gun until RESTORE verifies them for the new account). SWITCH (to an already linked account) resets
the bounty state and the verification the same way. It keeps the device settings (sound, haptics, motion,
difficulty) and pending store receipts (`pn_iap_pending`).

Not done yet (review pass 2, deferred): Sign in with Apple token revocation on delete (Apple asks apps that offer
SIWA to call `https://appleid.apple.com/auth/revoke`). Plan: DELETE asks for one more Apple sheet when the account is
linked to Apple, sends its `authCode` to `account-delete`, which exchanges it (client secret = ES256 JWT from the SIWA
key) and revokes the refresh token before deleting the user.

## Files

| File | What |
|---|---|
| `Code/Core/Accounts.cs` | `DisplayName` (rules, guest names), `AuthUserInfo`, `AuthError`, `LinkOutcome`, `LinkAnswer`, `ProfileInfo`, `BountyGate` + `IFlagStore` |
| `Code/Runtime/Net/SupabaseAuth.cs` | + `IsLinked`, `LinkedProvider`, `LinkApple` / `LinkGoogle` / `Link`, `SignInWithIdToken`, `SimulateLink`, `StatusWord`; a refused refresh for a **linked** player answers `relink` (never silently a new guest) |
| `Code/Runtime/Net/NativeSignIn.cs` | `INativeSignIn` seam, `Nonce`, `NativeSignInHost` (GameObject `PawSignIn`, UnitySendMessage target), iOS / Android / mock / unavailable implementations |
| `Plugins/iOS/PawSignIn.mm` | AuthenticationServices bridge (ARC; frameworks set in its .meta) |
| `Code/Editor/PawSignInPostBuild.cs` | iOS capability + framework after build |
| `Code/Runtime/Net/AccountDirector.cs` | the flows above |
| `Code/Runtime/Net/AccountScreens.cs` | `AccountSheet`, `ProfileCard`, `NameKeypad`, `ConfirmCard`, `BrandButton` |
| `Code/Runtime/Net/AuthConfig.cs`, `Resources/Data/auth_config.json` | client IDs |
| `Resources/Art/UI/btn_black(_pressed)`, `btn_white(_pressed)`, `ico_apple`, `ico_google`, `ico_profile` | `Tools/ArtGen/accounts/gen_account_ui.py` |
| hooks | `PawGame` (Accounts field, Update routing, solo match end, ShowTitle), `TitleScreen` (cat button left of the gear), `SettingsScreen` (ACCOUNT button), `OnlineDirector` (`Auth` / `Api` getters, `auth=` word), `ReviewDirector` |
| tests | `Tests/EditMode/AccountTests.cs`, `supabase/functions/_shared/account.test.ts`, shared `Tools/Parity/display_names.json` |
| backend | `functions/profile-read`, `functions/profile-update`, `_shared/display_name.ts`, `_shared/profile.ts`, schemas, `migrations/0060_m6_accounts.sql` |

## Open questions

1. **Apple token revocation on delete.** Apple asks apps that use Sign in with Apple to revoke the user's tokens when
   the account is deleted (`POST https://appleid.apple.com/auth/revoke`). That needs a refresh token, which comes from
   exchanging the one-time `authorizationCode` with the client secret (Apple steps 3–4). The iOS plugin already
   returns `authCode`, but nothing stores or uses it yet. Proposal: send it to a small `apple-token` function right
   after linking, store the refresh token server-side, and revoke it in `account-delete`.
2. **Button styling and App Review.** The Apple and Google buttons are pixel-art versions that follow the brand rules
   (a black button with the white Apple logo and "SIGN IN WITH APPLE"; a light button with the four-colour G), drawn
   in the game's bitmap font. Apple's HIG allows custom Sign in with Apple buttons that keep the logo, the wording and
   the colours, but App Review sometimes questions them. The fallback is `ASAuthorizationAppleIDButton` in an overlay.
3. **O and 0** look the same in `font_medium` and `font_small`, so BOB and B0B read alike on cards and opponent plates.
   The keypad keeps the digits in their own brass block. A slashed zero glyph would fix it everywhere, but that is a
   font change (not this lane).
4. **Rename rate.** There is no cooldown on `profile-update` names. Should a player be able to rename freely?
5. **Profanity.** There is only a reserved-name list, no word filter. Do we want a small blocklist before launch?
6. **Guest left behind on switch** (see above). Should a guest with zero matches be deleted automatically before
   switching?
