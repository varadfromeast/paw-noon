using System;

namespace PawNoon.Core
{
    public enum Grade
    {
        None = 0,
        Lightning,
        Perfect,
        Great,
        Good,
        Slow,
        Slowpoke
    }

    /// <summary>Continuous piecewise-linear damage from reaction time (SPEC §1).</summary>
    public static class DamageCurve
    {
        public static double Evaluate(DuelConfig cfg, double reactionMs)
        {
            var xs = cfg.damageMs;
            var ys = cfg.damageValue;
            int n = Math.Min(xs.Length, ys.Length);
            if (n == 0) return 0;
            if (double.IsNaN(reactionMs) || reactionMs < 0) return 0;
            if (reactionMs <= xs[0]) return ys[0];
            if (reactionMs > xs[n - 1]) return 0;
            for (int k = 1; k < n; k++)
            {
                if (reactionMs <= xs[k])
                {
                    double span = xs[k] - xs[k - 1];
                    double t = span <= 0 ? 1.0 : (reactionMs - xs[k - 1]) / span;
                    return ys[k - 1] + (ys[k] - ys[k - 1]) * t;
                }
            }
            return ys[n - 1];
        }
    }

    public static class Grading
    {
        public static Grade FromMs(DuelConfig cfg, double reactionMs)
        {
            if (reactionMs <= cfg.gradeLightningMs) return Grade.Lightning;
            if (reactionMs <= cfg.gradePerfectMs) return Grade.Perfect;
            if (reactionMs <= cfg.gradeGreatMs) return Grade.Great;
            if (reactionMs <= cfg.gradeGoodMs) return Grade.Good;
            if (reactionMs <= cfg.gradeSlowMs) return Grade.Slow;
            return Grade.Slowpoke;
        }

        public static string Label(Grade g)
        {
            switch (g)
            {
                case Grade.Lightning: return "LIGHTNING!";
                case Grade.Perfect: return "PERFECT!";
                case Grade.Great: return "GREAT!";
                case Grade.Good: return "GOOD";
                case Grade.Slow: return "SLOW";
                case Grade.Slowpoke: return "SLOWPOKE";
                default: return "";
            }
        }
    }
}
