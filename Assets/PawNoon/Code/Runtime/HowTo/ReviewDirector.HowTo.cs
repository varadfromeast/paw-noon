using System.Globalization;

namespace PawNoon.Runtime
{
    /// <summary>Bridge hooks for the HOW TO DUEL comic (Docs/TUTORIAL.md).</summary>
    public static partial class ReviewDirector
    {
        /// <summary>
        /// "show" = open the comic over the title (GOT IT returns to the title), "beat N [t]" = open if needed, jump to
        /// beat 1..3 and hold it there (no auto page turn; with t the scene also freezes at t seconds, for stills, e.g.
        /// "beat 2 1.6" = the jam, "beat 3 0.9" = the hat in the air), "next" = turn the page, "close" = GOT IT!
        /// (marks the lesson done like the player would), "skip" = SKIP.
        /// </summary>
        public static string HowTo(string arg)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (arg ?? "").Trim().ToLowerInvariant();
            string rest = null;
            int sp = s.IndexOf(' ');
            if (sp > 0) { rest = s.Substring(sp + 1).Trim(); s = s.Substring(0, sp); }
            var c = g.HowTo;
            switch (s)
            {
                case "":
                case "show":
                case "open":
                    g.ShowHowTo(PawGame.HowToThen.Title);
                    break;
                case "beat":
                {
                    int n = 0;
                    float at = -1f;
                    string ns = rest ?? "";
                    string ts = null;
                    int sp2 = ns.IndexOf(' ');
                    if (sp2 > 0) { ts = ns.Substring(sp2 + 1).Trim(); ns = ns.Substring(0, sp2); }
                    if (!int.TryParse(ns, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < 1 || n > 3)
                        return "error: expected beat 1, 2 or 3 [seconds]";
                    if (ts != null && !float.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out at))
                        return "error: bad seconds '" + ts + "'";
                    if (!c.IsOpen) g.ShowHowTo(PawGame.HowToThen.Title);
                    c.ReviewBeat(n, at);
                    break;
                }
                case "next":
                    if (!c.IsOpen) return "error: comic not open";
                    if (!c.Next()) return "error: already on the last beat (use close)";
                    break;
                case "close":
                case "gotit":
                case "skip":
                    if (!c.IsOpen) return "error: comic not open";
                    g.FinishHowTo();
                    break;
                default:
                    return "error: expected show, beat N [t], next, close or skip";
            }
            return "ok: howto=" + c.StatusText + " tutorialDone=" + (PawSettings.TutorialDone ? "1" : "0");
        }
    }
}
