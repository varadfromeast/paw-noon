namespace PawNoon.Runtime
{
    /// <summary>Review hooks for the town (Docs/TOWN.md). Same contract: string in, status out, never throws.</summary>
    public static partial class ReviewDirector
    {
        /// <summary>
        /// Forces the weather now: "dusk" (dawn plate at weatherDuskDawn + warm grade), "storm" (10 % darker plate,
        /// wind motes unless Reduced Motion, tumbleweed sway), "clear". Lasts until the next match start or the title.
        /// "status" reports the current weather and the match counter that gates it.
        /// </summary>
        public static string Weather(string kind)
        {
            var g = Game();
            if (g == null) return NotRunning();
            var town = g.Director.Town;
            if (town == null) return "error: no town wired";
            string s = (kind ?? "").Trim().ToLowerInvariant();
            try
            {
                switch (s)
                {
                    case "dusk": town.ApplyWeather(TownWeather.Dusk); break;
                    case "storm":
                    case "dust":
                    case "duststorm": town.ApplyWeather(TownWeather.Storm); break;
                    case "clear":
                    case "noon": town.ApplyWeather(TownWeather.Clear); break;
                    case "":
                    case "status": break;
                    default: return "error: expected dusk, storm, clear or status";
                }
                if (s == "dusk" && g.CurrentMode == PawGame.Mode.Title) g.Street.SetDawn(0f);   // the title is dawn already
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: weather " + town.Weather + " (matches played " + PawSettings.MatchesPlayed + ")";
        }

        /// <summary>
        /// Townsfolk still: "peek" (idle, blinking), "duck" (behind cover, as at "1"), "cheer" (LIGHTNING pop + hat
        /// flight, animated), "cheerhold" (popped up, hat at the top of its flight, frozen), "hide". Outside a duel it
        /// sets up a quiet standoff stage first (both cats ready, HUD up).
        /// </summary>
        public static string Townsfolk(string mode)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (mode ?? "").Trim().ToLowerInvariant();
            if (s == "") s = "peek";
            if (s != "peek" && s != "duck" && s != "cheer" && s != "cheerhold" && s != "hide")
                return "error: expected peek, duck, cheer, cheerhold or hide";
            try
            {
                if (g.CurrentMode != PawGame.Mode.Duel)
                {
                    g.EnterReviewDuel();
                    g.Street.SetDawn(0f);
                }
                return g.Director.ReviewTownsfolk(s);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
        }
    }
}
