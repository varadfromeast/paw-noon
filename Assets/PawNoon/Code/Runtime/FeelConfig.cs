using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Presentation tunables for the feel passes (Resources/Data/feel_config.json). Client-only: nothing here
    /// reaches Core, the authority or the server, so it lives apart from duel_config.json (which the server
    /// mirrors through supabase/scripts/sync_duel_config.sh). Missing file or keys fall back to the defaults.
    /// </summary>
    public sealed class FeelConfig
    {
        /// <summary>Match intro opponent: "walk" (steps out right, walks back in) or "stay" (settles in place).</summary>
        public string introOpponent = "walk";
        /// <summary>Daily bounty on the title (SPEC_V2 §7). Off hides the strip and never calls daily-claim.</summary>
        public bool bountyEnabled = true;
        /// <summary>PvP wins per day for the bounty (the server's DAILY.winsRequired decides; this is the display).</summary>
        public int bountyWinsRequired = 3;
        /// <summary>Coins the bounty pays (display only; the server's DAILY.coins decides).</summary>
        public int bountyReward = 20;
        /// <summary>SHARE buttons on the round card and the match result screen.</summary>
        public bool shareEnabled = true;
        /// <summary>First-run tutorial pacing (Docs/TUTORIAL.md): keys tutorialGripSeconds, tutorialHoldMinSeconds,
        /// tutorialHoldMaxSeconds, tutorialLiftGraceSeconds, tutorialFailSeconds, tutorialVerdictSeconds, tutorialSlowRetries.</summary>
        public TutorialTuning Tutorial = new TutorialTuning();
        /// <summary>Townsfolk + weather pass (Docs/TOWN.md): keys town* and weather*.</summary>
        public TownTuning Town = new TownTuning();

        // ---- exchange outcome readability / win moments (Docs/FEEL.md "Outcome moments"); presentation only
        /// <summary>Outright wins in a row before the round card shows the brass streak tag ("x2"); 0 = never.</summary>
        public int streakMin = 2;
        /// <summary>Wins in a row for "ON A ROLL" on the round sign and the opponent's rattled banter; 0 = never.</summary>
        public int streakRollMin = 3;
        /// <summary>Seconds after the grade stamp lands until the streak tag pops in.</summary>
        public float streakTagDelay = 0.42f;
        /// <summary>How long the round sign reads "ON A ROLL" before it goes back to ROUND n / LAST LIGHT.</summary>
        public float rollLabelSeconds = 1.6f;
        /// <summary>Seconds after the grade stamp lands until the NEW BEST! ribbon, confetti, sfx and haptic.</summary>
        public float newBestDelay = 0.2f;
        /// <summary>Confetti motes in the NEW BEST burst (whole pixels, two palette colours). Reduced Motion: none.</summary>
        public int confettiCount = 26;
        /// <summary>Life of a confetti mote (seconds, the last fifth blinks out).</summary>
        public float confettiSeconds = 0.8f;
        /// <summary>Launch speed of the confetti (canvas px/s, randomised 55-100 %).</summary>
        public float confettiSpeed = 210f;
        /// <summary>Confetti gravity (canvas px/s^2, y down).</summary>
        public float confettiGravity = 320f;
        /// <summary>Frames of solid white on the opponent after the kill slow-mo of the match-deciding hit (0 = off).</summary>
        public int killPunchFlashFrames = 1;
        /// <summary>Camera kick (canvas px, down) on the same frame. Reduced Motion: neither flash nor kick.</summary>
        public float killPunchKickPx = 2f;

        /// <summary>Pool size of the round card's confetti (confettiCount is clamped to it).</summary>
        public const int ConfettiMax = 48;

        private static FeelConfig cached;

        public bool IntroOpponentWalks { get { return introOpponent != "stay"; } }

        public static FeelConfig Current
        {
            get
            {
                if (cached == null) cached = Load(LoadText());
                return cached;
            }
        }

        private static string LoadText()
        {
            var ta = Resources.Load<TextAsset>("Data/feel_config");
            return ta != null ? ta.text : null;
        }

        /// <summary>Parses the JSON (null / broken = defaults). Pure, for tests.</summary>
        public static FeelConfig Load(string json)
        {
            var c = new FeelConfig();
            if (string.IsNullOrEmpty(json)) return c;
            object root;
            try { root = PnJson.Parse(json); }
            catch (System.Exception) { return c; }
            if (root == null) return c;
            c.introOpponent = PnJson.Str(root, "introOpponent", c.introOpponent);
            c.bountyEnabled = PnJson.Bool(root, "bountyEnabled", c.bountyEnabled);
            c.bountyWinsRequired = Mathf.Max(1, PnJson.Int(root, "bountyWinsRequired", c.bountyWinsRequired));
            c.bountyReward = Mathf.Max(0, PnJson.Int(root, "bountyReward", c.bountyReward));
            c.shareEnabled = PnJson.Bool(root, "shareEnabled", c.shareEnabled);
            var t = c.Tutorial;
            t.gripSeconds = PnJson.Num(root, "tutorialGripSeconds", t.gripSeconds);
            t.holdMinSeconds = PnJson.Num(root, "tutorialHoldMinSeconds", t.holdMinSeconds);
            t.holdMaxSeconds = PnJson.Num(root, "tutorialHoldMaxSeconds", t.holdMaxSeconds);
            t.liftGraceSeconds = PnJson.Num(root, "tutorialLiftGraceSeconds", t.liftGraceSeconds);
            t.failSeconds = PnJson.Num(root, "tutorialFailSeconds", t.failSeconds);
            t.verdictSeconds = PnJson.Num(root, "tutorialVerdictSeconds", t.verdictSeconds);
            t.slowRetries = PnJson.Int(root, "tutorialSlowRetries", t.slowRetries);
            t.Sanitize();
            c.Town = TownTuning.Read(root);
            c.streakMin = Mathf.Clamp(PnJson.Int(root, "streakMin", c.streakMin), 0, 99);
            c.streakRollMin = Mathf.Clamp(PnJson.Int(root, "streakRollMin", c.streakRollMin), 0, 99);
            c.streakTagDelay = Mathf.Clamp((float)PnJson.Num(root, "streakTagDelay", c.streakTagDelay), 0f, 3f);
            c.rollLabelSeconds = Mathf.Clamp((float)PnJson.Num(root, "rollLabelSeconds", c.rollLabelSeconds), 0.2f, 5f);
            c.newBestDelay = Mathf.Clamp((float)PnJson.Num(root, "newBestDelay", c.newBestDelay), 0f, 3f);
            c.confettiCount = Mathf.Clamp(PnJson.Int(root, "confettiCount", c.confettiCount), 0, ConfettiMax);
            c.confettiSeconds = Mathf.Clamp((float)PnJson.Num(root, "confettiSeconds", c.confettiSeconds), 0.2f, 3f);
            c.confettiSpeed = Mathf.Clamp((float)PnJson.Num(root, "confettiSpeed", c.confettiSpeed), 10f, 400f);
            c.confettiGravity = Mathf.Clamp((float)PnJson.Num(root, "confettiGravity", c.confettiGravity), 0f, 1500f);
            c.killPunchFlashFrames = Mathf.Clamp(PnJson.Int(root, "killPunchFlashFrames", c.killPunchFlashFrames), 0, 6);
            c.killPunchKickPx = Mathf.Clamp((float)PnJson.Num(root, "killPunchKickPx", c.killPunchKickPx), 0f, 6f);
            return c;
        }
    }
}
