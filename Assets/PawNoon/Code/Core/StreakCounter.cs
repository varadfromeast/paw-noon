namespace PawNoon.Core
{
    /// <summary>
    /// Consecutive exchanges a duelist won outright within one match (presentation only: the streak tag on the round
    /// card, "ON A ROLL", the opponent's rattled banter). A win adds one; a loss, a foul or a no-shot resets to zero;
    /// a tie (TOO CLOSE) or a void leaves it as it is. Engine-free, unit-tested.
    /// </summary>
    public sealed class StreakCounter
    {
        private int current;
        private int best;

        /// <summary>Wins in a row right now.</summary>
        public int Current { get { return current; } }

        /// <summary>Longest run this match.</summary>
        public int Best { get { return best; } }

        /// <summary>New match.</summary>
        public void Reset()
        {
            current = 0;
            best = 0;
        }

        /// <summary>Records one exchange and returns the streak after it.</summary>
        public int Record(ExchangeResult r)
        {
            switch (r)
            {
                case ExchangeResult.Won:
                    current++;
                    if (current > best) best = current;
                    break;
                case ExchangeResult.Lost:
                case ExchangeResult.Foul:
                case ExchangeResult.NoShot:
                    current = 0;
                    break;
            }
            return current;
        }

        /// <summary>True when an exchange that left <paramref name="streak"/> deserves the tag (min &lt;= 0 = never).</summary>
        public static bool ShowsTag(ExchangeResult r, int streak, int min)
        {
            return r == ExchangeResult.Won && min > 0 && streak >= min;
        }
    }
}
