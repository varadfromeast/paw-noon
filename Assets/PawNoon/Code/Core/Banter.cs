using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>What the opponent cat is reacting to when he speaks (presentation only; never touches the rules).</summary>
    public enum BanterContext
    {
        None = 0,
        MatchStart,
        LastOne,
        Lightning,
        PlayerEarly,
        BotWonRound,
        BotLostRound,
        MatchWin,
        /// <summary>The player has won several exchanges in a row (StreakCounter >= the rattled threshold).</summary>
        Rattled
    }

    /// <summary>
    /// One character's one-liners per context (keys: matchStart, lastOne, lightning, playerEarly, botWon,
    /// botLost, matchWin, rattled; "bot" = the speaker, "player" = the cat across the street). The field defaults are
    /// Pawtruth's lines; <see cref="WhiskersDefaults"/> are Whiskers'. Data lives in
    /// Resources/Data/characters.json (see <see cref="CharacterRoster"/>); a context the file does not define
    /// falls back to these built-ins.
    /// </summary>
    [Serializable]
    public sealed class BanterLines
    {
        public string[] matchStart = { "Yer hand's shakin', kid.", "This town's too small, kid.", "Draw when yer ready." };
        public string[] lastOne = { "Last one, pardner.", "One more and we're done.", "This one counts, kid." };
        public string[] lightning = { "...Who taught you that?", "Huh. Quick paws.", "Well, I'll be." };
        public string[] playerEarly = { "Jumpy, ain't ya?", "Easy on that trigger.", "Wait for it, kid." };
        public string[] botWon = { "Too slow, pardner.", "Keep up, kid.", "That all ya got?" };
        public string[] botLost = { "Lucky shot.", "Beginner's luck.", "Won't happen twice." };
        public string[] matchWin = { "Sleep tight, kid.", "Come back with more whiskers." };
        public string[] rattled = { "Now hold on a minute...", "Lucky streak. Won't last.", "Who ARE you, kid?" };

        public string[] For(BanterContext c)
        {
            switch (c)
            {
                case BanterContext.MatchStart: return matchStart;
                case BanterContext.LastOne: return lastOne;
                case BanterContext.Lightning: return lightning;
                case BanterContext.PlayerEarly: return playerEarly;
                case BanterContext.BotWonRound: return botWon;
                case BanterContext.BotLostRound: return botLost;
                case BanterContext.MatchWin: return matchWin;
                case BanterContext.Rattled: return rattled;
                default: return null;
            }
        }

        /// <summary>Whiskers' built-in lines (used when characters.json is missing or has no whiskers entry).</summary>
        public static BanterLines WhiskersDefaults()
        {
            var b = new BanterLines();
            b.matchStart = new[] { "Nice hat. Shame about it.", "I'm faster than I look.", "Noon's my nap time. Hurry." };
            b.lastOne = new[] { "One left. Make it count.", "Last one. No pressure.", "Down to whiskers now." };
            b.lightning = new[] { "Okay. That was fast.", "Rude. Do it slower.", "Who sharpened your claws?" };
            b.playerEarly = new[] { "The bell's not a suggestion.", "Somebody's jumpy.", "Wait for the word, big cat." };
            b.botWon = new[] { "Blink and you miss me.", "Too slow, shadow.", "I'd say sorry. I won't." };
            b.botLost = new[] { "Fine. Lucky paw.", "That one tickled.", "Noted. Won't happen again." };
            b.matchWin = new[] { "Go home, shadow.", "Tell 'em a white cat did it." };
            b.rattled = new[] { "Okay. Okay. Breathe.", "Stop doing that.", "My tail's twitching. Rude." };
            return b;
        }

        /// <summary>Reads a {"matchStart": [...], ...} object; missing / empty lists keep the defaults.</summary>
        public static BanterLines FromJson(object node)
        {
            return FromJson(node, null);
        }

        /// <summary>
        /// Reads a {"matchStart": [...], ...} object; a missing / empty list takes that context from
        /// <paramref name="fallback"/> (null = the built-in Pawtruth defaults).
        /// </summary>
        public static BanterLines FromJson(object node, BanterLines fallback)
        {
            var b = new BanterLines();
            if (fallback != null)
            {
                b.matchStart = fallback.matchStart;
                b.lastOne = fallback.lastOne;
                b.lightning = fallback.lightning;
                b.playerEarly = fallback.playerEarly;
                b.botWon = fallback.botWon;
                b.botLost = fallback.botLost;
                b.matchWin = fallback.matchWin;
                b.rattled = fallback.rattled;
            }
            if (node == null) return b;
            b.matchStart = Strings(node, "matchStart", b.matchStart);
            b.lastOne = Strings(node, "lastOne", b.lastOne);
            b.lightning = Strings(node, "lightning", b.lightning);
            b.playerEarly = Strings(node, "playerEarly", b.playerEarly);
            b.botWon = Strings(node, "botWon", b.botWon);
            b.botLost = Strings(node, "botLost", b.botLost);
            b.matchWin = Strings(node, "matchWin", b.matchWin);
            b.rattled = Strings(node, "rattled", b.rattled);
            return b;
        }

        private static string[] Strings(object node, string key, string[] def)
        {
            var arr = PnJson.Arr(node, key);
            if (arr == null) return def;
            var l = new List<string>(arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                var s = arr[i] as string;
                if (!string.IsNullOrEmpty(s)) l.Add(s);
            }
            return l.Count > 0 ? l.ToArray() : def;
        }
    }

    /// <summary>
    /// Chooses whether the opponent cat talks before a round and what he says. Seeded, allocation-free per pick,
    /// never repeats the previous line (when the pool has another one).
    /// </summary>
    public sealed class BanterPicker
    {
        private Random rng;
        private string last;

        public BanterPicker(int seed) { rng = new Random(seed); }

        /// <summary>Reseeds the roll; the no-repeat memory is kept across matches.</summary>
        public void Reseed(int seed) { rng = new Random(seed); }

        public string LastLine { get { return last; } }

        /// <summary>Contexts that always speak (no chance roll).</summary>
        public static bool Always(BanterContext c)
        {
            return c == BanterContext.MatchStart || c == BanterContext.LastOne || c == BanterContext.MatchWin || c == BanterContext.Rattled;
        }

        /// <summary>
        /// Context for the standoff before a round, by priority: match start, either duelist under lowHp
        /// ("last one"), player's LIGHTNING last round, player drew early last round, the opponent won / lost
        /// the last round. lastWinner -1 (tie / no result) gives no win/lose context.
        /// </summary>
        public static BanterContext Context(bool matchStart, double hp0, double hp1, double lowHp,
            bool playerLightning, bool playerEarly, int lastWinner)
        {
            return Context(matchStart, hp0, hp1, lowHp, playerLightning, playerEarly, lastWinner, 0, 0);
        }

        /// <summary>
        /// As above, with the player's win streak: at <paramref name="rattledStreak"/> or more wins in a row (0 = off)
        /// the opponent is rattled, which outranks everything but the match start (a 3-win run usually also means he is
        /// under lowHp, and "rattled" is the rarer, better line).
        /// </summary>
        public static BanterContext Context(bool matchStart, double hp0, double hp1, double lowHp,
            bool playerLightning, bool playerEarly, int lastWinner, int playerStreak, int rattledStreak)
        {
            if (matchStart) return BanterContext.MatchStart;
            if (rattledStreak > 0 && playerStreak >= rattledStreak) return BanterContext.Rattled;
            if ((hp0 > 0 && hp0 < lowHp) || (hp1 > 0 && hp1 < lowHp)) return BanterContext.LastOne;
            if (playerLightning) return BanterContext.Lightning;
            if (playerEarly) return BanterContext.PlayerEarly;
            if (lastWinner == 1) return BanterContext.BotWonRound;
            if (lastWinner == 0) return BanterContext.BotLostRound;
            return BanterContext.None;
        }

        /// <summary>Returns a line, or null when he keeps quiet (no context, empty pool or failed chance roll).</summary>
        public string Pick(BanterLines lines, BanterContext c, double chance)
        {
            if (lines == null || c == BanterContext.None) return null;
            var pool = lines.For(c);
            if (pool == null || pool.Length == 0) return null;
            if (!Always(c) && rng.NextDouble() >= chance) return null;
            return Line(pool);
        }

        /// <summary>A random line from the pool that differs from the previous line whenever possible.</summary>
        public string Line(string[] pool)
        {
            if (pool == null || pool.Length == 0) return null;
            int n = 0;
            for (int i = 0; i < pool.Length; i++) if (pool[i] != last) n++;
            string pick;
            if (n == 0) pick = pool[rng.Next(pool.Length)];
            else
            {
                int k = rng.Next(n);
                pick = null;
                for (int i = 0; i < pool.Length; i++)
                {
                    if (pool[i] == last) continue;
                    if (k-- == 0) { pick = pool[i]; break; }
                }
            }
            last = pick;
            return pick;
        }
    }

    /// <summary>Integer canvas rect (y down), engine-free so bubble layout is testable in Core.</summary>
    public struct PxRect
    {
        public int x, y, w, h;
        public PxRect(int x, int y, int w, int h) { this.x = x; this.y = y; this.w = w; this.h = h; }
        public int xMax { get { return x + w; } }
        public int yMax { get { return y + h; } }
    }

    /// <summary>
    /// Speech bubble geometry: 9-slice body sized to the text + Pad on every side, and a tail of TailSteps
    /// stacked rectangles (each TailStepH px tall, 1 px narrower than the one above, sliding toward the
    /// speaker) whose tip lands on the anchor. The body is clamped inside bounds (margin) and never above minY.
    /// </summary>
    public static class BubbleGeometry
    {
        public const int Pad = 6;
        public const int TailSteps = 3;
        public const int TailStepH = 2;
        public const int TailBaseW = 6;
        public const int MinW = 24;
        /// <summary>Tail rows below the body (the first step overlaps the body's bottom ink row by 1 px).</summary>
        public const int TailBelow = TailSteps * TailStepH;

        /// <summary>
        /// Lays out the body for a text block; tailX is the reference x of the tail (see Step).
        /// tailLeft = tail near the body's left end, leaning left (bubble sits to the right of the speaker).
        /// </summary>
        public static PxRect Layout(int textW, int textH, int anchorX, int anchorY, bool tailLeft, PxRect bounds, int minY, int margin, out int tailX)
        {
            int w = Math.Max(MinW, textW + Pad * 2);
            int h = textH + Pad * 2;
            int x = tailLeft ? anchorX - 6 : anchorX + 6 - w;
            int y = anchorY - TailBelow - h;
            int top = Math.Max(bounds.y + margin, minY);
            int bottom = bounds.yMax - margin - TailBelow;
            if (x + w > bounds.xMax - margin) x = bounds.xMax - margin - w;
            if (x < bounds.x + margin) x = bounds.x + margin;
            if (y + h > bottom) y = bottom - h;
            if (y < top) y = top;
            var r = new PxRect(x, y, w, h);
            int ideal = tailLeft ? anchorX : anchorX - 6;
            int lo = tailLeft ? r.x + 5 : r.x + 3;
            int hi = tailLeft ? r.xMax - 9 : r.xMax - 11;
            tailX = ideal < lo ? lo : (ideal > hi ? hi : ideal);
            return r;
        }

        /// <summary>Outer (ink) rect of tail step k (0 = at the body). The paper fill is this rect inset 1 px left/right
        /// (and 1 px from the bottom on the last step).</summary>
        public static PxRect Step(int k, bool tailLeft, int tailX, int bodyBottom)
        {
            int w = TailBaseW - k;
            int x = tailLeft ? tailX - k : tailX + 2 * k;
            int y = bodyBottom - 1 + k * TailStepH;
            int h = k == TailSteps - 1 ? TailStepH + 1 : TailStepH;
            return new PxRect(x, y, w, h);
        }
    }
}
