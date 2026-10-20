// Google Play validation (SPEC_V2 §6): Play Developer API purchases.products.get with an OAuth token
// obtained from a service account. The service-account JWT (RS256) is built by hand with WebCrypto, so
// the function needs no npm package. Non-consumables must be acknowledged within 3 days or Google refunds
// them; the function acknowledges a valid, unacknowledged purchase itself (Unity IAP acknowledges again on
// ConfirmPendingProduct, which Google answers as a no-op).
import { ab, bytesToB64url, pemToDer, sha256Hex, utf8 } from "./der.ts";
import { type FetchFn, iso, PurchaseError, type StoreEntitlement } from "./types.ts";

export interface GoogleConfig {
  packageName: string;
  /** The service-account key JSON (client_email, private_key, token_uri). */
  serviceAccountJson?: string;
  /** Acknowledge valid unacknowledged purchases (default true). */
  acknowledge?: boolean;
}

export const PLAY_API = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications";
export const PLAY_SCOPE = "https://www.googleapis.com/auth/androidpublisher";
const TOKEN_URI = "https://oauth2.googleapis.com/token";

interface ServiceAccount {
  client_email: string;
  private_key: string;
  token_uri: string;
}

export function parseServiceAccount(jsonText: string | undefined): ServiceAccount {
  if (!jsonText) throw new PurchaseError(503, "store_not_configured", "GOOGLE_SERVICE_ACCOUNT_JSON is not set");
  let o: Record<string, unknown>;
  try {
    o = JSON.parse(jsonText);
  } catch {
    throw new PurchaseError(503, "store_not_configured", "GOOGLE_SERVICE_ACCOUNT_JSON is not JSON");
  }
  if (typeof o.client_email !== "string" || typeof o.private_key !== "string") {
    throw new PurchaseError(503, "store_not_configured", "service account needs client_email and private_key");
  }
  return { client_email: o.client_email, private_key: o.private_key, token_uri: typeof o.token_uri === "string" ? o.token_uri : TOKEN_URI };
}

/** RS256 JWT assertion for the OAuth 2.0 JWT bearer grant (RFC 7523), valid one hour. */
export async function serviceAccountAssertion(sa: ServiceAccount, nowSec: number): Promise<string> {
  let key: CryptoKey;
  try {
    key = await crypto.subtle.importKey("pkcs8", ab(pemToDer(sa.private_key.replace(/\\n/g, "\n"))),
      { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  } catch {
    throw new PurchaseError(503, "store_not_configured", "service account private_key is not an RSA PKCS#8 key");
  }
  const header = { alg: "RS256", typ: "JWT" };
  const claims = { iss: sa.client_email, scope: PLAY_SCOPE, aud: sa.token_uri, iat: nowSec, exp: nowSec + 3600 };
  const signing = `${bytesToB64url(utf8(JSON.stringify(header)))}.${bytesToB64url(utf8(JSON.stringify(claims)))}`;
  const sig = new Uint8Array(await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, ab(utf8(signing))));
  return `${signing}.${bytesToB64url(sig)}`;
}

/** Access token cache across invocations of a warm function instance. */
let cached: { token: string; until: number; email: string } | null = null;

export function clearGoogleTokenCache(): void {
  cached = null;
}

export async function googleAccessToken(cfg: GoogleConfig, fetchFn: FetchFn, now = Date.now()): Promise<string> {
  const sa = parseServiceAccount(cfg.serviceAccountJson);
  if (cached && cached.email === sa.client_email && cached.until > now + 60_000) return cached.token;
  const assertion = await serviceAccountAssertion(sa, Math.floor(now / 1000));
  let res: Response;
  try {
    res = await fetchFn(sa.token_uri, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: `grant_type=${encodeURIComponent("urn:ietf:params:oauth:grant-type:jwt-bearer")}&assertion=${assertion}`,
    });
  } catch (e) {
    throw new PurchaseError(503, "store_unavailable", `Google OAuth unreachable: ${String(e)}`);
  }
  if (res.status === 400 || res.status === 401) throw new PurchaseError(503, "store_not_configured", `Google OAuth refused the service account (${res.status})`);
  if (!res.ok) throw new PurchaseError(503, "store_unavailable", `Google OAuth ${res.status}`);
  const body = await res.json() as Record<string, unknown>;
  if (typeof body.access_token !== "string") throw new PurchaseError(503, "store_unavailable", "Google OAuth answer without access_token");
  const ttl = typeof body.expires_in === "number" ? body.expires_in : 3600;
  cached = { token: body.access_token, until: now + ttl * 1000, email: sa.client_email };
  return body.access_token;
}

/** ProductPurchase (purchases.products.get) -> entitlement. */
export async function entitlementFromProductPurchase(
  p: Record<string, unknown>,
  productId: string,
  item: string,
  purchaseToken: string,
): Promise<StoreEntitlement> {
  if (typeof p.productId === "string" && p.productId !== productId) throw new PurchaseError(422, "product_mismatch", "token is for another product");
  const state = Number(p.purchaseState ?? 0);
  const status = state === 0 ? "valid" : state === 2 ? "pending" : "refunded";
  const orderId = typeof p.orderId === "string" && p.orderId.length > 0 ? p.orderId : null;
  const key = orderId ?? `token:${await sha256Hex(utf8(purchaseToken))}`;
  return {
    store: "google_play",
    productId,
    item,
    transactionId: key,
    latestTransactionId: key,
    originalTransactionId: orderId,
    // purchaseType 0 = test purchase (license testers), absent = a real purchase
    environment: p.purchaseType === 0 ? "sandbox" : "production",
    status,
    purchasedAt: iso(p.purchaseTimeMillis),
    source: "play_api",
    needsAcknowledge: status === "valid" && Number(p.acknowledgementState ?? 0) === 0,
    raw: pickPlay(p),
  };
}

function pickPlay(p: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const k of ["orderId", "purchaseTimeMillis", "purchaseState", "consumptionState", "purchaseType", "acknowledgementState",
    "regionCode", "kind", "quantity", "refundableQuantity"]) {
    if (p[k] !== undefined) out[k] = p[k];
  }
  return out;
}

export async function verifyPlayPurchase(
  purchaseToken: string,
  cfg: GoogleConfig,
  productId: string,
  item: string,
  fetchFn: FetchFn,
  now = Date.now(),
): Promise<StoreEntitlement> {
  if (!/^[A-Za-z0-9._\-:]{8,4096}$/.test(purchaseToken)) throw new PurchaseError(400, "bad_request", "malformed purchaseToken");
  const access = await googleAccessToken(cfg, fetchFn, now);
  const base = `${PLAY_API}/${encodeURIComponent(cfg.packageName)}/purchases/products/${encodeURIComponent(productId)}/tokens/${encodeURIComponent(purchaseToken)}`;
  let res: Response;
  try {
    res = await fetchFn(base, { headers: { Authorization: `Bearer ${access}`, Accept: "application/json" } });
  } catch (e) {
    throw new PurchaseError(503, "store_unavailable", `Play API unreachable: ${String(e)}`);
  }
  if (res.status === 404 || res.status === 400 || res.status === 410) {
    await res.body?.cancel();
    throw new PurchaseError(422, "invalid_receipt", `Play does not know this purchase (${res.status})`);
  }
  if (res.status === 401 || res.status === 403) {
    await res.body?.cancel();
    throw new PurchaseError(503, "store_not_configured", `Play API refused the service account (${res.status}); grant it access in Play Console`);
  }
  if (!res.ok) throw new PurchaseError(503, "store_unavailable", `Play API ${res.status}`);
  const ent = await entitlementFromProductPurchase(await res.json() as Record<string, unknown>, productId, item, purchaseToken);
  if (ent.needsAcknowledge && cfg.acknowledge !== false) {
    try {
      const ack = await fetchFn(`${base}:acknowledge`, {
        method: "POST",
        headers: { Authorization: `Bearer ${access}`, "Content-Type": "application/json" },
        body: "{}",
      });
      if (ack.ok) ent.needsAcknowledge = false;
      else console.warn("[purchase-validate] acknowledge failed", ack.status);
      await ack.body?.cancel();
    } catch (e) {
      // not fatal: the client's ConfirmPendingProduct acknowledges too
      console.warn("[purchase-validate] acknowledge failed", String(e));
    }
  }
  return ent;
}
