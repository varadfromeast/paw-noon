// POST /functions/v1/daily-claim  {}
// Daily bounty (SPEC_V2 §7): DAILY.winsRequired wins today -> DAILY.coins, once per day, with a
// streak. "Today" is the PN_DAY_TZ calendar day (default Asia/Kolkata).
//
// Only SERVER-VALIDATED wins count (completed PvP matches the player won). Solo wins against the bot
// happen offline and cannot be verified, so they do not count here; see README "Open points".
import { requireUser } from "../_shared/auth.ts";
import { admin, must } from "../_shared/db.ts";
import { json, serve } from "../_shared/http.ts";
import { takeRate } from "../_shared/rate.ts";
import { DAILY, dayTimeZone } from "../_shared/server_config.ts";

serve("daily-claim", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  await takeRate(db, user.id, "daily_claim");
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
