# Payments: the Peacemaker (SPEC_V2 §6, M7)

One product for now:

| | |
|---|---|
| Product id (both stores) | `com.pawnoon.pixel.peacemaker` |
| Type | **Non-consumable** (App Store) / **one-time in-app product** (Google Play) |
| Price | ₹299 (India), $2.99 tier elsewhere |
| Bundle id / package | `com.pawnoon.pixel` |
| What it does | damage `[48,42,34,26,16,8,4,2]` (`duel_config.json` `damageValuePeacemaker`) in solo and casual PvP; ranked uses the standard curve for everyone. Its own holster, draw sound, gunshot and blue-white muzzle flash in every mode |

How a purchase flows: Unity IAP buys it → the game sends Unity's receipt to the `purchase-validate` Edge Function → the function checks it with Apple / Google → writes `purchases` + `profiles.owned_items` (and equips it) → the game unlocks it. The store transaction is only finished after the server answered, so a purchase made offline is checked on the next launch. RESTORE PURCHASES (required by Apple) asks the store to re-deliver and re-validates.

---

## 1. App Store Connect (Varad)

1. **Agreements, Tax and Banking**: accept the *Paid Apps* agreement and fill in bank + tax. Until it is *Active*, products never load (the shop shows "STORE UNAVAILABLE").
2. **Apps → Paw Noon → Monetization → In-App Purchases → +**
   - Type **Non-Consumable**, Reference name `Peacemaker`, Product ID `com.pawnoon.pixel.peacemaker` (cannot be changed or reused later).
   - Price: India ₹299 (pick the matching tier; let other countries follow it, around $2.99).
   - Localization (English): Display name `The Peacemaker`, Description `Fast draws hit harder. Blue fire, ivory grip. Solo and casual duels; ranked uses standard guns.`
   - Review screenshot: a screenshot of the GENERAL STORE panel (`ReviewDirector.Show shop` in the Editor), plus a review note: "Tap SHOP (top-left of the title) → BUY. Effect: higher damage for fast draws in solo and casual; ranked unchanged."
   - Save. The first IAP has to be submitted **together with an app version** (select it in the version's *In-App Purchases* section).
3. **Sandbox testers**: Users and Access → Sandbox → Test Accounts → + (use a fresh email, e.g. `varad+sandbox1@...`). On the iPhone: Settings → App Store → Sandbox Account → sign in. TestFlight builds always use the sandbox, so purchases there are free.
4. **In-App Purchase key** (server validation, App Store Server API): Users and Access → Integrations → **In-App Purchase** → Generate. Note the **Issuer ID** and the **Key ID** and download `SubscriptionKey_XXXXXXXXXX.p8` (downloadable once; keep it in the password manager).
5. Optional: *App-Specific Shared Secret* (App Information → Manage). Only used by the `verifyReceipt` fallback, which runs when the key in step 4 is not set.
6. Xcode (after Unity's iOS build): Signing & Capabilities → **+ In-App Purchase**. (Unity IAP adds StoreKit; the capability must still be on the app id.)

## 2. Google Play Console (Varad)

1. **Payments profile**: Settings → Payments profile, make sure the merchant account is set up (needed before any paid product).
2. **Upload a build first**: Play only lets you create products after a build with the billing permission exists. Upload the AAB (Unity IAP adds `com.android.vending.BILLING`) to **Testing → Internal testing**.
3. **Monetize → Products → In-app products → Create product**: Product ID `com.pawnoon.pixel.peacemaker`, name `The Peacemaker`, same description, price ₹299 (convert for other countries), **Activate**.
4. **License testers**: Settings (the gear on the console home) → License testing → add the Gmail accounts that test. Their purchases use test cards ("Test card, always approves" / "slow, declines") and cost nothing; the server sees them as `environment: sandbox`. Also add the same accounts to the internal testing track and install from its opt-in link.
5. **Service account** (server validation, Play Developer API):
   1. Google Cloud console (any project, e.g. `pawnoon`): APIs & Services → Library → enable **Google Play Android Developer API**.
   2. IAM & Admin → Service Accounts → Create (`iap-validator`), no roles needed. Keys → Add key → JSON → download.
   3. Play Console → **Users and permissions → Invite new users** → the service account's email → App permissions: Paw Noon → *View app information*, **View financial data, orders and cancellation survey responses**, **Manage orders and subscriptions** (needed to acknowledge) → Invite. It can take up to 24 h before the API answers 200 instead of 401/403 (the function then says `store_not_configured`).

## 3. Server secrets (Supabase, Mumbai project)

```bash
supabase secrets set \
  APPLE_BUNDLE_ID=com.pawnoon.pixel \
  APPLE_ISSUER_ID=<issuer id from step 1.4> \
  APPLE_KEY_ID=<key id from step 1.4> \
  APPLE_PRIVATE_KEY="$(cat SubscriptionKey_XXXXXXXXXX.p8)" \
  GOOGLE_PACKAGE_NAME=com.pawnoon.pixel \
  GOOGLE_SERVICE_ACCOUNT_JSON="$(cat pawnoon-iap-validator.json)"
# optional
supabase secrets set APPLE_SHARED_SECRET=<app-specific shared secret>
supabase db push                                   # migration 0007_m7_payments.sql
supabase functions deploy purchase-validate loadout-equip
```

| Variable | Needed for | Default |
|---|---|---|
| `APPLE_BUNDLE_ID` | every Apple check (receipts of other apps are refused) | `com.pawnoon.pixel` |
| `APPLE_ISSUER_ID`, `APPLE_KEY_ID`, `APPLE_PRIVATE_KEY` | App Store Server API lookups (primary Apple path) | unset → verifyReceipt fallback |
| `APPLE_SHARED_SECRET` | verifyReceipt password (fallback only, optional for non-consumables) | unset |
| `GOOGLE_PACKAGE_NAME` | Play API path + receipt check | `com.pawnoon.pixel` |
| `GOOGLE_SERVICE_ACCOUNT_JSON` | every Google check | unset → `503 store_not_configured` |
| `PN_IAP_ALLOW_SANDBOX` | accept sandbox / license-tester purchases. **Keep `true`**: App Review buys in the sandbox on the release build | `true` |
| `PN_GOOGLE_ACKNOWLEDGE` | the server acknowledges Google purchases (unacknowledged ones are refunded after 3 days) | `true` |

Local: copy the same keys into `supabase/functions/.env.local` (see `.env.example`).

## 4. How the server checks a purchase

`supabase/functions/_shared/payments/` (no npm, WebCrypto only):

- **Apple**, first match wins:
  1. `signedTransaction` (StoreKit 2 JWS, for a future Unity IAP 5 / native client): verified offline. The x5c chain must end in **Apple Root CA - G3** (pinned SHA-256 `63343abf…3e9179`), each certificate signed by the next, all valid now, Apple's WWDR / App Store OIDs present, then the ES256 signature.
  2. **App Store Server API v2** `GET /inApps/v1/transactions/{transactionId}` (production, then sandbox on 404) with an ES256 JWT from the In-App Purchase key; the answer's `signedTransactionInfo` is verified exactly as in 1. This is the path for today's Unity IAP 4.x (StoreKit 1 transaction id in the receipt).
  3. **verifyReceipt** fallback (deprecated by Apple, still served): the app receipt from the Unity receipt, production then sandbox on status 21007.
  Checks: bundle id, product id, `Non-Consumable`, not revoked (a revoked / refunded transaction answers `status: refunded` and removes the item).
- **Google**: OAuth token from the service account (RS256 JWT built by hand) → `purchases.products.get` → `purchaseState` 0 valid / 2 pending / 1 refunded, `purchaseType` 0 = test → sandbox; then `:acknowledge` when needed.
- **Database** (`pn_grant_entitlement`, migration 0007): one row per store purchase (Apple *original* transaction id, Google order id). Same purchase again → `already_owned` (idempotent). The same purchase from another account (reinstall as a new guest, a second phone without linking) → `transferred`: the item moves to the new account and the old one loses it, **only with device proof or a matching account binding** (0061, §6); otherwise `owned_elsewhere`. A fresh purchase equips the gun; a restore does not touch the loadout. `profiles.equipped_gun` can only hold `standard` or an owned item (check constraint).
- Tests: `node Tools/Parity/run_node_tests.ts` (or `deno test --allow-read supabase/functions/_shared/`) runs `payments.test.ts` against recorded store answers in `payments/fixtures/` (signed by a test CA in Apple's chain shape; `make_fixtures.py` rebuilds them).

## 5. Testing checklist (the M7 "done when")

Editor (no stores, no money):
```
call PawNoon.Runtime.ReviewDirector.Show shop
call PawNoon.Runtime.ReviewDirector.MockBuy peacemaker      # granted locally, solo only
call PawNoon.Runtime.ReviewDirector.Equip peacemaker
call PawNoon.Runtime.ReviewDirector.StartDuel
call PawNoon.Runtime.ReviewDirector.Status                  # ... gun=peacemaker ...
call PawNoon.Runtime.ReviewDirector.Equip standard
call PawNoon.Runtime.ReviewDirector.ResetGuns               # back to a fresh install
```
iPhone (TestFlight, sandbox account): SHOP → BUY → sandbox sheet → "CHECKING..." → OWNED / EQUIPPED; the holster turns black-and-ivory, the draw clicks four times, the flash is blue. Delete the app, reinstall, SHOP → RESTORE PURCHASES → OWNED. In Supabase: `select * from purchases` shows one `app_store` row, `environment = sandbox`; `profiles.owned_items = {peacemaker}`.
Android (internal testing, license tester): same, with "Test card, always approves"; then "slow test card, approves after a few minutes" → PAYMENT PENDING → OWNED later. The row is `google_play`, `environment = sandbox`; Play Console → Order management shows it acknowledged.

## 6. Known limits / later

- **Refunds** are noticed on the next validation or restore, not pushed. Next step: App Store Server Notifications V2 (`REFUND`) and Google Real-time developer notifications into a `purchase-notify` function calling `pn_grant_entitlement` with `refunded`.
- **Purchases are bound to the account (review pass 2, fixed).** Before, the App Store Server API path trusted the transaction id the client named, and validating a purchase from another account moved it there, so anybody who learned (or guessed: ids are dense) another buyer's transaction id could take their gun. Now:
  - The client binds every purchase to the signed-in user (Unity IAP 4.x): Apple `IAppleExtensions.SetApplicationUsername(<user id>)` (a UUID, which StoreKit copies into the transaction's `appAccountToken`; verifyReceipt shows it as `app_account_token`), Google `IGooglePlayConfiguration.SetObfuscatedAccountId(sha256hex(<user id>))` (`obfuscatedExternalAccountId`). `PurchaseBinding` (C#) and `payments/binding.ts` compute the same tokens (shared test vector).
  - A purchase bound to the caller is the caller's. Anything else may only be granted to or moved to the caller with **device proof**: a StoreKit 2 JWS, an app receipt that Apple's verifyReceipt confirms holds the purchase, or a Google purchase token (a long secret only the buying device gets). A bare transaction id is not proof; the server-API path asks verifyReceipt with the receipt the client sent only when somebody else already owns the purchase.
  - Bound to another account without proof: `409 account_mismatch`. Owned by another account without proof: `409 owned_elsewhere` (nothing moves; `pn_grant_entitlement(..., p_allow_transfer)`). The client finishes the store transaction and says OWNED BY ANOTHER ACCOUNT; the real owner restores it from their phone.
  - Restoring onto a new guest after a reinstall still works (the device's receipt is the proof). Purchases made before the binding are unbound and behave like that too.
  - Google: some Unity IAP 4.x versions only read the obfuscated account id at initialisation; it is set at init and again before each purchase. On Google the token is the proof anyway, so a stale id only shows up as `accountBinding: "mismatch"` in `purchases.raw`.
  - `purchase-validate` is rate limited (30 calls / 10 min per user, `RATE.purchase_validate`).
- Coins (`coins_small/medium`, consumables) are not in the shop yet (SPEC_V2 §10 Q2); the receipt path already ignores and finishes unknown products.
