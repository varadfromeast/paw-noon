using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Settings and all-time best, persisted with PlayerPrefs under the "pawnoon." namespace.
    /// First launch: music on, sfx on, haptics on, reduced motion off, music style Original.
    /// Keys from early builds ("pn.*") are migrated once.
    /// </summary>
    public static class PawSettings
    {
        private const string KMusic = "pawnoon.music";
        private const string KClassic = "pawnoon.classicMusic";
        private const string KSfx = "pawnoon.sfx";
        private const string KHaptics = "pawnoon.haptics";
        private const string KReduced = "pawnoon.reducedMotion";
        private const string KBest = "pawnoon.bestNs";
        /// <summary>Difficulty ("easy" / "hard", SPEC_V2 §1). Absent until the player picks on first launch.</summary>
        private const string KDifficulty = "pn_difficulty";

        public static bool Music = true;
        public static bool ClassicMusic;
        public static bool Sfx = true;
        public static bool Haptics = true;
        public static bool ReducedMotion;
        /// <summary>All-time best reaction in ns, or -1.</summary>
        public static long BestNs = -1;
        /// <summary>"easy" or "hard" (hard until chosen).</summary>
        public static string Difficulty = DuelConfig.DifficultyHard;
        /// <summary>False on first launch: the title shows the one-time EASY / HARD card.</summary>
        public static bool DifficultyChosen;

        private static int Int(string key, string legacyKey, int def)
        {
            if (PlayerPrefs.HasKey(key)) return PlayerPrefs.GetInt(key, def);
            if (PlayerPrefs.HasKey(legacyKey)) return PlayerPrefs.GetInt(legacyKey, def);
            return def;
        }

        public static void Load()
        {
            // defaults first, so a storage failure still leaves sane values
            Music = true; ClassicMusic = false; Sfx = true; Haptics = true; ReducedMotion = false; BestNs = -1;
            Difficulty = DuelConfig.DifficultyHard; DifficultyChosen = false;
            try
            {
                Music = Int(KMusic, "pn.music", 1) == 1;
                ClassicMusic = Int(KClassic, "pn.classic", 0) == 1;
                Sfx = Int(KSfx, "pn.sfx", 1) == 1;
                Haptics = Int(KHaptics, "pn.haptics", 1) == 1;
                ReducedMotion = Int(KReduced, "pn.reducedMotion", 0) == 1;
                string best = PlayerPrefs.HasKey(KBest) ? PlayerPrefs.GetString(KBest, "-1") : PlayerPrefs.GetString("pn.bestNs", "-1");
                long v;
                BestNs = long.TryParse(best, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 ? v : -1;
                DifficultyChosen = PlayerPrefs.HasKey(KDifficulty);
                Difficulty = DuelConfig.NormalizeDifficulty(PlayerPrefs.GetString(KDifficulty, DuelConfig.DifficultyHard));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] settings load failed, using defaults: " + e.Message);
            }
        }

        public static void Save()
        {
            try
            {
                PlayerPrefs.SetInt(KMusic, Music ? 1 : 0);
                PlayerPrefs.SetInt(KClassic, ClassicMusic ? 1 : 0);
                PlayerPrefs.SetInt(KSfx, Sfx ? 1 : 0);
                PlayerPrefs.SetInt(KHaptics, Haptics ? 1 : 0);
                PlayerPrefs.SetInt(KReduced, ReducedMotion ? 1 : 0);
                PlayerPrefs.SetString(KBest, BestNs.ToString(CultureInfo.InvariantCulture));
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] settings save failed: " + e.Message);
            }
        }

        /// <summary>Stores the difficulty (marks the first-launch choice as made).</summary>
        public static void SaveDifficulty(string d)
        {
            Difficulty = DuelConfig.NormalizeDifficulty(d);
            DifficultyChosen = true;
            try
            {
                PlayerPrefs.SetString(KDifficulty, Difficulty);
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] difficulty save failed: " + e.Message);
            }
        }

        /// <summary>Forgets the difficulty so the next title shows the first-launch choice again (review / QA).</summary>
        public static void ClearDifficulty()
        {
            Difficulty = DuelConfig.DifficultyHard;
            DifficultyChosen = false;
            try
            {
                PlayerPrefs.DeleteKey(KDifficulty);
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] difficulty clear failed: " + e.Message);
            }
        }

        /// <summary>Records a reaction; returns true if it is a new all-time best.</summary>
        public static bool SubmitBest(long ns)
        {
            if (ns <= 0) return false;
            if (BestNs < 0 || ns < BestNs)
            {
                BestNs = ns;
                Save();
                return true;
            }
            return false;
        }
    }
}
