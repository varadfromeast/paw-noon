using System;
using System.Globalization;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Daily bounty + streak (SPEC_V2 §7), the client side. Engine-free state machine, pinned by EditMode tests.
    /// Days are the player's LOCAL calendar days (what the title shows). Only PvP wins count (the server's
    /// daily-claim counts completed, server-validated PvP wins; solo wins against the bot never reach it).
    /// Flow: each PvP win adds one for today; at <see cref="Required"/> wins a claim becomes pending and is sent
    /// through daily-claim whenever the game can (now, after a queued match-submit is delivered, on the next
    /// launch or title). Offline the count is kept locally and the claim waits. The server's answer is final:
    /// claimed (celebrate, streak and coins from the server), already claimed, or not enough validated wins yet
    /// (stay pending; the queued submissions have not arrived).
    /// </summary>
    public sealed class BountyState
    {
        public enum ClaimOutcome { Claimed, AlreadyClaimed, NotYet, Offline, Failed }

        public int Required = 3;

        /// <summary>Local day the counters belong to (days since 0001-01-01), 0 = never.</summary>
        public int Day;
        public int WinsToday;
        /// <summary>A claim for <see cref="Day"/> is owed to the server.</summary>
        public bool Pending;
        /// <summary>Local day of the last successful (or already made) claim, 0 = never.</summary>
        public int LastClaimDay;
        /// <summary>Streak as of <see cref="LastClaimDay"/> (the server's number when it answered).</summary>
        public int Streak;
        /// <summary>Coin balance reported by the server (-1 = unknown).</summary>
        public int Coins = -1;

        public static int DayOf(DateTime local) { return (int)(local.Date.Ticks / TimeSpan.TicksPerDay); }

        /// <summary>New local day: today's wins start at 0 and a claim owed for an earlier day is dropped.</summary>
        public bool Rollover(DateTime localNow)
        {
            int today = DayOf(localNow);
            if (today == Day) return false;
            Day = today;
            WinsToday = 0;
            Pending = false;
            return true;
        }

        public bool ClaimedToday(DateTime localNow) { return LastClaimDay != 0 && LastClaimDay == DayOf(localNow); }

        /// <summary>A PvP win just finished. Returns true when a claim should be attempted now.</summary>
        public bool RecordPvpWin(DateTime localNow)
        {
            Rollover(localNow);
            if (WinsToday < 999) WinsToday++;
            if (WinsToday >= Required && !ClaimedToday(localNow)) Pending = true;
            return Pending;
        }

        /// <summary>Whether a daily-claim call is owed today.</summary>
        public bool ShouldClaim(DateTime localNow)
        {
            Rollover(localNow);
            return Pending && !ClaimedToday(localNow);
        }

        /// <summary>
        /// Applies the server's answer (or the lack of one). Returns true when the bounty was just paid (celebrate).
        /// serverWins / serverStreak / serverCoins are ignored when negative.
        /// </summary>
        public bool Apply(DateTime localNow, ClaimOutcome outcome, int serverWins, int serverStreak, int serverCoins)
        {
            return Apply(localNow, outcome, serverWins, serverStreak, serverCoins, false);
        }

        /// <summary>
        /// As above; <paramref name="nothingQueued"/> = no match-submit is waiting to be delivered, so a "not yet"
        /// answer is final for now: the server's count replaces the local one (a local win the server did not
        /// confirm, e.g. its verdict differed, stops the claim from being retried all day).
        /// </summary>
        public bool Apply(DateTime localNow, ClaimOutcome outcome, int serverWins, int serverStreak, int serverCoins, bool nothingQueued)
        {
            Rollover(localNow);
            int today = DayOf(localNow);
            if (serverCoins >= 0) Coins = serverCoins;
            switch (outcome)
            {
                case ClaimOutcome.Claimed:
                    Pending = false;
                    // without the server's number: yesterday's streak (read before today's claim is stored) + 1
                    Streak = serverStreak >= 0 ? serverStreak : StreakAt(today - 1) + 1;
                    LastClaimDay = today;
                    if (serverWins > WinsToday) WinsToday = serverWins;
                    if (WinsToday < Required) WinsToday = Required;
                    return true;
                case ClaimOutcome.AlreadyClaimed:
                    Pending = false;
                    LastClaimDay = today;
                    if (serverStreak >= 0) Streak = serverStreak;
                    if (WinsToday < Required) WinsToday = Required;
                    return false;
                case ClaimOutcome.NotYet:
                    // the server has seen fewer validated wins: while submissions are still queued keep the local
                    // count and stay pending; with nothing queued the server's count is the truth
                    if (nothingQueued && serverWins >= 0)
                    {
                        WinsToday = serverWins;
                        Pending = false;
                    }
                    else if (serverWins > WinsToday) WinsToday = serverWins;
                    return false;
                default:
                    // offline / failed: nothing changes, the claim is retried later
                    return false;
            }
        }

        /// <summary>The streak to show on <paramref name="day"/>: alive while the last claim was that day or the day before.</summary>
        public int StreakAt(int day)
        {
            if (LastClaimDay == 0 || Streak <= 0) return 0;
            return day - LastClaimDay <= 1 && day >= LastClaimDay ? Streak : 0;
        }

        public int DisplayStreak(DateTime localNow) { return StreakAt(DayOf(localNow)); }

        /// <summary>Wins to show for today (0 after a day change, capped at Required).</summary>
        public int DisplayWins(DateTime localNow)
        {
            int w = DayOf(localNow) == Day ? WinsToday : 0;
            return w > Required ? Required : w;
        }

        // ------------------------------------------------------------------ title copy

        public enum Phase { Open, Progress, Claiming, WillSync, Claimed }

        public Phase PhaseAt(DateTime localNow, bool online)
        {
            if (ClaimedToday(localNow)) return Phase.Claimed;
            int w = DisplayWins(localNow);
            if (w >= Required) return online ? Phase.Claiming : Phase.WillSync;
            return w > 0 ? Phase.Progress : Phase.Open;
        }

        /// <summary>The bounty line on the title (bitmap-font glyphs only).</summary>
        public string Line(DateTime localNow, bool online, int reward)
        {
            int w = DisplayWins(localNow);
            switch (PhaseAt(localNow, online))
            {
                case Phase.Claimed: return "BOUNTY PAID  +" + reward.ToString(CultureInfo.InvariantCulture);
                case Phase.Claiming: return "BOUNTY: " + w + "/" + Required + " CLAIMING...";
                case Phase.WillSync: return "BOUNTY: " + w + "/" + Required + " WILL SYNC";
                case Phase.Progress: return "BOUNTY: " + w + "/" + Required + " WINS TODAY";
                default: return "BOUNTY: WIN " + Required + " PVP DUELS";
            }
        }

        // ------------------------------------------------------------------ persistence

        /// <summary>"v1|day|wins|pending|lastClaimDay|streak|coins".</summary>
        public string Serialize()
        {
            var c = CultureInfo.InvariantCulture;
            return "v1|" + Day.ToString(c) + "|" + WinsToday.ToString(c) + "|" + (Pending ? "1" : "0") + "|" +
                   LastClaimDay.ToString(c) + "|" + Streak.ToString(c) + "|" + Coins.ToString(c);
        }

        public static BountyState Parse(string s, int required)
        {
            var b = new BountyState();
            b.Required = Math.Max(1, required);
            if (string.IsNullOrEmpty(s)) return b;
            var p = s.Split('|');
            if (p.Length < 7 || p[0] != "v1") return b;
            int v;
            var c = CultureInfo.InvariantCulture;
            if (int.TryParse(p[1], NumberStyles.Integer, c, out v)) b.Day = v;
            if (int.TryParse(p[2], NumberStyles.Integer, c, out v)) b.WinsToday = Math.Max(0, v);
            b.Pending = p[3] == "1";
            if (int.TryParse(p[4], NumberStyles.Integer, c, out v)) b.LastClaimDay = v;
            if (int.TryParse(p[5], NumberStyles.Integer, c, out v)) b.Streak = Math.Max(0, v);
            if (int.TryParse(p[6], NumberStyles.Integer, c, out v)) b.Coins = v;
            return b;
        }
    }
}
