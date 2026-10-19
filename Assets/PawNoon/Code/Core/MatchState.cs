using System.Collections.Generic;

namespace PawNoon.Core
{
    public sealed class MatchState
    {
        public const int Slots = 2;

        public readonly double[] Hp = new double[Slots];
        public readonly int[] RoundWins = new int[Slots];
        public double MaxHp;
        /// <summary>1-based number of the round being played (void rounds replay the same number).</summary>
        public int RoundNumber;
        public bool Over;
        /// <summary>Winning slot, or -1 when not over or when the match is a draw (both fell in one tie).</summary>
        public int Winner = -1;

        /// <summary>Over with no winner: both duelists went down on the same tied round (double KO).</summary>
        public bool IsDraw { get { return Over && Winner < 0; } }

        /// <summary>Ends the match. winner = slot, or -1 for a draw.</summary>
        public void Finish(int winner)
        {
            Over = true;
            Winner = winner >= 0 && winner < Slots ? winner : -1;
        }

        private readonly List<long>[] reactions = { new List<long>(16), new List<long>(16) };

        public MatchState(double maxHp)
        {
            Reset(maxHp);
        }

        public void Reset(double maxHp)
        {
            MaxHp = maxHp;
            for (int i = 0; i < Slots; i++)
            {
                Hp[i] = maxHp;
                RoundWins[i] = 0;
                reactions[i].Clear();
            }
            RoundNumber = 1;
            Over = false;
            Winner = -1;
        }

        public void RecordReaction(int slot, long reactionNs)
        {
            if (slot < 0 || slot >= Slots) return;
            reactions[slot].Add(reactionNs);
        }

        public int ReactionCount(int slot) { return reactions[slot].Count; }

        /// <summary>Best (lowest) valid reaction this match, or -1.</summary>
        public long BestNs(int slot)
        {
            var l = reactions[slot];
            long best = -1;
            for (int i = 0; i < l.Count; i++)
                if (best < 0 || l[i] < best) best = l[i];
            return best;
        }

        /// <summary>Average valid reaction this match, or -1.</summary>
        public long AverageNs(int slot)
        {
            var l = reactions[slot];
            if (l.Count == 0) return -1;
            long sum = 0;
            for (int i = 0; i < l.Count; i++) sum += l[i];
            return sum / l.Count;
        }

        public double HpFraction(int slot)
        {
            return MaxHp <= 0 ? 0 : Hp[slot] / MaxHp;
        }
    }
}
