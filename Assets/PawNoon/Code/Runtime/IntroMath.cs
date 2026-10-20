using System;

namespace PawNoon.Runtime
{
    /// <summary>One frame of the match intro: whole-pixel offsets from the actors' rest marks (canvas, y down).</summary>
    public struct IntroPose
    {
        /// <summary>Hero: the 2-frame settle only (he never leaves his title position horizontally).</summary>
        public int heroDy;
        public int oppDx, oppDy;
        /// <summary>The hero has settled into his stance (switch idle to ready).</summary>
        public bool heroReady;
    }

    /// <summary>
    /// Timeline of the match intro (SPEC_V2 §8.7, feel wave 2), engine-free so EditMode tests can pin it.
    /// The intro starts from the title positions: on frame 0 every offset is 0, so nothing jumps when DUEL is
    /// tapped. The hero stays and settles (2 px, 1 px, rest). The opponent ("walk" mode) steps out past the right
    /// edge of the view, holds a beat off screen and walks back in to his mark; "stay" mode settles him like the
    /// hero. All motion is time-quantised to <see cref="StepHz"/> and rounded to whole pixels.
    /// </summary>
    public static class IntroMath
    {
        public const float StepHz = 12f;
        /// <summary>Hero settle: +2 px, then +1 px, one step each, starting at <see cref="SettleAt"/>.</summary>
        public const float SettleAt = 0.08f;
        public static readonly int[] Settle = { 2, 1 };
        public const float SettleStep = 1f / StepHz;
        /// <summary>Opponent out (ease-in quad, 5 steps) and back in (ease-out cubic, 7 steps).</summary>
        public const float OutFrom = 0.00f, OutTo = 0.42f;
        public const float InFrom = 0.50f, InTo = 1.10f;
        /// <summary>"stay" mode: the opponent settles a little after the hero.</summary>
        public const float OppSettleAt = 0.20f;
        public const float HeroPlateAt = 1.14f, OppPlateAt = 1.28f;
        public const float Total = 1.66f;
        public const float RmHeroPlateAt = 0.20f, RmOppPlateAt = 0.40f, RmTotal = 0.90f;
        /// <summary>Steps past the view edge so the whole cell is out of sight.</summary>
        public const int ExitMargin = 2;

        public static float HeroPlate(bool reduced) { return reduced ? RmHeroPlateAt : HeroPlateAt; }
        public static float OppPlate(bool reduced) { return reduced ? RmOppPlateAt : OppPlateAt; }
        public static float Duration(bool reduced) { return reduced ? RmTotal : Total; }

        /// <summary>
        /// How far right the opponent must step so his whole cell is past the visible right edge:
        /// visibleRight - cellLeft + ExitMargin, never negative.
        /// </summary>
        public static int ExitDistance(int visibleRight, int cellLeft)
        {
            return Math.Max(0, visibleRight - cellLeft) + ExitMargin;
        }

        /// <summary>Quantised progress 0..1 of [from, to] at time t (0 before, 1 after), StepHz steps.</summary>
        public static float Steps(float t, float from, float to)
        {
            if (t <= from) return 0f;
            if (t >= to) return 1f;
            float steps = Math.Max(1f, (float)Math.Round((to - from) * StepHz));
            return (float)Math.Floor((t - from) / (to - from) * steps) / steps;
        }

        /// <summary>Index of the current step inside [from, to) (for the walk bob), -1 outside.</summary>
        public static int StepIndex(float t, float from, float to)
        {
            if (t < from || t >= to) return -1;
            float steps = Math.Max(1f, (float)Math.Round((to - from) * StepHz));
            return (int)Math.Floor((t - from) / (to - from) * steps);
        }

        private static int SettleDy(float t, float at)
        {
            if (t < at) return 0;
            int i = (int)Math.Floor((t - at) / SettleStep);
            return i >= 0 && i < Settle.Length ? Settle[i] : 0;
        }

        private static float InQuad(float k) { return k * k; }

        private static float OutCubic(float k) { k -= 1f; return k * k * k + 1f; }

        /// <summary>The pose at intro time t. exitDx = <see cref="ExitDistance"/>; reduced = Reduced Motion (no motion).</summary>
        public static IntroPose Sample(float t, bool reduced, bool opponentWalks, int exitDx)
        {
            var p = new IntroPose();
            if (reduced)
            {
                p.heroReady = true;
                return p;
            }
            p.heroDy = SettleDy(t, SettleAt);
            p.heroReady = t >= SettleAt + Settle.Length * SettleStep;
            if (!opponentWalks)
            {
                p.oppDy = SettleDy(t, OppSettleAt);
                return p;
            }
            if (exitDx < 0) exitDx = 0;
            float k;
            int step;
            if (t < InFrom)
            {
                k = InQuad(Steps(t, OutFrom, OutTo));
                step = StepIndex(t, OutFrom, OutTo);
            }
            else
            {
                k = 1f - OutCubic(Steps(t, InFrom, InTo));
                step = StepIndex(t, InFrom, InTo);
            }
            p.oppDx = (int)Math.Round(exitDx * k);
            // walking bob: 1 px up on every other step while he moves (never on the first or resting frame)
            p.oppDy = step > 0 && (step & 1) == 1 && p.oppDx > 0 && p.oppDx < exitDx ? -1 : 0;
            return p;
        }
    }
}
