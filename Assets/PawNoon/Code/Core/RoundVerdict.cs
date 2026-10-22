using System;
using System.Globalization;

namespace PawNoon.Core
{
    /// <summary>How one exchange went for one duelist (presentation: verdict line, streak, banter). Never feeds a rule.</summary>
    public enum ExchangeResult
    {
        /// <summary>Nothing to judge (void round, no record, nobody shot).</summary>
        None = 0,
        /// <summary>Hit first: shot, outside the tie window, and faster than the other cat (or he fouled / never shot).</summary>
        Won,
        /// <summary>The other cat hit first (or killed you before your shot).</summary>
        Lost,
        /// <summary>Both shots inside the tie window ("TOO CLOSE").</summary>
        Tie,
        /// <summary>Drew before DRAW (foul or anticipation).</summary>
        Foul,
        /// <summary>No swipe within the no-shot timeout.</summary>
        NoShot
    }

    /// <summary>The verdict headline's kind on the round card.</summary>
    public enum VerdictKind
    {
        None = 0,
        Faster,
        Beaten,
        DeadHeat,
        TooEarly,
        NoShot,
        /// <summary>You shot, the other cat fouled or never shot.</summary>
        CleanShot
    }

    /// <summary>Colour family of a verdict (the card maps it to its palette).</summary>
    public enum VerdictTone { Plain = 0, Good, Bad, Even }

    /// <summary>
    /// The round card's verdict headline, as text (engine-free, unit-tested). Three lengths per kind: the card
    /// measures them with its bitmap font and takes the first that fits ("YOU WERE FASTER  +12 ms" -> "FASTER +12 ms"
    /// -> "+12 ms"). The gap is whole milliseconds (rounded; one decimal under 1 ms).
    /// </summary>
    public static class RoundVerdict
    {
        public const int Long = 0, Short = 1, Minimal = 2;
        public const int Forms = 3;

        /// <summary>One exchange for <paramref name="slot"/>, from the authority's round record.</summary>
        public static ExchangeResult Classify(RoundRecord r, int slot)
        {
            if (r == null || r.isVoid || slot < 0 || slot > 1) return ExchangeResult.None;
            var me = r.slots[slot];
            var opp = r.slots[1 - slot];
            if (me.IsFoul) return ExchangeResult.Foul;
            if (me.outcome == SlotOutcome.NoShot) return ExchangeResult.NoShot;
            if (me.outcome == SlotOutcome.Stopped) return ExchangeResult.Lost;
            if (me.outcome != SlotOutcome.Shot) return ExchangeResult.None;
            if (r.isTie) return ExchangeResult.Tie;
            if (opp.outcome != SlotOutcome.Shot) return ExchangeResult.Won;   // he fouled, never shot, or was stopped
            if (me.reactionNs < opp.reactionNs) return ExchangeResult.Won;
            if (me.reactionNs > opp.reactionNs) return ExchangeResult.Lost;
            return ExchangeResult.Tie;
        }

        /// <summary>Verdict kind from the round card's values (youNs / botNs = -1 when there is no time).</summary>
        public static VerdictKind Kind(bool youFoul, bool youNoShot, bool tie, long youNs, long botNs)
        {
            if (youFoul) return VerdictKind.TooEarly;
            if (youNoShot) return VerdictKind.NoShot;
            if (tie) return VerdictKind.DeadHeat;
            if (youNs >= 0 && botNs >= 0) return youNs <= botNs ? VerdictKind.Faster : VerdictKind.Beaten;
            if (youNs >= 0) return VerdictKind.CleanShot;
            if (botNs >= 0) return VerdictKind.Beaten;   // shot down before your own swipe
            return VerdictKind.None;
        }

        public static VerdictTone Tone(VerdictKind k)
        {
            switch (k)
            {
                case VerdictKind.Faster:
                case VerdictKind.CleanShot: return VerdictTone.Good;
                case VerdictKind.Beaten:
                case VerdictKind.TooEarly:
                case VerdictKind.NoShot: return VerdictTone.Bad;
                case VerdictKind.DeadHeat: return VerdictTone.Even;
                default: return VerdictTone.Plain;
            }
        }

        /// <summary>|gap| in whole ms, rounded half up ("12"); under half a millisecond one decimal ("0.4"; "0" under 0.05 ms).</summary>
        public static string GapMs(long gapNs)
        {
            if (gapNs < 0) gapNs = -gapNs;
            long ms = (gapNs + 500000L) / 1000000L;
            if (ms >= 1) return ms.ToString(CultureInfo.InvariantCulture);
            long tenths = (gapNs + 50000L) / 100000L;   // 0..5 here (0.5 ms and up rounds to 1)
            if (tenths == 0) return "0";
            return "0." + tenths.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The headline in one of three lengths (Long / Short / Minimal). gapNs = |you - him| (any sign); hasGap false
        /// when either time is missing. plusMinus = the font's "±" if it has one, else "+-".
        /// </summary>
        public static string Text(VerdictKind k, int form, long gapNs, bool hasGap, string opponentName, string plusMinus)
        {
            if (form < Long) form = Long;
            if (form > Minimal) form = Minimal;
            string who = string.IsNullOrEmpty(opponentName) ? "HE" : opponentName.ToUpperInvariant();
            string pm = string.IsNullOrEmpty(plusMinus) ? "+-" : plusMinus;
            switch (k)
            {
                case VerdictKind.Faster:
                {
                    string g = hasGap ? "+" + GapMs(gapNs) + " ms" : "";
                    if (form == Long) return hasGap ? "YOU WERE FASTER  " + g : "YOU WERE FASTER";
                    if (form == Short) return hasGap ? "FASTER " + g : "FASTER";
                    return hasGap ? g : "FASTER";
                }
                case VerdictKind.Beaten:
                {
                    string g = hasGap ? "-" + GapMs(gapNs) + " ms" : "";
                    if (form == Long) return hasGap ? who + " BEAT YOU  " + g : who + " BEAT YOU";
                    if (form == Short) return hasGap ? "BEATEN " + g : "BEATEN";
                    return hasGap ? g : "BEATEN";
                }
                case VerdictKind.DeadHeat:
                {
                    string g = hasGap ? pm + GapMs(gapNs) + " ms" : "";
                    if (form == Long) return hasGap ? "DEAD HEAT  " + g : "DEAD HEAT";
                    if (form == Short) return hasGap ? "DEAD HEAT " + g : "DEAD HEAT";
                    return "DEAD HEAT";
                }
                case VerdictKind.TooEarly:
                    if (form == Long) return "TOO EARLY!  wait for DRAW";
                    if (form == Short) return "TOO EARLY! wait for DRAW";
                    return "TOO EARLY!";
                case VerdictKind.NoShot:
                    if (form == Long) return "NO SHOT  swipe up from the holster";
                    if (form == Short) return "NO SHOT  swipe up!";
                    return "NO SHOT";
                case VerdictKind.CleanShot:
                    return "CLEAN SHOT!";
                default:
                    return "";
            }
        }

        /// <summary>
        /// Picks the longest form whose measured width fits <paramref name="maxWidth"/> (Minimal when none does).
        /// measure = the card font's pixel width of a string. Allocates the candidate strings: call once per card.
        /// </summary>
        public static string Fit(VerdictKind k, long gapNs, bool hasGap, string opponentName, string plusMinus, int maxWidth, Func<string, int> measure)
        {
            string s = null;
            for (int f = Long; f <= Minimal; f++)
            {
                s = Text(k, f, gapNs, hasGap, opponentName, plusMinus);
                if (measure == null || measure(s) <= maxWidth) return s;
            }
            return s;
        }
    }
}
