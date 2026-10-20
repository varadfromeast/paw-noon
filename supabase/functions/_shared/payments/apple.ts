// Apple validation (SPEC_V2 §6). Three ways in, tried in this order by validate.ts:
//
//  1. `signedTransaction`: a StoreKit 2 JWS sent by the client (Unity IAP 5 / native StoreKit 2). Verified
//     offline against Apple Root CA - G3 (jws.ts). Needs no secrets.
//  2. App Store Server API v2: GET /inApps/v1/transactions/{transactionId} with an ES256 JWT signed by the
//     In-App Purchase key (APPLE_ISSUER_ID, APPLE_KEY_ID, APPLE_PRIVATE_KEY). The answer's
//     signedTransactionInfo is verified with the same JWS chain check. Production first, sandbox on 404.
//     This is the primary path for Unity IAP 4.x, which hands us the StoreKit 1 transaction id.
//  3. verifyReceipt (deprecated by Apple in 2023 but still served): the base64 app receipt from the Unity
//     receipt, POSTed to buy.itunes.apple.com (sandbox on status 21007), with APPLE_SHARED_SECRET as the
//     password when set. Fallback when the API key is not configured.
import { ab, bytesToB64url, pemToDer, utf8 } from "./der.ts";
import { JwsError, verifyAppleJws } from "./jws.ts";
import { type FetchFn, iso, PurchaseError, type StoreEntitlement } from "./types.ts";

export interface AppleConfig {
  bundleId: string;
  issuerId?: string;
  keyId?: string;
  /** Contents of the .p8 In-App Purchase key (PEM; literal "\n" sequences are accepted). */
  privateKeyPem?: string;
  /** App-specific shared secret (verifyReceipt password), optional for non-consumables. */
  sharedSecret?: string;
  /** Test override for the pinned root (lower-case hex SHA-256). */
  rootSha256?: string[];
  /** Test override: certificates without Apple's OIDs. */
  requireAppleOids?: boolean;
}

export const APPLE_API = {
  production: "https://api.storekit.itunes.apple.com",
  sandbox: "https://api.storekit-sandbox.itunes.apple.com",
};
export const VERIFY_RECEIPT = {
  production: "https://buy.itunes.apple.com/verifyReceipt",
  sandbox: "https://sandbox.itunes.apple.com/verifyReceipt",
};

export function hasServerApi(cfg: AppleConfig): boolean {
  return !!(cfg.issuerId && cfg.keyId && cfg.privateKeyPem);
}

// ---------------------------------------------------------------- normalising

/** JWSTransactionDecodedPayload -> entitlement. Checks app and product. */
export function entitlementFromAppleTransaction(
  p: Record<string, unknown>,
  expectedProductId: string,
  item: string,
  bundleId: string,
  source: "jws" | "server_api",
): StoreEntitlement {
  if (p.bundleId !== bundleId) throw new PurchaseError(422, "wrong_app", "transaction belongs to another app");
  if (p.productId !== expectedProductId) throw new PurchaseError(422, "product_mismatch", "transaction is for another product");
  if (typeof p.type === "string" && p.type !== "Non-Consumable") throw new PurchaseError(422, "product_mismatch", `unexpected type ${p.type}`);
  const env = p.environment === "Production" ? "production" : p.environment === "Sandbox" ? "sandbox" : null;
  if (!env) throw new PurchaseError(422, "invalid_receipt", `unsupported environment ${String(p.environment)}`);
  const txn = str(p.transactionId);
  const orig = str(p.originalTransactionId) ?? txn;
  if (!txn || !orig) throw new PurchaseError(422, "invalid_receipt", "transaction without an id");
  return {
    store: "app_store",
    productId: expectedProductId,
    item,
    transactionId: orig,
    latestTransactionId: txn,
    originalTransactionId: orig,
    environment: env,
    status: p.revocationDate != null ? "refunded" : "valid",
    purchasedAt: iso(p.originalPurchaseDate ?? p.purchaseDate),
    source,
    raw: pick(p, ["transactionId", "originalTransactionId", "productId", "purchaseDate", "originalPurchaseDate", "signedDate",
      "type", "inAppOwnershipType", "revocationDate", "revocationReason", "environment", "storefront", "transactionReason"]),
  };
}

/** verifyReceipt answer -> entitlement for `expectedProductId` (prefers `transactionIdHint`). */
export function entitlementFromVerifyReceipt(
  body: Record<string, unknown>,
  expectedProductId: string,
  item: string,
  bundleId: string,
  transactionIdHint?: string | null,
): StoreEntitlement {
  const receipt = (body.receipt ?? {}) as Record<string, unknown>;
  if (receipt.bundle_id !== bundleId) throw new PurchaseError(422, "wrong_app", "receipt belongs to another app");
  const all = [
    ...(Array.isArray(receipt.in_app) ? receipt.in_app : []),
    ...(Array.isArray(body.latest_receipt_info) ? body.latest_receipt_info : []),
  ] as Record<string, unknown>[];
  const mine = all.filter((x) => x && x.product_id === expectedProductId);
  if (mine.length === 0) throw new PurchaseError(422, "not_in_receipt", "the receipt does not contain this product");
  const hit = mine.find((x) => transactionIdHint && x.transaction_id === transactionIdHint) ??
    mine.find((x) => x.cancellation_date_ms == null) ?? mine[0];
  const txn = str(hit.transaction_id);
  const orig = str(hit.original_transaction_id) ?? txn;
  if (!txn || !orig) throw new PurchaseError(422, "invalid_receipt", "receipt entry without an id");
  return {
    store: "app_store",
    productId: expectedProductId,
    item,
    transactionId: orig,
    latestTransactionId: txn,
    originalTransactionId: orig,
    environment: body.environment === "Sandbox" ? "sandbox" : "production",
    status: hit.cancellation_date_ms != null || hit.cancellation_date != null ? "refunded" : "valid",
    purchasedAt: iso(hit.original_purchase_date_ms ?? hit.purchase_date_ms),
    source: "verify_receipt",
    raw: pick(hit, ["transaction_id", "original_transaction_id", "product_id", "purchase_date_ms", "original_purchase_date_ms",
      "cancellation_date_ms", "cancellation_reason", "in_app_ownership_type"]),
  };
}

// ---------------------------------------------------------------- 1. signed transaction

export async function verifySignedTransaction(
  jws: string,
  cfg: AppleConfig,
  productId: string,
  item: string,
  now?: number,
  source: "jws" | "server_api" = "jws",
): Promise<StoreEntitlement> {
  let payload: Record<string, unknown>;
  try {
    payload = await verifyAppleJws(jws, { rootSha256: cfg.rootSha256, now, requireAppleOids: cfg.requireAppleOids });
  } catch (e) {
    if (e instanceof JwsError) throw new PurchaseError(422, "invalid_receipt", `signed transaction rejected: ${e.reason}`, { detail: e.reason });
    throw e;
  }
  return entitlementFromAppleTransaction(payload, productId, item, cfg.bundleId, source);
}

// ---------------------------------------------------------------- 2. App Store Server API

/** ES256 JWT for the App Store Server API (valid 20 minutes; Apple allows at most 60). */
export async function appStoreApiJwt(cfg: AppleConfig, nowSec: number): Promise<string> {
  if (!hasServerApi(cfg)) throw new PurchaseError(503, "store_not_configured", "App Store Server API key is not configured");
  const pem = cfg.privateKeyPem!.replace(/\\n/g, "\n");
  let key: CryptoKey;
  try {
    key = await crypto.subtle.importKey("pkcs8", ab(pemToDer(pem)), { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
  } catch {
    throw new PurchaseError(503, "store_not_configured", "APPLE_PRIVATE_KEY is not a P-256 PKCS#8 key");
  }
  const header = { alg: "ES256", kid: cfg.keyId, typ: "JWT" };
  const claims = { iss: cfg.issuerId, iat: nowSec, exp: nowSec + 1200, aud: "appstoreconnect-v1", bid: cfg.bundleId };
  const signing = `${bytesToB64url(utf8(JSON.stringify(header)))}.${bytesToB64url(utf8(JSON.stringify(claims)))}`;
  const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, ab(utf8(signing))));
  return `${signing}.${bytesToB64url(sig)}`;
}

/** Looks the transaction up (production, then sandbox on 404) and verifies Apple's signed answer. */
export async function fetchTransaction(
  transactionId: string,
  cfg: AppleConfig,
  productId: string,
  item: string,
  fetchFn: FetchFn,
  now = Date.now(),
): Promise<StoreEntitlement> {
  if (!/^\d{1,32}$/.test(transactionId)) throw new PurchaseError(400, "bad_request", "transactionId must be numeric");
  const token = await appStoreApiJwt(cfg, Math.floor(now / 1000));
  for (const env of ["production", "sandbox"] as const) {
    let res: Response;
    try {
      res = await fetchFn(`${APPLE_API[env]}/inApps/v1/transactions/${transactionId}`, {
        headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
      });
    } catch (e) {
      throw new PurchaseError(503, "store_unavailable", `App Store unreachable: ${String(e)}`);
    }
    if (res.status === 404) {
      await res.body?.cancel();
      continue;
    }
    if (res.status === 401) throw new PurchaseError(503, "store_not_configured", "App Store Server API refused the key (401)");
    if (!res.ok) throw new PurchaseError(503, "store_unavailable", `App Store Server API ${res.status}`);
    const body = await res.json() as Record<string, unknown>;
    if (typeof body.signedTransactionInfo !== "string") throw new PurchaseError(503, "store_unavailable", "App Store answer without signedTransactionInfo");
    return await verifySignedTransaction(body.signedTransactionInfo, cfg, productId, item, now, "server_api");
  }
  throw new PurchaseError(422, "not_found", "the App Store does not know this transaction");
}

// ---------------------------------------------------------------- 3. verifyReceipt (fallback)

export async function verifyReceiptLegacy(
  receiptB64: string,
  cfg: AppleConfig,
  productId: string,
  item: string,
  fetchFn: FetchFn,
  transactionIdHint?: string | null,
): Promise<StoreEntitlement> {
  const req: Record<string, unknown> = { "receipt-data": receiptB64, "exclude-old-transactions": false };
  if (cfg.sharedSecret) req.password = cfg.sharedSecret;
  for (const env of ["production", "sandbox"] as const) {
    let body: Record<string, unknown>;
    try {
      const res = await fetchFn(VERIFY_RECEIPT[env], { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(req) });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      body = await res.json() as Record<string, unknown>;
    } catch (e) {
      throw new PurchaseError(503, "store_unavailable", `verifyReceipt unreachable: ${String(e)}`);
    }
    const status = Number(body.status);
    if (status === 21007 && env === "production") continue; // a sandbox receipt: ask the sandbox
    if (status === 0) return entitlementFromVerifyReceipt(body, productId, item, cfg.bundleId, transactionIdHint);
    if (status === 21004) throw new PurchaseError(503, "store_not_configured", "APPLE_SHARED_SECRET does not match");
    if (status === 21005 || status === 21009 || (status >= 21100 && status <= 21199) || body["is-retryable"] === true) {
      throw new PurchaseError(503, "store_unavailable", `verifyReceipt status ${status}`);
    }
    throw new PurchaseError(422, "invalid_receipt", `verifyReceipt status ${status}`);
  }
  throw new PurchaseError(422, "invalid_receipt", "receipt rejected by both environments");
}

// ---------------------------------------------------------------- helpers

function str(v: unknown): string | null {
  if (typeof v === "string" && v.length > 0) return v;
  if (typeof v === "number" && Number.isFinite(v)) return String(v);
  return null;
}

function pick(o: Record<string, unknown>, keys: string[]): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const k of keys) if (o[k] !== undefined) out[k] = o[k];
  return out;
}
