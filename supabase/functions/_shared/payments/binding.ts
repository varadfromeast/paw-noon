// Who may own a store purchase (review pass 2, Docs/PAYMENTS.md §6).
//
// The risk: purchases are keyed by the store's transaction id, and validating a purchase from another account used
// to MOVE it there (`transferred`, meant for a reinstall that restores onto a new guest). An Apple transaction id is
// a short number that is not secret, and the App Store Server API answers for it without any proof that the caller
// holds the purchase. So anybody who learned (or guessed: ids are dense) another player's transaction id could take
// their gun away by posting `{ transactionId }`.
//
// The rule now:
//   * The client binds every new purchase to its account: Unity IAP `IAppleExtensions.SetApplicationUsername(<user
//     id>)` (StoreKit copies a UUID applicationUsername into the transaction's appAccountToken) and
//     `IGooglePlayConfiguration.SetObfuscatedAccountId(sha256hex(<user id>))` (Play's obfuscatedExternalAccountId).
//   * A purchase bound to the caller is the caller's, always (`match`).
//   * Anything else may only be granted to / moved to the caller with DEVICE PROOF that this phone's store account
//     owns it: a StoreKit 2 signed transaction or an app receipt verified by Apple that contains the purchase, or
//     a Google purchase token (a long random secret only the buying device gets). A bare transaction id is not proof.
//   * Bound to another account and no proof: refused (409 account_mismatch). Unbound (older purchases) and no proof:
//     granted only while nobody else owns it (pn_grant_entitlement p_allow_transfer = false -> owned_elsewhere).
import { sha256Hex, utf8 } from "./der.ts";
import type { PurchaseRequest } from "./receipt.ts";
import type { Store, StoreEntitlement } from "./types.ts";

export type BindingState = "match" | "mismatch" | "absent";

/** The token the client sets for `userId`: Apple = the user id (a UUID, lower case); Google = SHA-256 hex of it. */
export async function expectedAccountToken(store: Store, userId: string): Promise<string> {
  const id = userId.trim().toLowerCase();
  return store === "app_store" ? id : await sha256Hex(utf8(id));
}

export function bindingState(ent: Pick<StoreEntitlement, "accountToken">, expected: string): BindingState {
  const t = ent.accountToken;
  if (t === undefined || t === null || t === "") return "absent";
  return t.trim().toLowerCase() === expected.toLowerCase() ? "match" : "mismatch";
}

/**
 * Whether the validation itself proves this device owns the purchase (no extra store call):
 * a verified StoreKit 2 JWS sent by the client, Apple's verifyReceipt on the device's app receipt, or any Google
 * purchase (the token is the proof). The App Store Server API lookup by transaction id proves nothing about the
 * caller, so it needs the extra receipt check (see `needsReceiptProof`).
 */
export function deviceProof(ent: Pick<StoreEntitlement, "source">): boolean {
  return ent.source === "jws" || ent.source === "verify_receipt" || ent.source === "play_api";
}

/** The server-API path could still be proven with the app receipt the client sent (one verifyReceipt call). */
export function canProveWithReceipt(ent: Pick<StoreEntitlement, "source">, req: Pick<PurchaseRequest, "appReceipt">): boolean {
  return ent.source === "server_api" && !!req.appReceipt;
}

export interface Decision {
  /** Refuse before touching the database (bound to another account, no proof). */
  refuse: { status: number; code: string; message: string } | null;
  /** pn_grant_entitlement p_allow_transfer. */
  allowTransfer: boolean;
}

export function decide(binding: BindingState, proof: boolean): Decision {
  if (binding === "match") return { refuse: null, allowTransfer: true };
  if (binding === "mismatch" && !proof) {
    return { refuse: { status: 409, code: "account_mismatch", message: "this purchase was made for another account" }, allowTransfer: false };
  }
  return { refuse: null, allowTransfer: proof };
}
