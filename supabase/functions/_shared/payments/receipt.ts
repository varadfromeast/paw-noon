// Parses the purchase-validate request, including the raw Unity IAP receipt the client forwards as is.
//
// Unity IAP 4.x `Product.receipt` (a JSON string):
//   App Store:   {"Store":"AppleAppStore","TransactionID":"2000000123456789","Payload":"<base64 app receipt>"}
//   Google Play: {"Store":"GooglePlay","TransactionID":"<purchase token>","Payload":"{\"json\":\"{...ProductPurchase...}\",
//                 \"signature\":\"...\",\"skuDetails\":[...]}"}   (Payload and json are JSON *strings*)
//   Editor:      {"Store":"fake",...}  never accepted.
import { GUN_PRODUCT } from "../duel/loadouts.ts";
import { PurchaseError, type Store } from "./types.ts";

export interface PurchaseRequest {
  store: Store;
  productId: string;
  /** Item the product unlocks. */
  item: string;
  restore: boolean;
  /** App Store: StoreKit 2 JWS (optional). */
  signedTransaction: string | null;
  /** App Store: transaction id (explicit, or from the Unity receipt). */
  transactionId: string | null;
  /** App Store: base64 app receipt (from the Unity receipt or `appReceipt`). */
  appReceipt: string | null;
  /** Google Play: purchase token (explicit, or from the Unity receipt). */
  purchaseToken: string | null;
  /** Google Play: package name the receipt claims (checked against the configured one). */
  packageName: string | null;
}

export interface UnityReceipt {
  store: string;
  transactionId: string | null;
  payload: string | null;
}

/** Unity IAP receipt JSON -> its three fields. Throws PurchaseError 400 when it is not one. */
export function parseUnityReceipt(text: string): UnityReceipt {
  let o: unknown;
  try {
    o = JSON.parse(text);
  } catch {
    throw new PurchaseError(400, "bad_receipt", "receipt is not JSON");
  }
  if (o === null || typeof o !== "object" || Array.isArray(o)) throw new PurchaseError(400, "bad_receipt", "receipt is not an object");
  const r = o as Record<string, unknown>;
  if (typeof r.Store !== "string") throw new PurchaseError(400, "bad_receipt", "receipt has no Store");
  return {
    store: r.Store,
    transactionId: typeof r.TransactionID === "string" && r.TransactionID.length > 0 ? r.TransactionID : null,
    payload: typeof r.Payload === "string" && r.Payload.length > 0 ? r.Payload : null,
  };
}

/** The ProductPurchase-like JSON inside a Google Unity receipt's Payload. */
export function parseGooglePayload(payload: string): { purchaseToken: string | null; productId: string | null; packageName: string | null; orderId: string | null } {
  let outer: Record<string, unknown>;
  try {
    outer = JSON.parse(payload);
  } catch {
    throw new PurchaseError(400, "bad_receipt", "Google payload is not JSON");
  }
  let inner: Record<string, unknown> = {};
  if (typeof outer.json === "string") {
    try {
      inner = JSON.parse(outer.json);
    } catch {
      throw new PurchaseError(400, "bad_receipt", "Google payload.json is not JSON");
    }
  } else if (outer.json && typeof outer.json === "object") {
    inner = outer.json as Record<string, unknown>;
  }
  const s = (v: unknown) => (typeof v === "string" && v.length > 0 ? v : null);
  return { purchaseToken: s(inner.purchaseToken), productId: s(inner.productId), packageName: s(inner.packageName), orderId: s(inner.orderId) };
}

const PRODUCT_ITEMS: Record<string, string> = Object.fromEntries(Object.entries(GUN_PRODUCT).map(([item, pid]) => [pid, item]));

function optStr(body: Record<string, unknown>, key: string, max: number): string | null {
  const v = body[key];
  if (v === undefined || v === null || v === "") return null;
  if (typeof v !== "string") throw new PurchaseError(400, "bad_request", `${key} must be a string`);
  if (v.length > max) throw new PurchaseError(400, "bad_request", `${key} is too long`);
  return v;
}

/** Validates the request body shape and pulls the store proof out of it (no network). */
export function parsePurchaseRequest(body: Record<string, unknown>): PurchaseRequest {
  const store = body.store;
  if (store !== "app_store" && store !== "google_play") throw new PurchaseError(400, "bad_request", 'store must be "app_store" or "google_play"');
  if (typeof body.productId !== "string" || !(body.productId in PRODUCT_ITEMS)) throw new PurchaseError(400, "unknown_product", "unknown productId");
  if (body.restore !== undefined && typeof body.restore !== "boolean") throw new PurchaseError(400, "bad_request", "restore must be a boolean");
  const req: PurchaseRequest = {
    store,
    productId: body.productId,
    item: PRODUCT_ITEMS[body.productId],
    restore: body.restore === true,
    signedTransaction: optStr(body, "signedTransaction", 16 * 1024),
    transactionId: optStr(body, "transactionId", 64),
    appReceipt: optStr(body, "appReceipt", 200 * 1024),
    purchaseToken: optStr(body, "purchaseToken", 4096),
    packageName: null,
  };
  const unity = optStr(body, "receipt", 240 * 1024);
  if (unity) {
    const r = parseUnityReceipt(unity);
    if (r.store === "fake") throw new PurchaseError(422, "fake_store", "receipts from the editor's fake store are never valid");
    if (store === "app_store") {
      if (r.store !== "AppleAppStore" && r.store !== "MacAppStore") throw new PurchaseError(400, "bad_receipt", `receipt from ${r.store}, expected AppleAppStore`);
      req.transactionId ??= r.transactionId;
      req.appReceipt ??= r.payload;
    } else {
      if (r.store !== "GooglePlay") throw new PurchaseError(400, "bad_receipt", `receipt from ${r.store}, expected GooglePlay`);
      const g = r.payload ? parseGooglePayload(r.payload) : { purchaseToken: null, productId: null, packageName: null, orderId: null };
      if (g.productId && g.productId !== req.productId) throw new PurchaseError(422, "product_mismatch", "receipt is for another product");
      req.purchaseToken ??= g.purchaseToken ?? r.transactionId;
      req.packageName = g.packageName;
    }
  }
  if (store === "app_store" && !req.signedTransaction && !req.transactionId && !req.appReceipt) {
    throw new PurchaseError(400, "bad_request", "app_store needs receipt, signedTransaction, transactionId or appReceipt");
  }
  if (store === "google_play" && !req.purchaseToken) throw new PurchaseError(400, "bad_request", "google_play needs receipt or purchaseToken");
  if (req.transactionId && !/^\d{1,32}$/.test(req.transactionId)) throw new PurchaseError(400, "bad_request", "transactionId must be numeric");
  return req;
}
