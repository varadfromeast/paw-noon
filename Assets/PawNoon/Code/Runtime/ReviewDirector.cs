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
        public static readonly string[] States = { "title", "countdown2", "draw", "roundresult", "win", "lose", "settings", "foul", "difficulty" };

        private static PawGame Game()
        {
            return PawGame.Instance;
        }

        private static string NotRunning() { return "error: game not running (enter Play mode)"; }

        /// <summary>Forces a deterministic still: title, countdown2, draw, roundresult, win, lose, settings, foul,
        /// difficulty (title with the first-launch EASY / HARD card; the stored pick is kept).</summary>
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
                    case "difficulty":
                    case "choice":
                        g.ShowDifficultyChoice(false);
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

        /// <summary>
        /// Sets the difficulty ("easy" / "hard"), stored like the title toggle (PlayerPrefs "pn_difficulty").
        /// Applies now on the title screen, otherwise from the next match. "reset" forgets the pick and shows
        /// the first-launch card on the title.
        /// </summary>
        public static string SetDifficulty(string difficulty)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (difficulty ?? "").Trim().ToLowerInvariant();
            try
            {
                if (s == "reset" || s == "clear")
                {
                    g.ShowDifficultyChoice(true);
                    return "ok: difficulty cleared, first-launch choice shown";
                }
                if (s != DuelConfig.DifficultyEasy && s != DuelConfig.DifficultyHard)
                    return "error: expected easy, hard or reset";
                g.SetDifficulty(s);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            bool now = g.Config.Difficulty == s;
            return "ok: difficulty " + s + (now ? " (active now)" : " (from the next match)") +
                   " active: botMeanStart=" + g.Config.botMeanStartMs.ToString("0", CultureInfo.InvariantCulture) +
                   " holdMax=" + g.Config.holdMaxSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public static string StartDuel()
        {
            var g = Game();
            if (g == null) return NotRunning();
            MatchIntro.SkipNextForReview = true;   // automated runs skip the match intro unless SetIntro on
            g.StartDuel();
            return "ok: duel started (seed " + g.Director.Seed + ")";
        }

        /// <summary>
        /// Match intro (SPEC_V2 §8.7) for review: "on" = play it at every StartDuel (review runs included, ignores the
        /// once-per-session rule), "off" = never, "auto" = default (first match of a session, not in review runs,
        /// cfg.introEnabled), "reset" = auto and forget that it was shown this session.
        /// </summary>
        public static string SetIntro(string mode)
        {
            string s = (mode ?? "").Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true") MatchIntro.ReviewMode = 1;
            else if (s == "off" || s == "0" || s == "false") MatchIntro.ReviewMode = -1;
            else if (s == "auto" || s == "") MatchIntro.ReviewMode = 0;
            else if (s == "reset") { MatchIntro.ReviewMode = 0; MatchIntro.ShownThisSession = false; }
            else return "error: expected on, off, auto or reset";
            return "ok: intro " + MatchIntro.StatusText;
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
            return "screen=" + g.CurrentMode + " settings=" + g.SettingsOpenState + " difficulty=" + g.Config.Difficulty +
                   (g.Difficulty != g.Config.Difficulty ? "->" + g.Difficulty : "") + " " + g.Director.StatusLine() +
                   " fps=" + UnityEngine.Application.targetFrameRate + " virtual=" + g.Pipeline.VirtualW + "x" + g.Pipeline.VirtualH +
                   " preciseInput=" + g.Input.HasPreciseTimestamps +
                   " intro=" + MatchIntro.StatusText + (g.Director.Intro.Active ? "(playing)" : "") +
                   " opponent=" + (g.Director.Opponent != null ? g.Director.Opponent.id : "cfg");
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
