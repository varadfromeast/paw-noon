using System.IO;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>Review hooks for feel wave 2 (share card, daily bounty). Same contract: string in, status out, never throws.</summary>
    public static partial class ReviewDirector
    {
        /// <summary>
        /// Renders the share card to &lt;project&gt;/Lead/share.png (no share sheet). Mode: "round" (the round card
        /// on screen / last shown, default), "match" / "win" / "lose" / "standoff" (match share: best round of the
        /// match + verdict), "sample" (deterministic sample round), "samplematch". Uses the real sprites and fonts.
        /// </summary>
        public static string Share(string mode)
        {
            if (Game() == null) return NotRunning();
            string s = (mode ?? "").Trim().ToLowerInvariant();
            try
            {
                ShareCardData d;
                switch (s)
                {
                    case "":
                    case "round": d = ShareLog.ForRound(); break;
                    case "match":
                    case "win": d = ShareLog.ForMatch(0); break;
                    case "lose": d = ShareLog.ForMatch(1); break;
                    case "standoff": d = ShareLog.ForMatch(-1); break;
                    case "sample": d = ShareCardData.Sample(ShareKind.Round); break;
                    case "samplematch": d = ShareCardData.Sample(ShareKind.Match); break;
                    case "samplelastlight": d = ShareCardData.Sample(ShareKind.Round); d.lastLight = true; break;
                    default: return "error: expected round, match, win, lose, standoff, sample, samplematch or samplelastlight";
                }
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Lead", "share.png"));
                if (!ShareService.RenderToFile(d, path)) return "error: share card not rendered (no UI kit yet?)";
                return "ok: " + path + " (" + ShareCardLayout.OutWidth + "x" + ShareCardLayout.OutHeight + ", " + d.kind +
                       ", round " + d.roundNumber + (d.lastLight ? " LAST LIGHT" : "") + ", rounds logged " + ShareLog.Count + ")";
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// Daily bounty on the title (SPEC_V2 §7). "status" (default), "show" (strip visible even without a server,
        /// claims simulated locally), "win" (one PvP win now; the third pays: coins, stamp, streak), "wins N",
        /// "streak N" (pretend yesterday's claim had streak N, so the flame shows and today's claim makes N+1),
        /// "celebrate" (the coin burst only), "reset" (forget wins, streak, coins). Wins added here are simulated:
        /// they never reach the server.
        /// </summary>
        public static string Bounty(string cmd)
        {
            var g = Game();
            if (g == null) return NotRunning();
            if (g.Online == null || g.Online.Bounty == null) return "error: no online director";
            var b = g.Online.Bounty;
            var parts = (cmd ?? "").Trim().ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            string c = parts.Length > 0 ? parts[0] : "status";
            int n = 1;
            if (parts.Length > 1 && !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n))
                return "error: expected a number after " + c;
            try
            {
                switch (c)
                {
                    case "status": break;
                    case "show": b.ReviewSimulate = true; break;
                    case "win":
                    case "wins":
                        b.ReviewSimulate = true;
                        for (int i = 0; i < System.Math.Max(1, n); i++) b.RecordWin();
                        break;
                    case "streak": b.ReviewSimulate = true; b.SetStreakYesterday(n); break;
                    case "celebrate": b.ReviewSimulate = true; b.CelebrateNow(); break;
                    case "reset": b.ResetAll(); break;
                    default: return "error: expected status, show, win, wins N, streak N, celebrate or reset";
                }
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: " + b.Status() + (g.CurrentMode != PawGame.Mode.Title ? " (the strip shows on the title)" : "");
        }
    }
}
