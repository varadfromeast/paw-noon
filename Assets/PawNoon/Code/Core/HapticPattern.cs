namespace PawNoon.Core
{
    /// <summary>
    /// Haptic pulse codes used in patterns. 0..2 = impact (light / medium / heavy, as PawHaptics.Impact),
    /// 10..12 = notification (success / warning / error, as PawHaptics.Notify).
    /// </summary>
    public static class HapticStyle
    {
        public const int Light = 0;
        public const int Medium = 1;
        public const int Heavy = 2;
        public const int Success = 10;
        public const int Warning = 11;
        public const int Error = 12;

        public static bool IsNotify(int code) { return code >= Success; }
        /// <summary>Notify type 0/1/2 for a notification code.</summary>
        public static int NotifyType(int code) { return code - Success; }
    }

    /// <summary>
    /// The result-stamp haptic language (SPEC_V2 §8.6). Patterns are { pulse, gapMs, pulse, gapMs, pulse... }.
    /// Static arrays: choosing one never allocates.
    /// </summary>
    public static class StampHaptics
    {
        /// <summary>Triple tap: heavy, heavy, heavy, 65 ms apart (iOS feedback generators need ~50 ms to re-arm).</summary>
        public static readonly int[] Lightning = { HapticStyle.Heavy, 65, HapticStyle.Heavy, 65, HapticStyle.Heavy };
        /// <summary>Double tap: medium then heavy, 85 ms apart.</summary>
        public static readonly int[] Perfect = { HapticStyle.Medium, 85, HapticStyle.Heavy };
        public static readonly int[] Great = { HapticStyle.Medium };
        /// <summary>GOOD, SLOW and SLOWPOKE.</summary>
        public static readonly int[] Plain = { HapticStyle.Light };
        public static readonly int[] TooEarly = { HapticStyle.Error };
        public static readonly int[] NoShot = { HapticStyle.Warning };

        /// <summary>Pattern for the stamp a round result shows (foul wins over no-shot wins over the grade); null = no stamp.</summary>
        public static int[] For(bool foul, bool noShot, Grade grade)
        {
            if (foul) return TooEarly;
            if (noShot) return NoShot;
            switch (grade)
            {
                case Grade.Lightning: return Lightning;
                case Grade.Perfect: return Perfect;
                case Grade.Great: return Great;
                case Grade.Good:
                case Grade.Slow:
                case Grade.Slowpoke: return Plain;
                default: return null;
            }
        }

        /// <summary>Number of pulses in a pattern (even indices).</summary>
        public static int Pulses(int[] pattern)
        {
            return pattern == null ? 0 : (pattern.Length + 1) / 2;
        }
    }

    /// <summary>
    /// Plays a haptic pattern over time without coroutines or allocations: the owner advances it by the frame's
    /// delta and polls for due pulses. Gaps accumulate (an overshoot shortens the next gap), so a pattern keeps its
    /// rhythm at any frame rate; a gap shorter than a frame fires both pulses on the same frame.
    /// Pure C# (no Unity), unit-tested.
    /// </summary>
    public sealed class HapticSequencer
    {
        private int[] pattern;
        private int index;
        private double waitMs;
        private int limit;

        /// <summary>True while pulses remain.</summary>
        public bool Active { get { return pattern != null && index < limit; } }

        /// <summary>Starts a pattern (replaces any running one). firstOnly = play just the first pulse (Reduced Motion).</summary>
        public void Start(int[] stylesWithGapsMs, bool firstOnly = false)
        {
            pattern = stylesWithGapsMs;
            index = 0;
            waitMs = 0;
            limit = pattern == null ? 0 : (firstOnly ? (pattern.Length > 0 ? 1 : 0) : pattern.Length);
        }

        public void Cancel()
        {
            pattern = null;
            index = 0;
            limit = 0;
            waitMs = 0;
        }

        /// <summary>Moves time forward by dtMs (negative is ignored).</summary>
        public void Advance(double dtMs)
        {
            if (Active && dtMs > 0) waitMs -= dtMs;
        }

        /// <summary>Returns the next due pulse code, or -1 when nothing is due. Call until it returns -1.</summary>
        public int Poll()
        {
            if (!Active || waitMs > 0) return -1;
            int code = pattern[index++];
            if (index < limit)
            {
                int gap = pattern[index++];
                waitMs += gap > 0 ? gap : 0;
            }
            return code;
        }
    }
}
