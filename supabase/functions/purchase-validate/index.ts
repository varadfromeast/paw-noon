// POST /functions/v1/purchase-validate   (STUB until M7: always 501 after validating the request)
//
// Final request (Unity IAP hands these over after a purchase or a restore):
//   {
//     "store": "app_store" | "google_play",
//     "productId": "com.pawnoon.pixel.peacemaker",
//     "restore": false,
//     // app_store: the StoreKit 2 signed transaction (JWS) from the receipt
//     "signedTransaction": "eyJhbGciOiJFUzI1NiIs...",
//     // google_play: the purchase token
//     "purchaseToken": "opaque-token"
//   }
//
// Final response 200:
//   {
//     "entitlements": [
//       { "item": "peacemaker", "productId": "com.pawnoon.pixel.peacemaker", "store": "app_store",
//         "transactionId": "2000000123456789", "environment": "production", "status": "valid" }
//     ],
//     "profile": { "ownedItems": ["peacemaker"], "coins": 0 }
//   }
//
// M7 implementation: verify with the App Store Server API (JWS chain + GET /inApps/v1/transactions)
// or the Play Developer API (purchases.products.get + acknowledge), upsert `purchases`
// (unique store+transaction_id, so a receipt can only ever grant one account), add the item to
// profiles.owned_items, return the entitlements. Secrets: APPLE_ISSUER_ID, APPLE_KEY_ID,
// APPLE_PRIVATE_KEY, APPLE_BUNDLE_ID, GOOGLE_SERVICE_ACCOUNT_JSON, GOOGLE_PACKAGE_NAME.
import { requireUser } from "../_shared/auth.ts";
import { GUN_PRODUCT } from "../_shared/duel/loadouts.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";

const PRODUCTS = new Set<string>(Object.values(GUN_PRODUCT));

serve("purchase-validate", async (req) => {
  await requireUser(req);
  const body = await readJson(req, 32 * 1024);
  const store = body.store;
  if (store !== "app_store" && store !== "google_play") throw new HttpError(400, "bad_request", 'store must be "app_store" or "google_play"');
  if (typeof body.productId !== "string" || !PRODUCTS.has(body.productId)) throw new HttpError(400, "unknown_product", "unknown productId");
  const proof = store === "app_store" ? body.signedTransaction : body.purchaseToken;
  if (typeof proof !== "string" || proof.length < 8) {
    throw new HttpError(400, "bad_request", store === "app_store" ? "signedTransaction is required" : "purchaseToken is required");
  }
  return json({ error: "not_implemented", message: "purchase validation ships in M7", entitlements: [] }, 501);
});
