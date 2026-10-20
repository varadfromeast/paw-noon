// Ranked gating (SPEC_V2 §4.3): a linked Apple / Google account, an unflagged profile and a client build on
// the allow-list. Pure (no Supabase import) so it is unit-tested under the Node runner.
import { rankedAllowGuests, rankedBuildAllowed } from "./server_config.ts";

export interface IdentityLike {
  isAnonymous: boolean;
  providers: string[];
}

/** Ranked needs a linked Apple/Google identity (§4.3, §5). */
export function hasLinkedIdentity(u: IdentityLike): boolean {
  return !u.isAnonymous && u.providers.some((p) => p === "apple" || p === "google");
}

export type RankedReason = "link_account" | "account_flagged" | "client_build";

export interface RankedDenial {
  status: 403;
  /** `link_required` when linking would fix it (the client shows SIGN IN TO RANK), else `ranked_not_allowed`. */
  code: "link_required" | "ranked_not_allowed";
  reasons: RankedReason[];
}

/** null = ranked allowed. A shadow flag (C11) is deliberately NOT a reason: shadow players never learn it. */
export function rankedGate(u: IdentityLike, profile: { flagged: boolean }, clientBuild: string): RankedDenial | null {
  const reasons: RankedReason[] = [];
  if (!hasLinkedIdentity(u) && !rankedAllowGuests()) reasons.push("link_account");
  if (profile.flagged) reasons.push("account_flagged");
  if (!rankedBuildAllowed(clientBuild)) reasons.push("client_build");
  if (reasons.length === 0) return null;
  return { status: 403, code: reasons.includes("link_account") ? "link_required" : "ranked_not_allowed", reasons };
}

/** match-start `mode`: absent = casual (M5 default); anything else but the two names is a 400. */
export function parseMode(v: unknown): "casual" | "ranked" | null {
  if (v === undefined || v === null || v === "") return "casual";
  return v === "casual" || v === "ranked" ? v : null;
}
