namespace PawNoon.Core
{
    /// <summary>
    /// Sudden death "LAST LIGHT" (SPEC_V2 §8.4): when BOTH duelists are alive and at or below
    /// <see cref="DuelConfig.suddenDeathHp"/> as a standoff starts, that round's damage is multiplied by
    /// <see cref="DuelConfig.suddenDeathDamageMultiplier"/> (x2).
    ///
    /// The rule is evaluated by the match driver before each countdown (DuelDirector offline; the recorded
    /// match driver on the server, supabase/functions/_shared/duel/recorded_match.ts) and handed to
    /// <see cref="LocalAuthority.SetDamageMultiplier"/>; the authority applies it exactly once per shot.
    /// Void replays re-evaluate it from the same HP, so they get the same multiplier.
    /// </summary>
    public static class SuddenDeath
    {
        public static bool Triggers(DuelConfig cfg, double hp0, double hp1)
        {
            if (cfg == null || !cfg.suddenDeathEnabled) return false;
            return hp0 > 0 && hp1 > 0 && hp0 <= cfg.suddenDeathHp && hp1 <= cfg.suddenDeathHp;
        }

        public static bool Triggers(DuelConfig cfg, MatchState m)
        {
            return m != null && !m.Over && Triggers(cfg, m.Hp[0], m.Hp[1]);
        }

        /// <summary>Damage multiplier for the next round of this match: suddenDeathDamageMultiplier or 1.</summary>
        public static double MultiplierFor(DuelConfig cfg, MatchState m)
        {
            return Triggers(cfg, m) ? cfg.suddenDeathDamageMultiplier : 1.0;
        }
    }
}
