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
            return c;
        }
    }
}
