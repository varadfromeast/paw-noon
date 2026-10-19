namespace PawNoon.Core
{
    public enum PressResult
    {
        Ignored = 0,
        /// <summary>Countdown running, cue not yet presented (or press timestamp before the cue photons): TOO EARLY.</summary>
        Early = 1,
        Shot = 2,
        /// <summary>After the no-shot timeout.</summary>
        TooLate = 3,
        /// <summary>Already reported this round.</summary>
        AlreadyReported = 4
    }

    /// <summary>
    /// One duelist's local measurement endpoint. Receives RoundStart / DrawCue from the transport,
    /// stamps when the cue is (estimated to be) presented on its own clock, classifies presses and
    /// sends a ShotReport. Identical for the local player, the bot and (later) a remote player.
    /// </summary>
    public sealed class DuelClient
    {
        public readonly int Slot;
        private readonly IDuelTransport transport;
        private readonly DuelConfig cfg;

        public int RoundId { get; private set; }
        public int RoundNumber { get; private set; }
        public bool InRound { get; private set; }
        public long CountdownStartNs { get; private set; }
        public int CountdownBeats { get; private set; }
        public long BeatNs { get; private set; }
        public bool HasCue { get; private set; }
        public long CueDecidedNs { get; private set; }
        public long CuePresentedNs { get; private set; }
        public bool Reported { get; private set; }
        public bool Closed { get; private set; }
        public ShotReport LastReport { get; private set; }

        public DuelClient(int slot, IDuelTransport transport, DuelConfig cfg)
        {
            Slot = slot;
            this.transport = transport;
            this.cfg = cfg;
            RoundId = -1;
        }

        public void Reset()
        {
            InRound = false;
            HasCue = false;
            Reported = false;
            Closed = false;
            CueDecidedNs = 0;
            CuePresentedNs = 0;
        }

        /// <summary>Call at frame start. Returns true when a new round's countdown begins this frame.</summary>
        public bool PollRoundStart(long frameStartNs, out RoundStartMsg msg)
        {
            bool got = false;
            msg = default(RoundStartMsg);
            RoundStartMsg m;
            while (transport.TryReceiveRoundStart(Slot, out m))
            {
                Reset();
                msg = m;
                got = true;
                RoundId = m.roundId;
                RoundNumber = m.roundNumber;
                CountdownBeats = m.countdownBeats;
                BeatNs = m.beatNs;
                CountdownStartNs = frameStartNs;
                InRound = true;
            }
            return got;
        }

        /// <summary>
        /// Call at frame start, before rendering. If a cue for the current round arrived, the caller
        /// must show DRAW in this very frame. cuePresented = frameStart + estimated present latency.
        /// </summary>
        public bool PollCue(long frameStartNs, long presentLatencyNs)
        {
            bool got = false;
            DrawCue c;
            while (transport.TryReceiveCue(Slot, out c))
            {
                if (!InRound || c.roundId != RoundId || HasCue) continue; // stale or duplicate
                HasCue = true;
                CueDecidedNs = frameStartNs;
                CuePresentedNs = frameStartNs + (presentLatencyNs < 0 ? 0 : presentLatencyNs);
                got = true;
            }
            return got;
        }

        /// <summary>Closes the window after the no-shot timeout (no report is sent; the authority times out).</summary>
        public void Tick(long nowNs)
        {
            if (InRound && HasCue && !Reported && !Closed && nowNs - CuePresentedNs > cfg.NoShotTimeoutNs)
                Closed = true;
        }

        /// <summary>Elapsed since the cue photons, for the live timer (0 before the cue).</summary>
        public long ElapsedSinceCueNs(long nowNs)
        {
            if (!HasCue) return 0;
            long e = nowNs - CuePresentedNs;
            return e < 0 ? 0 : e;
        }

        public PressResult Press(long inputNs, InputSource source, bool frameQuantized)
        {
            if (!InRound) return PressResult.Ignored;
            if (Reported) return PressResult.AlreadyReported;
            if (Closed) return PressResult.TooLate;
            if (inputNs < CountdownStartNs) return PressResult.Ignored; // before "3": standoff, harmless

            var r = new ShotReport();
            r.roundId = RoundId;
            r.slot = Slot;
            r.inputNs = inputNs;
            r.inputSource = source;
            r.frameQuantized = frameQuantized;

            if (!HasCue || inputNs < CuePresentedNs)
            {
                r.kind = ReportKind.Early;
                r.cuePresentedNs = HasCue ? CuePresentedNs : 0;
                r.reactionNs = HasCue ? inputNs - CuePresentedNs : 0;
                Send(r);
                return PressResult.Early;
            }

            long reaction = inputNs - CuePresentedNs;
            if (reaction > cfg.NoShotTimeoutNs)
            {
                Closed = true;
                return PressResult.TooLate;
            }
            r.kind = ReportKind.Shot;
            r.cuePresentedNs = CuePresentedNs;
            r.reactionNs = reaction;
            Send(r);
            return PressResult.Shot;
        }

        private void Send(ShotReport r)
        {
            Reported = true;
            LastReport = r;
            transport.SendReport(r);
        }
    }
}
