// Picks the right store check for a parsed purchase-validate request (see apple.ts / google.ts) and
// returns the normalised entitlement. No database access here: purchase-validate/index.ts writes it.
import { type AppleConfig, fetchTransaction, hasServerApi, verifyReceiptLegacy, verifySignedTransaction } from "./apple.ts";
import { type GoogleConfig, verifyPlayPurchase } from "./google.ts";
import type { PurchaseRequest } from "./receipt.ts";
import { type FetchFn, PurchaseError, type StoreEntitlement } from "./types.ts";

export interface PaymentsEnv {
  apple: AppleConfig;
  google: GoogleConfig;
  /** Accept sandbox / license-tester purchases. Default true: App Review buys in the sandbox on release builds. */
  allowSandbox: boolean;
}

/** Reads the function secrets (docs: Docs/PAYMENTS.md, supabase/functions/.env.example). */
export function paymentsEnvFrom(get: (k: string) => string | undefined): PaymentsEnv {
  const flag = (k: string, def: boolean) => {
    const v = get(k);
    return v === undefined || v === "" ? def : v === "true" || v === "1";
  };
  return {
    apple: {
      bundleId: get("APPLE_BUNDLE_ID") || "com.pawnoon.pixel",
      issuerId: get("APPLE_ISSUER_ID") || undefined,
      keyId: get("APPLE_KEY_ID") || undefined,
      privateKeyPem: get("APPLE_PRIVATE_KEY") || undefined,
      sharedSecret: get("APPLE_SHARED_SECRET") || undefined,
    },
    google: {
      packageName: get("GOOGLE_PACKAGE_NAME") || "com.pawnoon.pixel",
      serviceAccountJson: get("GOOGLE_SERVICE_ACCOUNT_JSON") || undefined,
      acknowledge: flag("PN_GOOGLE_ACKNOWLEDGE", true),
    },
    allowSandbox: flag("PN_IAP_ALLOW_SANDBOX", true),
  };
}

export async function validatePurchase(req: PurchaseRequest, env: PaymentsEnv, fetchFn: FetchFn, now = Date.now()): Promise<StoreEntitlement> {
  let ent: StoreEntitlement;
  if (req.store === "app_store") {
    if (req.signedTransaction) {
      ent = await verifySignedTransaction(req.signedTransaction, env.apple, req.productId, req.item, now);
    } else if (req.transactionId && hasServerApi(env.apple)) {
      ent = await fetchTransaction(req.transactionId, env.apple, req.productId, req.item, fetchFn, now);
    } else if (req.appReceipt) {
      ent = await verifyReceiptLegacy(req.appReceipt, env.apple, req.productId, req.item, fetchFn, req.transactionId);
    } else {
      throw new PurchaseError(503, "store_not_configured", "only a transactionId was sent and the App Store Server API key is not configured");
    }
    if (req.transactionId && ent.latestTransactionId !== req.transactionId && ent.originalTransactionId !== req.transactionId && ent.source !== "verify_receipt") {
      throw new PurchaseError(422, "invalid_receipt", "signed transaction does not match the transaction id");
    }
  } else {
    if (req.packageName && req.packageName !== env.google.packageName) throw new PurchaseError(422, "wrong_app", "purchase belongs to another app");
    ent = await verifyPlayPurchase(req.purchaseToken!, env.google, req.productId, req.item, fetchFn, now);
  }
  if (ent.environment === "sandbox" && !env.allowSandbox) throw new PurchaseError(422, "sandbox_not_allowed", "sandbox purchases are not accepted");
  return ent;
}
