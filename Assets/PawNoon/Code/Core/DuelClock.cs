using System.Diagnostics;

namespace PawNoon.Core
{
    /// <summary>Monotonic clock in integer nanoseconds. All core timestamps live on one of these.</summary>
    public interface IDuelClock
    {
        long NowNs { get; }
    }

    /// <summary>Exact integer conversions (no float accumulation).</summary>
    public static class TimeConv
    {
        public const long NsPerSecond = 1000000000L;

        /// <summary>ticks * 1e9 / frequency without overflow and without floating point.</summary>
        public static long TicksToNs(long ticks, long frequency)
        {
            if (frequency <= 0) return 0;
            if (frequency == NsPerSecond) return ticks;
            long whole = ticks / frequency;
            long rem = ticks % frequency;
            return whole * NsPerSecond + (rem * NsPerSecond) / frequency;
        }

        /// <summary>Seconds (double, e.g. an OS event time) to ns, rounded to nearest.</summary>
        public static long SecondsToNs(double seconds)
        {
            return (long)System.Math.Round(seconds * 1e9);
        }

        public static double NsToMs(long ns) { return ns / 1e6; }
        public static double NsToSeconds(long ns) { return ns / 1e9; }
    }

    /// <summary>Stopwatch.GetTimestamp based clock (QueryPerformanceCounter / mach_absolute_time /
    /// clock_gettime(MONOTONIC) underneath). Origin = construction time.</summary>
    public sealed class StopwatchClock : IDuelClock
    {
        private readonly long origin;
        private readonly long freq;

        public StopwatchClock()
        {
            freq = Stopwatch.Frequency;
            origin = Stopwatch.GetTimestamp();
        }

        public long NowNs
        {
            get { return TimeConv.TicksToNs(Stopwatch.GetTimestamp() - origin, freq); }
        }
    }

    /// <summary>Test / replay clock.</summary>
    public sealed class ManualClock : IDuelClock
    {
        public long Now;
        public long NowNs { get { return Now; } }
        public void AdvanceMs(double ms) { Now += DuelConfig.MsToNs(ms); }
    }
}
