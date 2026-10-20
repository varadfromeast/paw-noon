using System;
using System.Collections.Generic;
using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    public enum ShareKind { Round, Match }

    /// <summary>Everything the share card shows, captured when a round card opens (presentation only).</summary>
    public sealed class ShareCardData
    {
        public ShareKind kind = ShareKind.Round;
        public int roundNumber = 1;
        public bool lastLight;
        public string youName = "YOU";
        public string oppName = "PAWTRUTH";
        public long youNs = -1, oppNs = -1;
        public bool youFoul, youNoShot, oppFoul;
        public bool tie;
        public long tieGapNs;
        public Grade grade;
        public double dealt, taken;
        public ReplayMarks replay;
        /// <summary>"easy" / "hard".</summary>
        public string difficulty = DuelConfig.DifficultyHard;
        public bool pvp;
        /// <summary>Match share: 0 = you won, 1 = opponent won, -1 = standoff (double KO).</summary>
        public int matchWinner;
        public DateTime date = DateTime.Now;

        public ShareCardData Clone() { return (ShareCardData)MemberwiseClone(); }

        public static ShareCardData FromRound(RoundView v)
        {
            var d = new ShareCardData();
            d.youName = string.IsNullOrEmpty(v.youName) ? "YOU" : v.youName;
            d.oppName = v.botName ?? "";
            d.youNs = v.youNs;
            d.oppNs = v.botNs;
            d.youFoul = v.youFoul;
            d.youNoShot = v.youNoShot;
            d.oppFoul = v.botFoul;
            d.grade = v.grade;
            d.dealt = v.dealt;
            d.taken = v.taken;
            d.replay = v.replay;
            d.replay.seconds = 0f;   // the card is a still: everything landed
            return d;
        }

        /// <summary>Deterministic sample (review stills, a share before any round was played).</summary>
        public static ShareCardData Sample(ShareKind kind)
        {
            var d = new ShareCardData();
            d.kind = kind;
            d.roundNumber = 2;
            d.youNs = 214382000L;
            d.oppNs = 288105000L;
            d.grade = Grade.Perfect;
            d.dealt = 34;
            d.replay = ReplayMarks.Shots(d.youNs, d.oppNs, 0f);
            d.matchWinner = 0;
            d.date = new DateTime(2026, 10, 20);
            return d;
        }
    }

    /// <summary>Text lines of the share card, the same wording as the round card (ResultPanel). Pure.</summary>
    public static class ShareCardText
    {
        public enum Tone { Good, Bad, Tie, Plain }

        private static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

        public static string Seconds(long ns) { return ns < 0 ? "--" : NumFmt.Seconds6(ns) + " s"; }

        public static string YouTime(ShareCardData d) { return d.youFoul || d.youNoShot ? "--" : Seconds(d.youNs); }

        public static string OppTime(ShareCardData d) { return d.oppFoul ? "FOUL" : Seconds(d.oppNs); }

        public static string Gap(ShareCardData d, out Tone tone)
        {
            if (d.youFoul) { tone = Tone.Bad; return "TOO EARLY!"; }
            if (d.youNoShot) { tone = Tone.Bad; return "NO SHOT"; }
            if (d.tie) { tone = Tone.Tie; return "+-" + NumFmt.Ms2(d.tieGapNs) + " ms TOO CLOSE"; }
            if (d.youNs >= 0 && d.oppNs >= 0)
            {
                long g = d.oppNs - d.youNs;
                bool faster = g >= 0;
                tone = faster ? Tone.Good : Tone.Bad;
                return (faster ? "+" : "-") + NumFmt.Ms2(g) + " ms " + (faster ? "FASTER" : "SLOWER");
            }
            if (d.youNs >= 0) { tone = Tone.Good; return "CLEAN SHOT!"; }
            tone = Tone.Plain;
            return "";
        }

        public static string Damage(ShareCardData d, out Tone tone)
        {
            int dealt = (int)Math.Round(d.dealt), taken = (int)Math.Round(d.taken);
            if (d.tie) { tone = Tone.Bad; return "HIT -" + dealt + "  TOOK -" + taken + " HP"; }
            if (d.dealt > 0.0001) { tone = Tone.Bad; return "HIT -" + dealt + " HP"; }
            if (d.taken > 0.0001) { tone = Tone.Plain; return "TOOK -" + taken + " HP"; }
            tone = Tone.Plain;
            return "NO DAMAGE";
        }

        /// <summary>Stamp sprite of the round (ui.json name), null for none.</summary>
        public static string Stamp(ShareCardData d)
        {
            if (d.youFoul) return "stamp_too_early";
            if (d.youNoShot) return "stamp_no_shot";
            return d.grade != Grade.None ? ResultPanel.GradeSprite(d.grade) : null;
        }

        public static string RoundLabel(ShareCardData d)
        {
            return d.lastLight ? "LAST LIGHT" : "ROUND " + d.roundNumber.ToString(CultureInfo.InvariantCulture);
        }

        public static string Mode(ShareCardData d)
        {
            if (d.pvp) return "PVP";
            return d.difficulty == DuelConfig.DifficultyEasy ? "EASY" : "HARD";
        }

        /// <summary>"20 OCT 2026" (fixed English month, the bitmap fonts' glyphs only).</summary>
        public static string Date(DateTime t)
        {
            return t.Day.ToString(CultureInfo.InvariantCulture) + " " + Months[Mathf.Clamp(t.Month - 1, 0, 11)] + " " +
                   t.Year.ToString(CultureInfo.InvariantCulture);
        }

        public const string CallToAction = "CAN YOU OUTDRAW ME?";
        public const string Tag = "#PAWNOON";

        /// <summary>Caption handed to the share sheet with the PNG.</summary>
        public static string Caption(ShareCardData d)
        {
            if (!d.youFoul && !d.youNoShot && d.youNs >= 0)
                return "I drew in " + NumFmt.Seconds6(d.youNs) + " s in Paw Noon. Can you outdraw me? #PawNoon";
            return "High noon in Paw Noon. Can you outdraw me? #PawNoon";
        }
    }

    /// <summary>
    /// Layout of the share card (engine-free maths, pinned by EditMode tests). The composition is 270x480 canvas
    /// px (the game's 270-px core grid) saved at 2x as a 540x960 PNG. Top to bottom: the PAW NOON wordmark over
    /// the sky, a darkened band holding the header (round sign, or the WIN / LOSE banner on a match share), the
    /// round card with both reaction times and the stamp, and the replay strip; the call to action; the duel
    /// itself (your hat and shoulders, the opponent in the street); a footer with mode, #PAWNOON and the date.
    /// </summary>
    public static class ShareCardLayout
    {
        public const int Width = 270, Height = 480, Scale = 2;
        public const int OutWidth = Width * Scale, OutHeight = Height * Scale;
        /// <summary>Canvas px of the street plate that the card shows (the core rect of the 360x640 canvas).</summary>
        public const int PlateX = 45, PlateY = 80;
        public const int LogoY = 10;
        public const int HeaderY = 84;
        public const int CardGap = 8;
        public const int StripGap = 10;
        public const int BandTop = 78, BandPad = 10, BandEdgeRows = 4;
        public const float BandKeep = 0.30f;
        public const int CtaGap = 6, CtaHeight = 10;
        public const int FooterH = 16;
        /// <summary>Text inset inside result_card.png (same as the round card in the game).</summary>
        public const int CardInsetX = 18, CardInsetY = 14;
        /// <summary>Stamp: right inset on the card and top offset from the card's top.</summary>
        public const int StampRight = 12, StampTop = 44;
        /// <summary>A wide stamp hangs this far below the card's bottom edge (clear of the last text row).</summary>
        public const int WideStampDrop = 11;
        /// <summary>Opponent's feet on the card canvas; hero sprite top-left (cropped by the footer).</summary>
        public const int OppFeetX = 186, OppFeetAboveFooter = 22;
        public const int HeroLeft = -72, HeroVisibleRows = 116;

        public struct Rects
        {
            public RectInt logo, header, card, stamp, strip, band, footer, opp, hero;
            public int ctaY;
        }

        private static int CenterLeft(int w) { return (Width - w) / 2; }

        /// <summary>
        /// Rects for one card. Sizes are the sprites' cells (pass zero size for a missing sprite: it keeps its slot).
        /// oppPivot is the opponent cell's pivot (feet).
        /// </summary>
        public static Rects Compute(bool match, Vector2Int logo, Vector2Int header, Vector2Int card, Vector2Int stamp,
            Vector2Int oppCell, Vector2Int oppPivot, Vector2Int heroCell)
        {
            var r = new Rects();
            r.logo = new RectInt(CenterLeft(logo.x), LogoY, logo.x, logo.y);
            // the header slot is as tall as the sprite (banner 47 / round sign 18); a missing sprite keeps 18
            int hh = header.y > 0 ? header.y : 18;
            r.header = new RectInt(CenterLeft(header.x), HeaderY, header.x, hh);
            int cw = card.x > 0 ? card.x : 228, ch = card.y > 0 ? card.y : 95;
            r.card = new RectInt(CenterLeft(cw), r.header.yMax + CardGap, cw, ch);
            if (stamp.x > cw / 2)
            {
                // a wide stamp (TOO EARLY!, 148 px) would cover the text column: centre it on the card's bottom edge
                r.stamp = new RectInt(r.card.x + (cw - stamp.x) / 2, r.card.yMax - stamp.y + WideStampDrop, stamp.x, stamp.y);
            }
            else r.stamp = new RectInt(r.card.xMax - StampRight - stamp.x, r.card.y + StampTop, stamp.x, stamp.y);
            int stripY = Math.Max(r.card.yMax + StripGap, r.stamp.yMax + 2);
            r.strip = new RectInt(CenterLeft(ReplayStripMath.Width), stripY, ReplayStripMath.Width, ReplayStripMath.Height);
            r.band = new RectInt(0, BandTop, Width, Math.Max(r.strip.yMax, r.stamp.yMax) + BandPad - BandTop);
            r.ctaY = r.band.yMax + CtaGap;
            r.footer = new RectInt(0, Height - FooterH, Width, FooterH);
            int feetY = r.footer.y - OppFeetAboveFooter;
            r.opp = new RectInt(OppFeetX - oppPivot.x, feetY - oppPivot.y, oppCell.x, oppCell.y);
            int heroTop = r.footer.y - HeroVisibleRows;
            r.hero = new RectInt(HeroLeft, heroTop, heroCell.x, Math.Min(heroCell.y, r.footer.y - heroTop));
            return r;
        }

        /// <summary>The PNG size for a canvas size (whole-pixel nearest-neighbour upscale).</summary>
        public static Vector2Int Output(int scale) { return new Vector2Int(Width * scale, Height * scale); }
    }

    /// <summary>
    /// The rounds of the current match as share-card data (filled from the round card, ResultPanel.Show).
    /// A match share uses the player's fastest clean shot of the match (the last round when there was none).
    /// </summary>
    public static class ShareLog
    {
        private static readonly List<ShareCardData> rounds = new List<ShareCardData>(9);
        private static long matchKey = long.MinValue;
        private static ShareCardData last;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession() { Clear(); }

        /// <summary>The round card just opened for <paramref name="v"/>.</summary>
        public static ShareCardData Capture(RoundView v)
        {
            var d = ShareCardData.FromRound(v);
            long key = 0;
            var g = PawGame.Instance;
            if (g != null && g.Director != null)
            {
                var dir = g.Director;
                var round = dir.Authority.Round;
                d.roundNumber = dir.Authority.Match.RoundNumber;
                d.lastLight = dir.LastLight;
                d.pvp = dir.MatchRules != null;
                d.difficulty = g.Config.Difficulty;
                long gap;
                if (round != null && round.isTie && round.TryGetGapNs(out gap)) { d.tie = true; d.tieGapNs = gap; }
                key = dir.Seed;   // StartMatch advances the seed once per match: one key per match
            }
            d.date = DateTime.Now;
            Add(d, key);
            return d;
        }

        /// <summary>Adds a round of the match <paramref name="key"/>; a new key starts a new match.</summary>
        public static void Add(ShareCardData d, long key)
        {
            if (key != matchKey) { rounds.Clear(); matchKey = key; }
            if (rounds.Count >= 16) rounds.RemoveAt(0);
            rounds.Add(d);
            last = d;
        }

        public static void Clear()
        {
            rounds.Clear();
            last = null;
            matchKey = long.MinValue;
        }

        public static int Count { get { return rounds.Count; } }

        /// <summary>The round card currently / last shown (null before the first round).</summary>
        public static ShareCardData LastRound { get { return last; } }

        /// <summary>Best round of the match: the fastest clean shot, else the last round, else null.</summary>
        public static ShareCardData BestRound()
        {
            ShareCardData best = null;
            for (int i = 0; i < rounds.Count; i++)
            {
                var r = rounds[i];
                if (r.youFoul || r.youNoShot || r.youNs < 0) continue;
                if (best == null || r.youNs < best.youNs) best = r;
            }
            return best ?? last;
        }

        /// <summary>The data for a match share (best round + the verdict).</summary>
        public static ShareCardData ForMatch(int winner)
        {
            var b = BestRound();
            var d = b != null ? b.Clone() : ShareCardData.Sample(ShareKind.Match);
            d.kind = ShareKind.Match;
            d.matchWinner = winner;
            return d;
        }

        /// <summary>The data for a round share (the card on screen).</summary>
        public static ShareCardData ForRound()
        {
            var d = last != null ? last.Clone() : ShareCardData.Sample(ShareKind.Round);
            d.kind = ShareKind.Round;
            return d;
        }
    }
}
