// purchase-validate's grant step with the account binding rule (binding.ts). Pure apart from the injected grant
// (the pn_grant_entitlement call) and fetch (the optional verifyReceipt proof), so it is unit-tested.
import { verifyReceiptLegacy } from "./apple.ts";
import { type BindingState, bindingState, canProveWithReceipt, decide, deviceProof, expectedAccountToken } from "./binding.ts";
import type { PurchaseRequest } from "./receipt.ts";
import { type FetchFn, PurchaseError, type StoreEntitlement } from "./types.ts";
import type { PaymentsEnv } from "./validate.ts";

export interface GrantRow {
  outcome: string;
  owned_items: string[];
  coins: number;
  equipped_gun: string;
  previous_owner: string | null;
}

export interface GrantMeta {
  binding: BindingState;
  proof: boolean;
}

/** Calls pn_grant_entitlement with p_allow_transfer; undefined = no profile row. */
export type GrantFn = (allowTransfer: boolean, meta: GrantMeta) => Promise<GrantRow | undefined>;

/**
 * Apple server-API lookups prove nothing about the caller; the app receipt the client sent does, when Apple's
 * verifyReceipt confirms it holds this purchase. Transient store failures propagate (the client retries later).
 */
async function proveWithReceipt(ent: StoreEntitlement, req: PurchaseRequest, env: PaymentsEnv, fetchFn: FetchFn): Promise<boolean> {
  try {
    const r = await verifyReceiptLegacy(req.appReceipt!, env.apple, ent.productId, ent.item, fetchFn, ent.latestTransactionId);
    return r.transactionId === ent.transactionId;
  } catch (e) {
    if (e instanceof PurchaseError && e.status >= 500) throw e;
    return false;
  }
}

export async function grantBound(
  ent: StoreEntitlement, req: PurchaseRequest, userId: string, env: PaymentsEnv, fetchFn: FetchFn, grant: GrantFn,
): Promise<{ row: GrantRow; meta: GrantMeta }> {
  const binding = bindingState(ent, await expectedAccountToken(ent.store, userId));
  let proof = deviceProof(ent);
  let proofTried = proof;
  if (!proof && binding === "mismatch" && canProveWithReceipt(ent, req)) {
    proof = await proveWithReceipt(ent, req, env, fetchFn);
    proofTried = true;
  }
  const d = decide(binding, proof);
  if (d.refuse) throw new PurchaseError(d.refuse.status, d.refuse.code, d.refuse.message);

  let meta: GrantMeta = { binding, proof };
  let row = await grant(d.allowTransfer, meta);
  if (!row) throw new PurchaseError(409, "no_profile", "profile not found");
  if (row.outcome === "owned_elsewhere" && !proofTried && canProveWithReceipt(ent, req)) {
    // someone else holds it: only now is the extra receipt check worth a store call
    proof = await proveWithReceipt(ent, req, env, fetchFn);
    if (proof) {
      meta = { binding, proof };
      row = await grant(true, meta);
      if (!row) throw new PurchaseError(409, "no_profile", "profile not found");
    }
  }
  if (row.outcome === "owned_elsewhere") {
    throw new PurchaseError(409, "owned_elsewhere", "this purchase belongs to another account; restore it from the phone that bought it");
  }
  return { row, meta };
}
