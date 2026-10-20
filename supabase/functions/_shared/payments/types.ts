// Shared types of the purchase validation (SPEC_V2 §6, M7).

export type Store = "app_store" | "google_play";
export type PurchaseStatus = "valid" | "pending" | "refunded" | "revoked";
export type StoreEnvironment = "sandbox" | "production";

/** What a store said about one purchase, normalised across Apple and Google. */
export interface StoreEntitlement {
  store: Store;
  productId: string;
  /** Item the product unlocks ("peacemaker"). */
  item: string;
  /**
   * Canonical purchase key, unique per store (purchases.transaction_id): Apple = originalTransactionId (a
   * restore or a re-download of a non-consumable keeps it), Google = orderId, or "token:<sha256>" for
   * purchases without an order id.
   */
  transactionId: string;
  /** Store transaction this particular validation saw (Apple restore = a new id; Google = orderId). */
  latestTransactionId: string;
  originalTransactionId: string | null;
  environment: StoreEnvironment;
  status: PurchaseStatus;
  /** ISO 8601, or null when the store did not say. */
  purchasedAt: string | null;
  /** Which check produced it. */
  source: "jws" | "server_api" | "verify_receipt" | "play_api";
  /**
   * The account the purchase was made for, as the store echoes it back (review pass 2): Apple `appAccountToken`
   * (a UUID: Unity IAP's SetApplicationUsername sets SKPayment.applicationUsername, which StoreKit copies here when it
   * is a UUID), Google `obfuscatedExternalAccountId`. Null when the client did not set one (older builds, restores of
   * purchases made before binding). See binding.ts.
   */
  accountToken?: string | null;
  /** Google: the purchase still needs acknowledging (done by the function when enabled). */
  needsAcknowledge?: boolean;
  /** Store answer, trimmed to what support needs (no receipts, no tokens). */
  raw: Record<string, unknown>;
}

/** A failed validation. `status` is the HTTP status the function answers with. */
export class PurchaseError extends Error {
  readonly status: number;
  readonly code: string;
  readonly extra: Record<string, unknown>;
  constructor(status: number, code: string, message: string, extra: Record<string, unknown> = {}) {
    super(message);
    this.status = status;
    this.code = code;
    this.extra = extra;
  }
}

/** fetch, injectable for tests (recorded store answers). */
export type FetchFn = (input: string, init?: RequestInit) => Promise<Response>;

export function iso(ms: unknown): string | null {
  const n = typeof ms === "string" ? Number(ms) : ms;
  if (typeof n !== "number" || !Number.isFinite(n) || n <= 0) return null;
  return new Date(n).toISOString();
}
