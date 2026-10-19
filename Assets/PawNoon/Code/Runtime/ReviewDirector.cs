using System.Globalization;
using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Review / automation hooks (screenshots, MCP, tests in Play mode). Every method takes zero or
    /// one string and returns a short status string. Never throws.
    /// </summary>
    public static class ReviewDirector
    {
        public static readonly string[] States = { "title", "countdown2", "draw", "roundresult", "win", "lose", "settings", "foul" };

        private static PawGame Game()
        {
            return PawGame.Instance;
        }

        private static string NotRunning() { return "error: game not running (enter Play mode)"; }

        /// <summary>Forces a deterministic still: title, countdown2, draw, roundresult, win, lose, settings, foul.</summary>
        public static string Show(string state)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (state ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (s)
                {
                    case "title":
                        g.ShowTitle();
                        break;
                    case "countdown2":
                    case "countdown":
                        g.EnterReviewDuel();
                        g.Director.ReviewCountdown(2);
                        break;
                    case "draw":
                        g.EnterReviewDuel();
                        g.Director.ReviewDraw(183245000L);
                        break;
                    case "roundresult":
                    case "result":
                        g.EnterReviewDuel();
                        g.Director.ReviewRoundResult();
                        break;
                    case "win":
                        g.ShowMatchResultSample(true);
                        break;
                    case "lose":
                        g.ShowMatchResultSample(false);
                        break;
                    case "settings":
                        g.ShowSettingsSample();
                        break;
                    case "foul":
                        g.EnterReviewDuel();
                        g.Director.ReviewFoul();
                        break;
                    default:
                        return "error: unknown state '" + state + "' (use " + string.Join(", ", States) + ")";
                }
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: showing " + s;
        }

        public static string StartDuel()
        {
            var g = Game();
            if (g == null) return NotRunning();
            g.StartDuel();
            return "ok: duel started (seed " + g.Director.Seed + ")";
        }

        /// <summary>Injects a press timestamped now through the real input queue and player client.</summary>
        public static string SimulateTap() { return SimulateSwipe(); }

        /// <summary>Injects a completed draw swipe now (same queue + report path as a real swipe).</summary>
        public static string SimulateSwipe()
        {
            var g = Game();
            if (g == null) return NotRunning();
            g.Input.Inject(g.Clock.NowNs, InputSource.Injected);
            return "ok: swipe injected (phase " + g.Director.Authority.Phase + ")";
        }

        /// <summary>Auto-tap N ms after each DRAW cue; "off" disables.</summary>
        public static string SetAutoTap(string ms)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (ms ?? "").Trim().ToLowerInvariant();
            if (s == "off" || s == "" || s == "-1")
            {
                g.Director.SetAutoTapMs(-1);
                return "ok: auto-tap off";
            }
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0)
                return "error: expected milliseconds or 'off'";
            g.Director.SetAutoTapMs(v);
            return "ok: auto-tap " + v.ToString("0.###", CultureInfo.InvariantCulture) + " ms after each cue";
        }

        /// <summary>Presentation time scale (animations/FX only; duel timing is unaffected).</summary>
        public static string SetTimeScale(string x)
        {
            double v;
            if (!double.TryParse((x ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0 || v > 10)
                return "error: expected a number 0..10";
            PawTime.ReviewScale = (float)v;
            return "ok: presentation time scale " + v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Status()
        {
            var g = Game();
            if (g == null) return NotRunning();
            return "screen=" + g.CurrentMode + " settings=" + g.SettingsOpenState + " " + g.Director.StatusLine() +
                   " fps=" + UnityEngine.Application.targetFrameRate + " virtual=" + g.Pipeline.VirtualW + "x" + g.Pipeline.VirtualH +
                   " preciseInput=" + g.Input.HasPreciseTimestamps;
        }

        /// <summary>Seed for the next match (authority RNG and bot RNG derive from it).</summary>
        public static string Seed(string n)
        {
            var g = Game();
            if (g == null) return NotRunning();
            int v;
            if (!int.TryParse((n ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return "error: expected an integer";
            g.Director.SetSeed(v);
            return "ok: seed " + v + " for the next match";
        }
    }
}
