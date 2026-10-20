namespace PawNoon.Core
{
    /// <summary>Observer of the duel messages that pass through a <see cref="TapTransport"/>.</summary>
    public interface IDuelTap
    {
        /// <summary>The authority started a countdown (one per attempt, void replays included).</summary>
        void OnRoundStart(RoundStartMsg msg);

        /// <summary>A client sent a ShotReport (any slot), before the authority sees it.</summary>
        void OnReport(ShotReport report);
    }

    /// <summary>
    /// Pass-through transport that lets an observer see round starts and ShotReports as they are sent
    /// (the PvP recorder, SPEC_V2 §4.2). Delivery, order and contents are untouched; with no tap it is a
    /// plain forward. No allocation.
    /// </summary>
    public sealed class TapTransport : IDuelTransport
    {
        private readonly IDuelTransport inner;

        /// <summary>Null = no observer.</summary>
        public IDuelTap Tap;

        public TapTransport(IDuelTransport inner)
        {
            this.inner = inner ?? new LoopbackTransport();
        }

        public IDuelTransport Inner { get { return inner; } }

        public void SendReport(ShotReport report)
        {
            var t = Tap;
            if (t != null) t.OnReport(report);
            inner.SendReport(report);
        }

        public bool TryReceiveReport(out ShotReport report) { return inner.TryReceiveReport(out report); }

        public void BroadcastRoundStart(RoundStartMsg msg)
        {
            var t = Tap;
            if (t != null) t.OnRoundStart(msg);
            inner.BroadcastRoundStart(msg);
        }

        public void BroadcastCue(DrawCue cue) { inner.BroadcastCue(cue); }
        public bool TryReceiveRoundStart(int slot, out RoundStartMsg msg) { return inner.TryReceiveRoundStart(slot, out msg); }
        public bool TryReceiveCue(int slot, out DrawCue cue) { return inner.TryReceiveCue(slot, out cue); }
        public void Clear() { inner.Clear(); }
    }
}
