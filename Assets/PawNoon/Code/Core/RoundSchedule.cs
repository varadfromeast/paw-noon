using System;

namespace PawNoon.Core
{
    /// <summary>
    /// The authority's private plan for one round: countdown beats then a random hold.
    /// Times are offsets from the countdown start (the moment "3" appears). Never sent to clients.
    /// </summary>
    public sealed class RoundSchedule
    {
        public readonly int Beats;
        public readonly long BeatNs;
        public readonly long HoldNs;

        public RoundSchedule(int beats, long beatNs, long holdNs)
        {
            Beats = beats;
            BeatNs = beatNs;
            HoldNs = holdNs;
        }

        public long CountdownNs { get { return Beats * BeatNs; } }

        /// <summary>Offset from countdown start at which the cue is due.</summary>
        public long CueOffsetNs { get { return CountdownNs + HoldNs; } }

        public static RoundSchedule Create(DuelConfig cfg, Random rng)
        {
            double h = cfg.holdMinSeconds + rng.NextDouble() * (cfg.holdMaxSeconds - cfg.holdMinSeconds);
            return new RoundSchedule(cfg.countdownBeats, cfg.BeatNs, DuelConfig.SecToNs(h));
        }

        /// <summary>Which countdown numeral is showing at an offset (Beats..1), or 0 once the countdown is over.</summary>
        public static int NumeralAt(int beats, long beatNs, long offsetNs)
        {
            if (offsetNs < 0) return beats;
            long idx = offsetNs / Math.Max(1L, beatNs);
            if (idx >= beats) return 0;
            return beats - (int)idx;
        }
    }
}
