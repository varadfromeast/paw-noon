namespace PawNoon.Runtime
{
    /// <summary>Bridge hooks for the first-run tutorial (Docs/TUTORIAL.md).</summary>
    public static partial class ReviewDirector
    {
        /// <summary>
        /// "on" = the tutorial opens every StartDuel (review runs included), "off" = never, "auto" = default (first solo
        /// match until it was finished or skipped; never in review runs), "reset" = auto and forget that it was done,
        /// "run" = the Settings replay now (tutorial, then the title). While it runs: "grip" / "lift" simulate the thumb
        /// on / off the holster ("real" = back to real input), "skip" = SKIP, "start" = TAP TO START. Swipes go through
        /// SimulateSwipe (the real input queue).
        /// </summary>
        public static string Tutorial(string mode)
        {
            var g = Game();
            string s = (mode ?? "").Trim().ToLowerInvariant();
            switch (s)
            {
                case "on": TutorialDirector.ReviewMode = 1; break;
                case "off": TutorialDirector.ReviewMode = -1; break;
                case "auto":
                case "": TutorialDirector.ReviewMode = 0; break;
                case "reset":
                    TutorialDirector.ReviewMode = 0;
                    PawSettings.TutorialDone = false;
                    PawSettings.Save();
                    break;
                case "grip": TutorialDirector.ReviewThumb = 1; break;
                case "lift": TutorialDirector.ReviewThumb = 0; break;
                case "real": TutorialDirector.ReviewThumb = -1; break;
                case "run":
                    if (g == null) return NotRunning();
                    g.ReplayTutorial();
                    break;
                case "skip":
                case "start":
                    if (g == null) return NotRunning();
                    if (g.Director.Tutorial == null || !g.Director.Tutorial.Active) return "error: no tutorial running";
                    if (s == "skip") g.Director.Tutorial.ReviewSkip();
                    else if (g.Director.Tutorial.Flow.Step != PawNoon.Core.TutorialStep.Ready) return "error: not on the ready card (" + g.Director.Tutorial.Flow.Step + ")";
                    else g.Director.Tutorial.ReviewConfirm();
                    break;
                default:
                    return "error: expected on, off, auto, reset, run, grip, lift, real, skip or start";
            }
            return "ok: tutorial " + TutorialStatus();
        }

        private static string TutorialStatus()
        {
            var g = Game();
            if (g != null && g.Director != null && g.Director.Tutorial != null) return g.Director.Tutorial.StatusText;
            return "none";
        }
    }
}
