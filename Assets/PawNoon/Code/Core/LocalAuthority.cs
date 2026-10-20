using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    public enum RoundPhase
    {
        Standoff = 0,
        Countdown = 1,
        Hold = 2,
        Draw = 3,
        Resolving = 4,
        Result = 5
    }

    public enum SlotOutcome
    {
        Pending = 0,
        Shot = 1,
        Foul = 2,
        Anticipation = 3,
        NoShot = 4,
        Stopped = 5
    }

    public enum ReportVerdict
    {
        Accepted = 0,
        BadSlot,
        WrongRound,
        Stale,
        Duplicate,
        Implausible,
        Inconsistent
    }

    public enum DuelEventKind
    {
        RoundStarted = 0,
        Cue,
        Foul,
        Shot,
        Stopped,
        NoShot,
        RoundVoid,
        RoundResolved,
        MatchOver
    }

    public struct DuelEvent
    {
        public DuelEventKind kind;
        public int roundId;
        public int slot;
        public long reactionNs;
        public double damage;
        public double targetHpAfter;
        public bool killed;
        public bool anticipation;
        /// <summary>Shot: fired inside the tie window together with the other shot.
        /// RoundResolved: the round is a TIE (slot = -1).</summary>
        public bool simultaneous;
    }

    public sealed class SlotRecord
    {
        public SlotOutcome outcome;
        public bool hasReport;
        public ShotReport report;
        /// <summary>Reaction in ns for accepted shots (also anticipation), else -1.</summary>
        public long reactionNs = -1;
        public double damageDealt;
        public Grade grade;

        public void Clear()
        {
            outcome = SlotOutcome.Pending;
            hasReport = false;
            report = default(ShotReport);
            reactionNs = -1;
            damageDealt = 0;
            grade = Grade.None;
        }

        public bool Settled { get { return outcome != SlotOutcome.Pending; } }
        public bool IsFoul { get { return outcome == SlotOutcome.Foul || outcome == SlotOutcome.Anticipation; } }
    }

    public sealed class RoundRecord
    {
        public int roundId;
        public int roundNumber;
        public RoundSchedule schedule;
        public long countdownStartNs;
        /// <summary>Authority time at which the DrawCue was sent (decided), -1 before.</summary>
        public long cueDecidedNs = -1;
        public bool cueSent;
        public bool isVoid;
        /// <summary>Both shots landed inside the tie window ("TOO CLOSE"): no round winner.</summary>
        public bool isTie;
        public int winnerSlot = -1;
        /// <summary>Damage multiplier this round was played under (1 = normal, 2 = LAST LIGHT). Fixed at StartCountdown.</summary>
        public double damageMultiplier = 1.0;
        public readonly SlotRecord[] slots = { new SlotRecord(), new SlotRecord() };

        public void Clear()
        {
            roundId = 0;
            schedule = null;
            countdownStartNs = 0;
            cueDecidedNs = -1;
            cueSent = false;
            isVoid = false;
            isTie = false;
            winnerSlot = -1;
            damageMultiplier = 1.0;
            slots[0].Clear();
            slots[1].Clear();
        }

        /// <summary>slot0 - slot1 reaction gap (negative = slot 0 faster). False unless both reactions exist.</summary>
        public bool TryGetGapNs(out long gapNs)
        {
            gapNs = 0;
            if (slots[0].reactionNs < 0 || slots[1].reactionNs < 0) return false;
            gapNs = slots[0].reactionNs - slots[1].reactionNs;
            return true;
        }
    }

    /// <summary>
    /// The rules authority. Owns the RNG, the schedule and the cue decision; never reveals the cue time
    /// in advance. Validates ShotReports from clients (local player, bot, later remote players) and
    /// resolves shot order by reported reaction with a tie window. Pure C#, driven by Tick().
    /// A future server runs this same class.
    /// </summary>
    public sealed class LocalAuthority
    {
        public const int Slots = 2;
        private const long ConsistencyToleranceNs = 1000; // 1 us

        private readonly DuelConfig cfg;
        private readonly IDuelClock clock;
        private readonly IDuelTransport transport;
        private Random rng;
        private int nextRoundId = 1;
        private bool forceCue;
        private double pendingDamageMultiplier = 1.0;
        private readonly Queue<DuelEvent> events = new Queue<DuelEvent>(16);

        public readonly MatchState Match;
        public readonly RoundRecord Round = new RoundRecord();
        public RoundPhase Phase { get; private set; }
        public ReportVerdict LastVerdict { get; private set; }
        public int RejectedReports { get; private set; }
        public DuelConfig Config { get { return cfg; } }

        public LocalAuthority(DuelConfig cfg, IDuelClock clock, IDuelTransport transport, int seed)
        {
            this.cfg = cfg ?? new DuelConfig();
            this.clock = clock;
            this.transport = transport;
            rng = new Random(seed);
            Match = new MatchState(this.cfg.maxHp);
            Phase = RoundPhase.Standoff;
        }

        public void Reseed(int seed) { rng = new Random(seed); }

        public void StartMatch()
        {
            Match.Reset(cfg.maxHp);
            Round.Clear();
            events.Clear();
            transport.Clear();
            forceCue = false;
            pendingDamageMultiplier = 1.0;
            Phase = RoundPhase.Standoff;
        }

        /// <summary>
        /// Damage multiplier for the NEXT round (taken at StartCountdown, so a round in flight never changes).
        /// The match driver sets it from SuddenDeath.MultiplierFor before each standoff; StartMatch resets it to 1.
        /// NaN, infinite or negative values mean 1.
        /// </summary>
        public void SetDamageMultiplier(double multiplier)
        {
            pendingDamageMultiplier = (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 0) ? 1.0 : multiplier;
        }

        /// <summary>The multiplier the next StartCountdown will use.</summary>
        public double PendingDamageMultiplier { get { return pendingDamageMultiplier; } }

        /// <summary>Standoff -> Countdown. The countdown starts "now" ("3" appears).</summary>
        public bool StartCountdown()
        {
            if (Phase != RoundPhase.Standoff || Match.Over) return false;
            long now = clock.NowNs;
            Round.Clear();
            Round.roundId = nextRoundId++;
            Round.roundNumber = Match.RoundNumber;
            Round.schedule = RoundSchedule.Create(cfg, rng);
            Round.countdownStartNs = now;
            Round.damageMultiplier = pendingDamageMultiplier;
            forceCue = false;
            Phase = RoundPhase.Countdown;

            var msg = new RoundStartMsg();
            msg.roundId = Round.roundId;
            msg.roundNumber = Round.roundNumber;
            msg.countdownBeats = Round.schedule.Beats;
            msg.beatNs = Round.schedule.BeatNs;
            transport.BroadcastRoundStart(msg);
            Push(DuelEventKind.RoundStarted, -1);
            return true;
        }

        /// <summary>Review/debug only: makes the cue fire on the next Tick.</summary>
        public void ForceCueNow() { forceCue = true; }

        /// <summary>Result -> Standoff for the next round (if the match is not over).</summary>
        public bool NextRound()
        {
            if (Phase != RoundPhase.Result || Match.Over) return false;
            Match.RoundNumber++;
            Phase = RoundPhase.Standoff;
            return true;
        }

        /// <summary>Cancels the running round (app backgrounded, both fouled...). Same round number replays.</summary>
        public bool VoidRound()
        {
            if (Phase == RoundPhase.Standoff || Phase == RoundPhase.Result) return false;
            DoVoid();
            return true;
        }

        public bool TryDequeueEvent(out DuelEvent e)
        {
            if (events.Count > 0) { e = events.Dequeue(); return true; }
            e = default(DuelEvent);
            return false;
        }

        public void Tick()
        {
            long now = clock.NowNs;

            if (Phase == RoundPhase.Countdown && (forceCue || now - Round.countdownStartNs >= Round.schedule.CountdownNs))
                Phase = RoundPhase.Hold;

            if (Phase == RoundPhase.Hold && (forceCue || now - Round.countdownStartNs >= Round.schedule.CueOffsetNs))
            {
                forceCue = false;
                Round.cueSent = true;
                Round.cueDecidedNs = now;
                Phase = RoundPhase.Draw;
                var cue = new DrawCue();
                cue.roundId = Round.roundId;
                cue.cueSeq = 1;
                transport.BroadcastCue(cue);
                Push(DuelEventKind.Cue, -1);
            }

            ShotReport r;
            while (transport.TryReceiveReport(out r))
            {
                var v = Submit(r, now);
                if (v != ReportVerdict.Accepted) RejectedReports++;
            }

            if (Phase == RoundPhase.Draw || Phase == RoundPhase.Resolving)
            {
                Commit(now);
                Timeouts(now);
                Commit(now);
                if (Round.slots[0].Settled && Round.slots[1].Settled) Resolve();
            }
        }

        /// <summary>Validates and applies a report at authority time "now". Public for tests.</summary>
        public ReportVerdict Submit(ShotReport r, long now)
        {
            LastVerdict = Validate(r, now);
            if (LastVerdict != ReportVerdict.Accepted) return LastVerdict;

            var s = Round.slots[r.slot];
            s.hasReport = true;
            s.report = r;
            if (r.kind == ReportKind.Early)
            {
                s.outcome = SlotOutcome.Foul;
                Push(DuelEventKind.Foul, r.slot);
                CheckBothFoul();
                return LastVerdict;
            }

            s.reactionNs = r.reactionNs;
            if (r.reactionNs < cfg.AnticipationNs)
            {
                s.outcome = SlotOutcome.Anticipation;
                var e = Make(DuelEventKind.Foul, r.slot);
                e.anticipation = true;
                e.reactionNs = r.reactionNs;
                events.Enqueue(e);
                CheckBothFoul();
            }
            return LastVerdict;
        }

        public ReportVerdict Validate(ShotReport r, long now)
        {
            if (r.slot < 0 || r.slot >= Slots) return ReportVerdict.BadSlot;
            if (r.roundId < Round.roundId) return ReportVerdict.Stale;
            if (r.roundId != Round.roundId) return ReportVerdict.WrongRound;
            if (Phase == RoundPhase.Standoff || Phase == RoundPhase.Result || Round.isVoid) return ReportVerdict.Stale;
            var s = Round.slots[r.slot];
            if (s.hasReport) return ReportVerdict.Duplicate;
            if (s.Settled) return ReportVerdict.Stale;

            if (r.kind == ReportKind.Early)
            {
                if (r.cuePresentedNs != 0 && r.inputNs >= r.cuePresentedNs) return ReportVerdict.Inconsistent;
                return ReportVerdict.Accepted;
            }
            if (r.kind != ReportKind.Shot) return ReportVerdict.Implausible;
            if (!Round.cueSent) return ReportVerdict.Implausible;
            if (r.reactionNs < 0) return ReportVerdict.Implausible;
            if (r.reactionNs > cfg.NoShotTimeoutNs + cfg.ReportSlackNs) return ReportVerdict.Implausible;
            long diff = (r.inputNs - r.cuePresentedNs) - r.reactionNs;
            if (diff > ConsistencyToleranceNs || diff < -ConsistencyToleranceNs) return ReportVerdict.Inconsistent;
            // A report cannot arrive before the reaction it claims could have happened after the cue was sent.
            if ((now - Round.cueDecidedNs) + cfg.ReportSlackNs < r.reactionNs) return ReportVerdict.Implausible;
            return ReportVerdict.Accepted;
        }

        // ------------------------------------------------------------------ internals

        private void CheckBothFoul()
        {
            if (Round.slots[0].IsFoul && Round.slots[1].IsFoul) DoVoid();
        }

        private void DoVoid()
        {
            Round.isVoid = true;
            Phase = RoundPhase.Standoff;
            forceCue = false;
            Push(DuelEventKind.RoundVoid, -1);
        }

        private bool IsPendingShot(SlotRecord s)
        {
            return s.hasReport && s.outcome == SlotOutcome.Pending && s.report.kind == ReportKind.Shot;
        }

        private void Commit(long now)
        {
            if (Phase != RoundPhase.Draw && Phase != RoundPhase.Resolving) return;
            for (int guard = 0; guard < 4; guard++)
            {
                var s0 = Round.slots[0];
                var s1 = Round.slots[1];
                bool p0 = IsPendingShot(s0), p1 = IsPendingShot(s1);
                if (!p0 && !p1) return;

                int a;
                if (p0 && p1) a = s0.reactionNs <= s1.reactionNs ? 0 : 1;
                else a = p0 ? 0 : 1;
                int b = 1 - a;
                var sa = Round.slots[a];
                var sb = Round.slots[b];

                if (IsPendingShot(sb))
                {
                    if (sb.reactionNs - sa.reactionNs <= cfg.TieWindowNs)
                    {
                        FireSimultaneous();
                        continue;
                    }
                    Fire(a, false);
                    if (Match.Hp[b] <= 0) Stop(b);
                    continue;
                }
                if (sb.Settled)
                {
                    Fire(a, false);
                    continue;
                }
                // b has not reported yet: commit a only once b can no longer claim a faster/tied reaction
                if (now - Round.cueDecidedNs >= sa.reactionNs + cfg.TieWindowNs + cfg.CommitGraceNs)
                {
                    Fire(a, false);
                    if (Match.Hp[b] <= 0) Stop(b);
                    continue;
                }
                return;
            }
        }

        private double DamageFor(long reactionNs)
        {
            // the round's multiplier (LAST LIGHT) is applied here and only here: one multiply per shot
            return DamageCurve.Evaluate(cfg, DuelConfig.NsToMs(reactionNs)) * Round.damageMultiplier;
        }

        private void Fire(int slot, bool simultaneous)
        {
            var s = Round.slots[slot];
            int target = 1 - slot;
            double dmg = DamageFor(s.reactionNs);
            Match.Hp[target] = Math.Max(0, Match.Hp[target] - dmg);
            CommitShot(slot, s, dmg, simultaneous);
        }

        private void FireSimultaneous()
        {
            var s0 = Round.slots[0];
            var s1 = Round.slots[1];
            double d0 = DamageFor(s0.reactionNs);
            double d1 = DamageFor(s1.reactionNs);
            // a tie: both shots land, whatever the HP (double KO possible). Decided only from the two
            // validated reactions, so every client sees the same outcome.
            Round.isTie = true;
            Match.Hp[1] = Math.Max(0, Match.Hp[1] - d0);
            Match.Hp[0] = Math.Max(0, Match.Hp[0] - d1);
            CommitShot(0, s0, d0, true);
            CommitShot(1, s1, d1, true);
        }

        private void CommitShot(int slot, SlotRecord s, double dmg, bool simultaneous)
        {
            int target = 1 - slot;
            s.outcome = SlotOutcome.Shot;
            s.damageDealt = dmg;
            s.grade = Grading.FromMs(cfg, DuelConfig.NsToMs(s.reactionNs));
            Match.RecordReaction(slot, s.reactionNs);
            Phase = RoundPhase.Resolving;
            var e = Make(DuelEventKind.Shot, slot);
            e.reactionNs = s.reactionNs;
            e.damage = dmg;
            e.targetHpAfter = Match.Hp[target];
            e.killed = Match.Hp[target] <= 0;
            e.simultaneous = simultaneous;
            events.Enqueue(e);
        }

        private void Stop(int slot)
        {
            var s = Round.slots[slot];
            if (s.Settled) return;
            s.outcome = SlotOutcome.Stopped;
            Push(DuelEventKind.Stopped, slot);
        }

        private void Timeouts(long now)
        {
            if (!Round.cueSent) return;
            long limit = cfg.NoShotTimeoutNs + cfg.ReportSlackNs;
            for (int i = 0; i < Slots; i++)
            {
                var s = Round.slots[i];
                if (!s.Settled && !s.hasReport && now - Round.cueDecidedNs > limit)
                {
                    s.outcome = SlotOutcome.NoShot;
                    Phase = RoundPhase.Resolving;
                    Push(DuelEventKind.NoShot, i);
                }
            }
        }

        private void Resolve()
        {
            double d0 = Round.slots[0].damageDealt, d1 = Round.slots[1].damageDealt;
            if (Round.isTie) Round.winnerSlot = -1;
            else Round.winnerSlot = d0 > d1 ? 0 : (d1 > d0 ? 1 : -1);
            if (Round.winnerSlot >= 0) Match.RoundWins[Round.winnerSlot]++;
            Phase = RoundPhase.Result;
            var re = Make(DuelEventKind.RoundResolved, Round.winnerSlot);
            re.simultaneous = Round.isTie;
            events.Enqueue(re);

            if (Match.Hp[0] <= 0 || Match.Hp[1] <= 0)
            {
                // both down (only possible in a tie) = drawn match: Over, Winner -1
                Match.Finish(Match.Hp[0] > 0 ? 0 : (Match.Hp[1] > 0 ? 1 : -1));
                Push(DuelEventKind.MatchOver, Match.Winner);
            }
        }

        private DuelEvent Make(DuelEventKind kind, int slot)
        {
            var e = new DuelEvent();
            e.kind = kind;
            e.slot = slot;
            e.roundId = Round.roundId;
            e.reactionNs = -1;
            return e;
        }

        private void Push(DuelEventKind kind, int slot)
        {
            events.Enqueue(Make(kind, slot));
        }
    }
}
