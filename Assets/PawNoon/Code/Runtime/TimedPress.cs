using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>One draw press from SwipeDraw's queue (the file used to hold the old PressInput reader).</summary>
    public struct TimedPress
    {
        /// <summary>Press (draw-threshold crossing) time on the Stopwatch duel clock (ns).</summary>
        public long tNs;
        public InputSource source;
        /// <summary>True when the timestamp is only the frame start (legacy input fallback).</summary>
        public bool frameQuantized;
    }
}
