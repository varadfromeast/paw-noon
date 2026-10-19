using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>
    /// NTP-style clock offset / RTT estimator for future PvP sanity checks.
    /// Sample: client sends at t0 (client clock), server receives at t1 and replies at t2 (server clock),
    /// client receives at t3 (client clock).
    ///   offset = ((t1 - t0) + (t2 - t3)) / 2     (server = client + offset)
    ///   rtt    = (t3 - t0) - (t2 - t1)
    /// The estimate keeps the lowest-RTT half of the samples and takes the median offset of those
    /// (asymmetric path delay biases a single sample by (up - down) / 2; low-RTT samples bound it).
    /// </summary>
    public sealed class ClockSync
    {
        private readonly int capacity;
        private readonly List<long> offsets = new List<long>();
        private readonly List<long> rtts = new List<long>();

        public ClockSync(int capacity = 16)
        {
            this.capacity = Math.Max(1, capacity);
        }

        public int Count { get { return offsets.Count; } }

        public void AddSample(long t0, long t1, long t2, long t3)
        {
            long rtt = (t3 - t0) - (t2 - t1);
            if (rtt < 0) return; // impossible sample (clock jump) -> drop
            long offset = ((t1 - t0) + (t2 - t3)) / 2;
            offsets.Add(offset);
            rtts.Add(rtt);
            if (offsets.Count > capacity)
            {
                offsets.RemoveAt(0);
                rtts.RemoveAt(0);
            }
        }

        public void Clear() { offsets.Clear(); rtts.Clear(); }

        /// <summary>Returns false until at least one sample exists.</summary>
        public bool TryEstimate(out long offsetNs, out long rttNs)
        {
            offsetNs = 0;
            rttNs = 0;
            int n = offsets.Count;
            if (n == 0) return false;
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => rtts[a].CompareTo(rtts[b]));
            int keep = Math.Max(1, (n + 1) / 2);
            var best = new long[keep];
            var bestRtt = new long[keep];
            for (int i = 0; i < keep; i++)
            {
                best[i] = offsets[idx[i]];
                bestRtt[i] = rtts[idx[i]];
            }
            offsetNs = Median(best);
            rttNs = Median(bestRtt);
            return true;
        }

        public static long Median(long[] values)
        {
            if (values == null || values.Length == 0) return 0;
            var c = (long[])values.Clone();
            Array.Sort(c);
            int m = c.Length / 2;
            if ((c.Length & 1) == 1) return c[m];
            return (c[m - 1] + c[m]) / 2;
        }
    }
}
