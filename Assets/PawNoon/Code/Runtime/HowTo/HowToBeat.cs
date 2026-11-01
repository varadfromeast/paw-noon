namespace PawNoon.Runtime
{
    /// <summary>
    /// One looping comic scene of HOW TO DUEL. The picture is a function of the scene time T (so a review hook can
    /// jump to any moment and hold it); sounds, haptics and the shake / flash requests fire in <see cref="Events"/>
    /// when T crosses a beat. Allocation-free per frame.
    /// </summary>
    public abstract class HowToBeat
    {
        protected readonly HowToStage S;
        protected float T;
        private bool on;

        protected HowToBeat(HowToStage stage) { S = stage; }

        /// <summary>Loop period of the scene (seconds).</summary>
        public abstract float Loop { get; }
        public float Time { get { return T; } }
        public bool On { get { return on; } }

        /// <summary>Shows the scene at scene time <paramref name="at"/> (no events for the time skipped).</summary>
        public void Begin(float at)
        {
            on = true;
            T = at < 0f ? 0f : (at >= Loop ? 0f : at);
            SetVisible(true);
            Restart();
        }

        public void End()
        {
            if (!on) return;
            on = false;
            SetVisible(false);
        }

        public void Tick(float dt)
        {
            if (!on || dt <= 0f) return;
            float prev = T;
            T += dt;
            if (T >= Loop)
            {
                T -= Loop;
                if (T >= Loop) T = 0f;
                prev = -1f;
                Restart();
            }
            Advance(dt);
            Events(prev, T);
        }

        /// <summary>Enables / disables every renderer of the scene.</summary>
        protected abstract void SetVisible(bool v);
        /// <summary>Back to the top of the loop (stateful parts: the heart monitor, the swipe cue).</summary>
        protected virtual void Restart() { }
        /// <summary>Stateful parts that run on real time (heart monitor, swipe cue).</summary>
        protected virtual void Advance(float dt) { }
        /// <summary>One-shot sounds / haptics / requests when T crosses a moment in (prev, t].</summary>
        protected abstract void Events(float prev, float t);
        /// <summary>Places every renderer for the current T at the stage's X / Y (called every frame).</summary>
        public abstract void Pose();

        /// <summary>Drop-in keyframes for a comic word or stamp (none under Reduced Motion).</summary>
        protected static readonly int[] PopKeys = { -6, -3, 0, -1, 0 };
        protected int Pop(float since) { return S.Reduced ? 0 : HowToStage.Steps(PopKeys, since, 0.04f); }
    }
}
