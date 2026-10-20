// POST /functions/v1/account-delete  { confirm: "DELETE" }
// App Store requirement (SPEC_V2 §5): deletes the caller's auth user. Every table references
// auth.users with ON DELETE CASCADE (profile, own recordings, matches played, ratings, purchases,
// notifications); other players' matches against this player's ghosts keep their result but lose
// the link (ON DELETE SET NULL). Store purchases are not refunded by this; the receipts stay with
// Apple / Google and can be restored onto a new account.
import { requireUser } from "../_shared/auth.ts";
import { admin } from "../_shared/db.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import { takeRate } from "../_shared/rate.ts";

serve("account-delete", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, 1024);
  if (body.confirm !== "DELETE") throw new HttpError(400, "confirm_required", 'send { "confirm": "DELETE" }');
  await takeRate(db, user.id, "account_delete");

  const { error } = await db.auth.admin.deleteUser(user.id, false); // hard delete
  if (error) {
    console.error("[account-delete]", user.id, error.message);
    throw new HttpError(500, "delete_failed", "could not delete the account, try again");
  }
  console.log(`[account-delete] deleted ${user.id}`);
  return json({ deleted: true, userId: user.id });
});
