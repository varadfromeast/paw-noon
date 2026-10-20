// POST /functions/v1/purchase-validate   (SPEC_V2 §6, M7)
//
// Request (the game sends Unity IAP's receipt as is; the explicit fields are alternatives):
//   {
//     "store": "app_store" | "google_play",
//     "productId": "com.pawnoon.pixel.peacemaker",
//     "restore": false,
//     "receipt": "<Unity IAP Product.receipt JSON string>",
//     // app_store alternatives: "signedTransaction" (StoreKit 2 JWS), "transactionId", "appReceipt" (base64)
//     // google_play alternative: "purchaseToken"
//   }
//
// 200:
//   {
//     "entitlements": [ { "item": "peacemaker", "productId": "com.pawnoon.pixel.peacemaker", "store": "app_store",
//                         "transactionId": "2000000123456789", "environment": "sandbox", "status": "valid" } ],
//     "outcome": "granted" | "already_owned" | "transferred" | "pending" | "refunded",
//     "profile": { "ownedItems": ["peacemaker"], "coins": 0, "equippedGun": "peacemaker" }
//   }
// A store answer of "pending" (Google slow card) or "refunded"/"revoked" is still a 200 with that status
// and no grant; the client keeps the purchase pending (pending) or finishes it without the item.
//
// Errors: 409 account_mismatch | owned_elsewhere (review pass 2: the purchase belongs to another account and this
// request does not prove the device owns it, see _shared/payments/binding.ts); 429 rate_limited;
// 400 bad_request | unknown_product | bad_receipt; 422 invalid_receipt | wrong_app | product_mismatch |
// not_in_receipt | not_found | fake_store | sandbox_not_allowed (permanent: the client finishes the store
// transaction without granting); 503 store_unavailable | store_not_configured (transient: the client keeps
// the transaction pending and retries on the next launch).
//
// Which Apple check runs is documented in _shared/payments/apple.ts and Docs/PAYMENTS.md. Idempotent: the
// same purchase validated again is "already_owned"; validated from another account it is "transferred".
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { parsePurchaseRequest, type PurchaseRequest } from "../_shared/payments/receipt.ts";
import { PurchaseError, type StoreEntitlement } from "../_shared/payments/types.ts";
import { paymentsEnvFrom, validatePurchase } from "../_shared/payments/validate.ts";
import { grantBound, type GrantRow } from "../_shared/payments/grant.ts";
import { takeRate } from "../_shared/rate.ts";

serve("purchase-validate", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 256 * 1024);
  await takeRate(db, user.id, "purchase_validate");   // each call may cost two store round trips
  const env = paymentsEnvFrom((k) => Deno.env.get(k));

  let parsed: PurchaseRequest;
  let ent: StoreEntitlement;
  try {
    parsed = parsePurchaseRequest(body);
    ent = await validatePurchase(parsed, env, fetch);
  } catch (e) {
    if (e instanceof PurchaseError) {
      console.warn(`[purchase-validate] ${user.id} ${String(body.store)} ${e.code}: ${e.message}`);
      throw new HttpError(e.status, e.code, e.message, e.extra);
    }
    throw e;
  }

  // review pass 2: a purchase only moves between accounts with device proof (payments/binding.ts, PAYMENTS.md §6)
  let bound: Awaited<ReturnType<typeof grantBound>>;
  try {
    bound = await grantBound(ent, parsed, user.id, env, fetch, async (allowTransfer, meta) => {
      const { data, error } = await db.rpc("pn_grant_entitlement", {
        p_user: user.id,
        p_store: ent.store,
        p_product: ent.productId,
        p_item: ent.item,
        p_txn: ent.transactionId,
        p_orig_txn: ent.originalTransactionId,
        p_env: ent.environment,
        p_status: ent.status,
        p_purchased_at: ent.purchasedAt,
        p_raw: { ...ent.raw, source: ent.source, latestTransactionId: ent.latestTransactionId, restore: parsed.restore,
                 accountBinding: meta.binding, deviceProof: meta.proof },
        // a fresh purchase equips the gun; a restore leaves the loadout as the player set it
        p_equip: !parsed.restore,
        p_allow_transfer: allowTransfer,
      });
      if (error) {
        console.error("[purchase-validate] grant failed", error);
        throw new HttpError(500, "db_error", "could not store the purchase");
      }
      return (Array.isArray(data) ? data[0] : data) as GrantRow | undefined;
    });
  } catch (e) {
    if (e instanceof PurchaseError) {
      console.warn(`[purchase-validate] ${user.id} ${ent.store} ${ent.transactionId} ${e.code}: ${e.message}`);
      throw new HttpError(e.status, e.code, e.message, e.extra);
    }
    throw e;
  }
  const row = bound.row;
  if (row.outcome === "transferred") {
    console.warn(`[purchase-validate] ${ent.store} ${ent.transactionId} moved from ${row.previous_owner} to ${user.id}`);
  }
  const outcome = ent.status === "valid" ? row.outcome : ent.status;
  return json({
    entitlements: [{
      item: ent.item,
      productId: ent.productId,
      store: ent.store,
      transactionId: ent.transactionId,
      environment: ent.environment,
      status: ent.status,
    }],
    outcome,
    profile: { ownedItems: row.owned_items ?? [], coins: row.coins ?? 0, equippedGun: row.equipped_gun ?? "standard" },
  });
});
