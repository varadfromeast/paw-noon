// POST /functions/v1/daily-claim  {}
// Daily bounty (SPEC_V2 §7): DAILY.winsRequired wins today -> DAILY.coins, once per day, with a
// streak. "Today" is the PN_DAY_TZ calendar day (default Asia/Kolkata).
//
// Only SERVER-VALIDATED wins count (completed PvP matches the player won). Solo wins against the bot
// happen offline and cannot be verified, so they do not count here; see README "Open points".
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { HttpError, json, serve } from "../_shared/http.ts";
import { takeRate } from "../_shared/rate.ts";
import { DAILY, dayTimeZone } from "../_shared/server_config.ts";

serve("daily-claim", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  await takeRate(db, user.id, "daily_claim");
  // review pass 1 D7: pn_claim_daily raises on a missing profile (a 500); answer like match-start does
  const profile = must<{ id: string } | null>(await db.from("profiles").select("id").eq("id", user.id).maybeSingle(), "load profile");
  if (!profile) throw new HttpError(409, "no_profile", "profile missing; sign in again");
  const tz = dayTimeZone();
  const rows = must<{ claimed: boolean; already_claimed: boolean; wins_today: number; coins: number; streak: number; day: string }[]>(
    await db.rpc("pn_claim_daily", { p_user: user.id, p_tz: tz, p_required: DAILY.winsRequired, p_coins: DAILY.coins }),
    "claim daily",
  );
  const r = rows[0];
  return json({
    claimed: r.claimed,
    alreadyClaimed: r.already_claimed,
    winsToday: r.wins_today,
    winsRequired: DAILY.winsRequired,
    reward: DAILY.coins,
    coins: r.coins,
    streak: r.streak,
    day: r.day,
    timeZone: tz,
  });
});
