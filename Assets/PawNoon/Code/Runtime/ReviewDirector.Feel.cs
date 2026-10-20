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
    }
}
