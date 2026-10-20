using System;
using System.Collections.Generic;
using System.Reflection;

namespace PawNoon.Core
{
    /// <summary>
    /// Every tunable of the duel in one place. Defaults equal SPEC §1; Resources/Data/duel_config.json
    /// overrides any field it contains (unknown keys are ignored, missing keys keep defaults).
    /// </summary>
    [Serializable]
    public sealed class DuelConfig
    {
        // --- round schedule
        public int countdownBeats = 3;
        public double beatSeconds = 0.70;
        public double holdMinSeconds = 1.0;
        public double holdMaxSeconds = 4.0;
        public double noShotTimeoutSeconds = 1.5;
        /// <summary>A received shot / planned bot shot is committed when it is at least this old (ms).
        /// Lets late-arriving input events with earlier timestamps still win the ordering.</summary>
        public double commitGraceMs = 25.0;
        /// <summary>Reactions faster than this are physiologically impossible -> ANTICIPATION foul.</summary>
        public double anticipationMs = 100.0;
        /// <summary>Two shots whose reactions differ by no more than this (inclusive) are a TIE: both land,
        /// the round has no winner, and if both duelists drop to 0 HP the match is a draw.</summary>
        public double tieWindowMs = 15.0;
        /// <summary>Extra slack (ms) on the no-shot timeout when validating a report.</summary>
        public double reportSlackMs = 50.0;
        /// <summary>Estimated frame-start -> photons latency, in display frames (calibration).</summary>
        public double presentLatencyFrames = 1.0;
        /// <summary>Additional fixed display latency (ms) added to the estimate (calibration, may be negative).</summary>
        public double presentLatencyExtraMs = 0.0;

        // --- match
        public double maxHp = 100.0;

        // --- damage curve (piecewise linear, ms -> damage). Below first anchor = first value;
        // above the last anchor = 0.
        public double[] damageMs = { 150, 200, 250, 300, 400, 600, 1000, 1500 };
        public double[] damageValue = { 40, 36, 30, 24, 16, 8, 4, 2 };

        // --- grades (upper bounds, inclusive, ms)
        public double gradeLightningMs = 200;
        public double gradePerfectMs = 250;
        public double gradeGreatMs = 320;
        public double gradeGoodMs = 420;
        public double gradeSlowMs = 700;

        // --- bot "Blackpaw" (adaptive: tightens as the player wins rounds)
        public string botName = "BLACKPAW";
        public double botMeanStartMs = 300;
        public double botMeanEndMs = 220;
        public double botSdStartMs = 50;
        public double botSdEndMs = 30;
        /// <summary>Player round wins needed to reach the End values (linear in between). 3 = a typical
        /// 4-hit win meets the sharpest Blackpaw (mean 220 / sd 30) on the finishing round.</summary>
        public int botAdaptRounds = 3;
        public double botMinMs = 170;
        public double botMaxMs = 900;
        /// <summary>Chance per round that he jumps the gun (planned at round start, always before the cue).</summary>
        public double botFoulChance = 0.04;
        /// <summary>He never fouls before this round number (round 1 is a clean read of the rules).</summary>
        public int botFoulMinRound = 2;

        // --- bot acting during the hold (presentation-only bait; never changes his reaction)
        /// <summary>Base chance per round of at least one feint (paw flick + spur jingle) during the hold.</summary>
        public double botFeintChance = 0.35;
        /// <summary>Added to the feint chance per unit of "itch" (the player's recent early draws).</summary>
        public double botFeintChancePerItch = 0.25;
        public double botFeintMaxChance = 0.85;
        /// <summary>Chance of a second feint, given a first one (+ per itch, capped).</summary>
        public double botSecondFeintChance = 0.25;
        public double botSecondFeintPerItch = 0.20;
        public double botSecondFeintMaxChance = 0.60;
        /// <summary>Feints never start closer than this to the real cue (ms) so they stay readable as bait.</summary>
        public double botFeintMinBeforeCueMs = 350;
        /// <summary>Feints never start earlier than this after the hold begins ("1" disappears), ms.</summary>
        public double botFeintMinAfterHoldMs = 250;
        /// <summary>Gap between two feints (ms, uniform in [min, max]).</summary>
        public double botFeintGapMinMs = 450;
        public double botFeintGapMaxMs = 1000;
        /// <summary>Itch added each round the player draws early (TOO EARLY or anticipation).</summary>
        public double botItchPerEarlyDraw = 1.0;
        /// <summary>Itch multiplier after each round the player did NOT draw early.</summary>
        public double botItchDecay = 0.6;
        public double botItchMax = 3.0;
        /// <summary>Chance per round of the deliberate late tell (ears flatten just before the cue).</summary>
        public double botTellChance = 0.20;
        /// <summary>How long before the cue the tell shows (ms).</summary>
        public double botTellLeadMs = 150;

        // --- presentation (runtime only; core ignores them)
        public double hitStopMs = 70;
        public double drawHitStopFrames = 1;
        public double killSlowMoScale = 0.35;
        public double killSlowMoSeconds = 0.6;
        public double resultAutoAdvanceSeconds = 2.2;
        /// <summary>Pause after the last shot / fall has settled before the round panel opens (s).</summary>
        public double resultSettleSeconds = 0.5;
        public double holdZoom = 1.035;
        /// <summary>Ambient time scale the world eases to during the hold (0 = everything stills).</summary>
        public double holdAmbientScale = 0.0;
        /// <summary>Extra vignette strength eased in over the hold.</summary>
        public double holdVignette = 0.10;
        /// <summary>Slight focus vignette added for the whole duel (eased in at match start).</summary>
        public double duelVignette = 0.12;
        /// <summary>Vignette thump on each hold heartbeat at full frenzy.</summary>
        public double beatVignette = 0.05;
        /// <summary>Chance per hold of one sweat drop on the hero.</summary>
        public double holdSweatChance = 0.4;
        /// <summary>Animation speed of a feint flick (lower = easier to see).</summary>
        public double feintAnimSpeed = 0.6;
        public double shakeShot = 2.0;
        public double shakeHit = 3.0;
        /// <summary>Hero gun recoil kick on his actor (px) and its decay time (s).</summary>
        public double heroRecoilPx = 3.0;
        public double recoilSeconds = 0.12;
        /// <summary>Knock-back of the victim on a hit (px) and its decay time (s).</summary>
        public double knockBanditPx = 4.0;
        public double knockHeroPx = 3.0;
        public double knockSeconds = 0.16;
        /// <summary>Darkening vignette pulse when Blackpaw fires toward the camera (added strength, seconds).</summary>
        public double banditShotVignette = 0.22;
        public double vignettePulseSeconds = 0.25;
        /// <summary>Tie ("TOO CLOSE!"): slow-mo at the second shot (scale, real seconds) and the screen
        /// desaturation amount (0..1, used only if the pixel pipeline exposes a desaturation field).</summary>
        public double tieSlowMoScale = 0.2;
        public double tieSlowMoSeconds = 0.9;
        public double tieDesaturate = 0.3;
        /// <summary>Reaction replay strip on the round card (SPEC_V2 §8.1): build-in time (s); Reduced Motion = instant.</summary>
        public double replayStripSeconds = 0.35;

        // --- Blackpaw talks (presentation only)
        /// <summary>Chance per round that he says something (match start and "last one" always speak).</summary>
        public double banterChance = 0.55;
        /// <summary>The bubble appears this long before "3" (the standoff is stretched to fit).</summary>
        public double banterLeadSeconds = 1.2;
        public double banterHoldSeconds = 1.6;
        /// <summary>"Last one" context when either duelist is below this HP.</summary>
        public double banterLowHp = 30;
        /// <summary>How long his match-win line stays up over the result.</summary>
        public double banterMatchEndSeconds = 2.0;
        public BanterLines blackpawLines = new BanterLines();

        // --- sudden death "LAST LIGHT" (SPEC_V2 §8.4). RULES: the round's damage multiplier is decided by the
        // match driver (DuelDirector offline, recorded_match.ts on the server) from SuddenDeath.MultiplierFor
        // before each countdown, and applied once by LocalAuthority. Keep the server port in sync (DECISIONS.md).
        /// <summary>Master switch for LAST LIGHT (rule and presentation).</summary>
        public bool suddenDeathEnabled = true;
        /// <summary>LAST LIGHT when BOTH duelists are at or below this HP (and alive) as a standoff starts.</summary>
        public double suddenDeathHp = 20;
        /// <summary>Damage multiplier for a LAST LIGHT round (both shooters).</summary>
        public double suddenDeathDamageMultiplier = 2.0;
        /// <summary>Presentation: ECG flat line before the single big spike (s).</summary>
        public double suddenDeathFlatLineSeconds = 1.2;
        /// <summary>Presentation: extra music duck during the LAST LIGHT hold (dB below the normal hold duck).</summary>
        public double suddenDeathMusicDuckDb = 6.0;
        /// <summary>Presentation: volume multiplier on the hold heartbeat one-shots during LAST LIGHT.</summary>
        public double suddenDeathHeartbeatGain = 1.1;

        // --- difficulty (SPEC_V2 §1): Hard = duel_config.json alone, Easy = duel_config.easy.json merged on top
        public const string DifficultyHard = "hard";
        public const string DifficultyEasy = "easy";
        /// <summary>"hard" or "easy": which rule set this config was loaded as (set by Load, not read from JSON).</summary>
        public string Difficulty = DifficultyHard;
        public bool IsEasy { get { return Difficulty == DifficultyEasy; } }

        /// <summary>Maps any input to "easy" or "hard" (unknown / empty = hard).</summary>
        public static string NormalizeDifficulty(string d)
        {
            return string.Equals((d ?? "").Trim(), DifficultyEasy, StringComparison.OrdinalIgnoreCase) ? DifficultyEasy : DifficultyHard;
        }

        public static DuelConfig Default() { return new DuelConfig(); }

        private static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

        public long BeatNs { get { return SecToNs(beatSeconds); } }
        public long NoShotTimeoutNs { get { return SecToNs(noShotTimeoutSeconds); } }
        public long CommitGraceNs { get { return MsToNs(commitGraceMs); } }
        public long AnticipationNs { get { return MsToNs(anticipationMs); } }
        public long TieWindowNs { get { return MsToNs(tieWindowMs); } }
        public long ReportSlackNs { get { return MsToNs(reportSlackMs); } }

        public static long SecToNs(double s) { return (long)Math.Round(s * 1e9); }
        public static long MsToNs(double ms) { return (long)Math.Round(ms * 1e6); }
        public static double NsToMs(long ns) { return ns / 1e6; }

        /// <summary>Bot reaction distribution for a given number of rounds the player has already won.</summary>
        public void BotDistribution(int playerRoundWins, out double meanMs, out double sdMs)
        {
            double t = botAdaptRounds <= 0 ? 1.0 : Math.Min(1.0, Math.Max(0, playerRoundWins) / (double)botAdaptRounds);
            meanMs = botMeanStartMs + (botMeanEndMs - botMeanStartMs) * t;
            sdMs = botSdStartMs + (botSdEndMs - botSdStartMs) * t;
        }

        /// <summary>Parses JSON text; returns defaults (never null) on any problem.</summary>
        public static DuelConfig FromJson(string json)
        {
            return FromNode(PnJson.ParseObject(json));
        }

        /// <summary>Hard rules: the base JSON alone (same as FromJson, Difficulty = "hard").</summary>
        public static DuelConfig Load(string hardJson)
        {
            return Load(hardJson, null, DifficultyHard);
        }

        /// <summary>
        /// Base JSON with an overlay merged on top (PnJson.Merge: overlay keys win, nested objects merged one
        /// level, arrays replaced). A null / empty / malformed overlay leaves the base untouched.
        /// Difficulty is "easy" when an overlay was applied, else "hard".
        /// </summary>
        public static DuelConfig Load(string hardJson, string overlayJson)
        {
            return Load(hardJson, overlayJson, null);
        }

        /// <summary>As Load(hard, overlay) with an explicit difficulty label (null = derive from the overlay).</summary>
        public static DuelConfig Load(string hardJson, string overlayJson, string difficulty)
        {
            var root = PnJson.ParseObject(hardJson);
            var overlay = PnJson.ParseObject(overlayJson);
            if (overlay != null) root = PnJson.Merge(root, overlay);
            var c = FromNode(root);
            c.Difficulty = difficulty != null ? NormalizeDifficulty(difficulty) : (overlay != null ? DifficultyEasy : DifficultyHard);
            return c;
        }

        /// <summary>
        /// Copies every field of <paramref name="other"/> into this instance, so systems that hold this config
        /// by reference (authority, clients, bot, director) see the new rules from the next read on.
        /// Call only between matches (difficulty change on the title screen).
        /// </summary>
        public void CopyFrom(DuelConfig other)
        {
            if (other == null || ReferenceEquals(other, this)) return;
            var fields = typeof(DuelConfig).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.IsInitOnly || f.IsLiteral) continue;
                var v = f.GetValue(other);
                var arr = v as double[];
                if (arr != null) v = (double[])arr.Clone();
                f.SetValue(this, v);
            }
        }

        private static DuelConfig FromNode(Dictionary<string, object> root)
        {
            var c = new DuelConfig();
            if (root == null) return c;
            c.countdownBeats = PnJson.Int(root, "countdownBeats", c.countdownBeats);
            c.beatSeconds = PnJson.Num(root, "beatSeconds", c.beatSeconds);
            c.holdMinSeconds = PnJson.Num(root, "holdMinSeconds", c.holdMinSeconds);
            c.holdMaxSeconds = PnJson.Num(root, "holdMaxSeconds", c.holdMaxSeconds);
            c.noShotTimeoutSeconds = PnJson.Num(root, "noShotTimeoutSeconds", c.noShotTimeoutSeconds);
            c.commitGraceMs = PnJson.Num(root, "commitGraceMs", c.commitGraceMs);
            c.anticipationMs = PnJson.Num(root, "anticipationMs", c.anticipationMs);
            c.tieWindowMs = PnJson.Num(root, "tieWindowMs", c.tieWindowMs);
            c.reportSlackMs = PnJson.Num(root, "reportSlackMs", c.reportSlackMs);
            c.presentLatencyFrames = PnJson.Num(root, "presentLatencyFrames", c.presentLatencyFrames);
            c.presentLatencyExtraMs = PnJson.Num(root, "presentLatencyExtraMs", c.presentLatencyExtraMs);
            c.maxHp = PnJson.Num(root, "maxHp", c.maxHp);

            var dm = PnJson.NumArray(root, "damageMs");
            var dv = PnJson.NumArray(root, "damageValue");
            if (dm != null && dv != null && dm.Length == dv.Length && dm.Length >= 2)
            {
                c.damageMs = dm;
                c.damageValue = dv;
            }

            c.gradeLightningMs = PnJson.Num(root, "gradeLightningMs", c.gradeLightningMs);
            c.gradePerfectMs = PnJson.Num(root, "gradePerfectMs", c.gradePerfectMs);
            c.gradeGreatMs = PnJson.Num(root, "gradeGreatMs", c.gradeGreatMs);
            c.gradeGoodMs = PnJson.Num(root, "gradeGoodMs", c.gradeGoodMs);
            c.gradeSlowMs = PnJson.Num(root, "gradeSlowMs", c.gradeSlowMs);

            c.botName = PnJson.Str(root, "botName", c.botName);
            c.botMeanStartMs = PnJson.Num(root, "botMeanStartMs", c.botMeanStartMs);
            c.botMeanEndMs = PnJson.Num(root, "botMeanEndMs", c.botMeanEndMs);
            c.botSdStartMs = PnJson.Num(root, "botSdStartMs", c.botSdStartMs);
            c.botSdEndMs = PnJson.Num(root, "botSdEndMs", c.botSdEndMs);
            c.botAdaptRounds = PnJson.Int(root, "botAdaptRounds", c.botAdaptRounds);
            c.botMinMs = PnJson.Num(root, "botMinMs", c.botMinMs);
            c.botMaxMs = PnJson.Num(root, "botMaxMs", c.botMaxMs);
            c.botFoulChance = PnJson.Num(root, "botFoulChance", c.botFoulChance);
            c.botFoulMinRound = PnJson.Int(root, "botFoulMinRound", c.botFoulMinRound);
            c.botFeintChance = PnJson.Num(root, "botFeintChance", c.botFeintChance);
            c.botFeintChancePerItch = PnJson.Num(root, "botFeintChancePerItch", c.botFeintChancePerItch);
            c.botFeintMaxChance = PnJson.Num(root, "botFeintMaxChance", c.botFeintMaxChance);
            c.botSecondFeintChance = PnJson.Num(root, "botSecondFeintChance", c.botSecondFeintChance);
            c.botSecondFeintPerItch = PnJson.Num(root, "botSecondFeintPerItch", c.botSecondFeintPerItch);
            c.botSecondFeintMaxChance = PnJson.Num(root, "botSecondFeintMaxChance", c.botSecondFeintMaxChance);
            c.botFeintMinBeforeCueMs = PnJson.Num(root, "botFeintMinBeforeCueMs", c.botFeintMinBeforeCueMs);
            c.botFeintMinAfterHoldMs = PnJson.Num(root, "botFeintMinAfterHoldMs", c.botFeintMinAfterHoldMs);
            c.botFeintGapMinMs = PnJson.Num(root, "botFeintGapMinMs", c.botFeintGapMinMs);
            c.botFeintGapMaxMs = PnJson.Num(root, "botFeintGapMaxMs", c.botFeintGapMaxMs);
            c.botItchPerEarlyDraw = PnJson.Num(root, "botItchPerEarlyDraw", c.botItchPerEarlyDraw);
            c.botItchDecay = PnJson.Num(root, "botItchDecay", c.botItchDecay);
            c.botItchMax = PnJson.Num(root, "botItchMax", c.botItchMax);
            c.botTellChance = PnJson.Num(root, "botTellChance", c.botTellChance);
            c.botTellLeadMs = PnJson.Num(root, "botTellLeadMs", c.botTellLeadMs);

            c.hitStopMs = PnJson.Num(root, "hitStopMs", c.hitStopMs);
            c.drawHitStopFrames = PnJson.Num(root, "drawHitStopFrames", c.drawHitStopFrames);
            c.killSlowMoScale = PnJson.Num(root, "killSlowMoScale", c.killSlowMoScale);
            c.killSlowMoSeconds = PnJson.Num(root, "killSlowMoSeconds", c.killSlowMoSeconds);
            c.resultAutoAdvanceSeconds = PnJson.Num(root, "resultAutoAdvanceSeconds", c.resultAutoAdvanceSeconds);
            c.resultSettleSeconds = PnJson.Num(root, "resultSettleSeconds", c.resultSettleSeconds);
            c.holdZoom = PnJson.Num(root, "holdZoom", c.holdZoom);
            c.holdAmbientScale = PnJson.Num(root, "holdAmbientScale", c.holdAmbientScale);
            c.holdVignette = PnJson.Num(root, "holdVignette", c.holdVignette);
            c.duelVignette = PnJson.Num(root, "duelVignette", c.duelVignette);
            c.beatVignette = PnJson.Num(root, "beatVignette", c.beatVignette);
            c.holdSweatChance = PnJson.Num(root, "holdSweatChance", c.holdSweatChance);
            c.feintAnimSpeed = PnJson.Num(root, "feintAnimSpeed", c.feintAnimSpeed);
            c.shakeShot = PnJson.Num(root, "shakeShot", c.shakeShot);
            c.shakeHit = PnJson.Num(root, "shakeHit", c.shakeHit);
            c.heroRecoilPx = PnJson.Num(root, "heroRecoilPx", c.heroRecoilPx);
            c.recoilSeconds = PnJson.Num(root, "recoilSeconds", c.recoilSeconds);
            c.knockBanditPx = PnJson.Num(root, "knockBanditPx", c.knockBanditPx);
            c.knockHeroPx = PnJson.Num(root, "knockHeroPx", c.knockHeroPx);
            c.knockSeconds = PnJson.Num(root, "knockSeconds", c.knockSeconds);
            c.banditShotVignette = PnJson.Num(root, "banditShotVignette", c.banditShotVignette);
            c.vignettePulseSeconds = PnJson.Num(root, "vignettePulseSeconds", c.vignettePulseSeconds);
            c.tieSlowMoScale = PnJson.Num(root, "tieSlowMoScale", c.tieSlowMoScale);
            c.tieSlowMoSeconds = PnJson.Num(root, "tieSlowMoSeconds", c.tieSlowMoSeconds);
            c.tieDesaturate = PnJson.Num(root, "tieDesaturate", c.tieDesaturate);
            c.replayStripSeconds = PnJson.Num(root, "replayStripSeconds", c.replayStripSeconds);
            c.banterChance = PnJson.Num(root, "banterChance", c.banterChance);
            c.banterLeadSeconds = PnJson.Num(root, "banterLeadSeconds", c.banterLeadSeconds);
            c.banterHoldSeconds = PnJson.Num(root, "banterHoldSeconds", c.banterHoldSeconds);
            c.banterLowHp = PnJson.Num(root, "banterLowHp", c.banterLowHp);
            c.banterMatchEndSeconds = PnJson.Num(root, "banterMatchEndSeconds", c.banterMatchEndSeconds);
            c.blackpawLines = BanterLines.FromJson(PnJson.Obj(root, "blackpawLines"));
            c.suddenDeathEnabled = PnJson.Bool(root, "suddenDeathEnabled", c.suddenDeathEnabled);
            c.suddenDeathHp = PnJson.Num(root, "suddenDeathHp", c.suddenDeathHp);
            c.suddenDeathDamageMultiplier = PnJson.Num(root, "suddenDeathDamageMultiplier", c.suddenDeathDamageMultiplier);
            c.suddenDeathFlatLineSeconds = PnJson.Num(root, "suddenDeathFlatLineSeconds", c.suddenDeathFlatLineSeconds);
            c.suddenDeathMusicDuckDb = PnJson.Num(root, "suddenDeathMusicDuckDb", c.suddenDeathMusicDuckDb);
            c.suddenDeathHeartbeatGain = PnJson.Num(root, "suddenDeathHeartbeatGain", c.suddenDeathHeartbeatGain);
            c.Sanitize();
            return c;
        }

        /// <summary>Clamps nonsense values so the game can never deadlock on a bad config.</summary>
        public void Sanitize()
        {
            if (countdownBeats < 1) countdownBeats = 1;
            if (beatSeconds < 0.05) beatSeconds = 0.05;
            if (holdMinSeconds < 0) holdMinSeconds = 0;
            if (holdMaxSeconds < holdMinSeconds) holdMaxSeconds = holdMinSeconds;
            if (noShotTimeoutSeconds < 0.2) noShotTimeoutSeconds = 0.2;
            if (commitGraceMs < 0) commitGraceMs = 0;
            if (anticipationMs < 0) anticipationMs = 0;
            if (tieWindowMs < 0) tieWindowMs = 0;
            if (reportSlackMs < 0) reportSlackMs = 0;
            if (presentLatencyFrames < 0) presentLatencyFrames = 0;
            if (maxHp < 1) maxHp = 1;
            if (botMinMs < anticipationMs + 1) botMinMs = anticipationMs + 1;
            if (botMaxMs < botMinMs) botMaxMs = botMinMs;
            // the bot must always fire before the no-shot timeout
            double limit = noShotTimeoutSeconds * 1000.0 - 1.0;
            if (botMaxMs > limit) botMaxMs = limit;
            if (botMinMs > botMaxMs) botMinMs = botMaxMs;
            if (botAdaptRounds < 0) botAdaptRounds = 0;
            botFoulChance = Clamp01(botFoulChance);
            botFeintChance = Clamp01(botFeintChance);
            botFeintMaxChance = Clamp01(botFeintMaxChance);
            botSecondFeintChance = Clamp01(botSecondFeintChance);
            botSecondFeintMaxChance = Clamp01(botSecondFeintMaxChance);
            botTellChance = Clamp01(botTellChance);
            botItchDecay = Clamp01(botItchDecay);
            if (botFeintChancePerItch < 0) botFeintChancePerItch = 0;
            if (botSecondFeintPerItch < 0) botSecondFeintPerItch = 0;
            if (botItchPerEarlyDraw < 0) botItchPerEarlyDraw = 0;
            if (botItchMax < 0) botItchMax = 0;
            if (botFeintMinBeforeCueMs < 0) botFeintMinBeforeCueMs = 0;
            if (botFeintMinAfterHoldMs < 0) botFeintMinAfterHoldMs = 0;
            if (botFeintGapMinMs < 50) botFeintGapMinMs = 50;
            if (botFeintGapMaxMs < botFeintGapMinMs) botFeintGapMaxMs = botFeintGapMinMs;
            if (botTellLeadMs < 0) botTellLeadMs = 0;
            if (resultSettleSeconds < 0) resultSettleSeconds = 0;
            if (holdAmbientScale < 0) holdAmbientScale = 0;
            if (holdAmbientScale > 1) holdAmbientScale = 1;
            if (feintAnimSpeed < 0.1) feintAnimSpeed = 0.1;
            if (recoilSeconds < 0.01) recoilSeconds = 0.01;
            if (knockSeconds < 0.01) knockSeconds = 0.01;
            if (vignettePulseSeconds < 0.01) vignettePulseSeconds = 0.01;
            if (tieSlowMoScale < 0.01) tieSlowMoScale = 0.01;
            if (tieSlowMoScale > 1) tieSlowMoScale = 1;
            if (tieSlowMoSeconds < 0) tieSlowMoSeconds = 0;
            tieDesaturate = Clamp01(tieDesaturate);
            if (double.IsNaN(replayStripSeconds) || replayStripSeconds < 0) replayStripSeconds = 0;
            if (replayStripSeconds > 2) replayStripSeconds = 2;
            banterChance = Clamp01(banterChance);
            if (banterLeadSeconds < 0) banterLeadSeconds = 0;
            if (banterHoldSeconds < 0.2) banterHoldSeconds = 0.2;
            if (banterMatchEndSeconds < 0.2) banterMatchEndSeconds = 0.2;
            if (blackpawLines == null) blackpawLines = new BanterLines();
            if (double.IsNaN(suddenDeathHp) || suddenDeathHp < 0) suddenDeathHp = 0;
            if (double.IsNaN(suddenDeathDamageMultiplier) || suddenDeathDamageMultiplier < 0) suddenDeathDamageMultiplier = 1.0;
            if (suddenDeathDamageMultiplier > 10) suddenDeathDamageMultiplier = 10;
            if (double.IsNaN(suddenDeathFlatLineSeconds) || suddenDeathFlatLineSeconds < 0) suddenDeathFlatLineSeconds = 0;
            if (suddenDeathFlatLineSeconds > 5) suddenDeathFlatLineSeconds = 5;
            if (double.IsNaN(suddenDeathMusicDuckDb) || suddenDeathMusicDuckDb < 0) suddenDeathMusicDuckDb = 0;
            if (suddenDeathMusicDuckDb > 40) suddenDeathMusicDuckDb = 40;
            if (double.IsNaN(suddenDeathHeartbeatGain) || suddenDeathHeartbeatGain < 0) suddenDeathHeartbeatGain = 0;
            if (suddenDeathHeartbeatGain > 2) suddenDeathHeartbeatGain = 2;
        }
    }
}
