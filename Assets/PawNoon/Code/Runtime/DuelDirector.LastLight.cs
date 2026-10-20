using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Sudden death "LAST LIGHT" (SPEC_V2 §8.4), kept apart from the main director file.
    /// Rule: SuddenDeath.MultiplierFor (Core) is handed to the authority at the start of every standoff, so the
    /// authority applies it once per shot for the next round; everything else here is presentation: dawn plate,
    /// "LAST LIGHT" round sign, ECG flat line + spike before the countdown, music duck, louder heartbeat.
    /// All of it is off when cfg.suddenDeathEnabled is false.
    /// </summary>
    public sealed partial class DuelDirector
    {
        public const string LastLightLabel = "LAST LIGHT";
        /// <summary>Standoff time after the flat line: the spike, a breath, then "3".</summary>
        private const float LastLightSpikeTail = 0.55f;

        private StreetScene street;
        private bool lastLight;

        /// <summary>True while the current / next exchange is LAST LIGHT.</summary>
        public bool LastLight { get { return lastLight; } }

        /// <summary>Optional: the street, for the dawn crossfade (PawGame wires it after construction).</summary>
        public void SetStreet(StreetScene s) { street = s; }

        /// <summary>Start of every standoff: decide LAST LIGHT from the HP right now and arm the authority.</summary>
        private void ApplySuddenDeath()
        {
            var m = authority.Match;
            lastLight = SuddenDeath.Triggers(cfg, m);
            // x2 (or x1, which also restores a previous LAST LIGHT) for the round this standoff leads into
            authority.SetDamageMultiplier(SuddenDeath.MultiplierFor(cfg, m));
            if (street != null) street.SetDawn(lastLight ? 1f : 0f);
            hud.SetBadgeLastLight(lastLight);
        }

        /// <summary>After the normal standoff set-up: swap GET READY for the flat line and the round sign.</summary>
        private void PresentLastLightStandoff()
        {
            if (!lastLight) return;
            float flat = (float)cfg.suddenDeathFlatLineSeconds;
            cues.ShowFlatLine(flat);
            float need = flat + LastLightSpikeTail;
            if (stateTimer < need) stateTimer = need;
            hud.SetRoundLabel(LastLightLabel);
        }

        private void ShowRoundSign(int roundNumber)
        {
            if (lastLight) hud.SetRoundLabel(LastLightLabel);
            else hud.SetRound(roundNumber);
        }

        /// <summary>Back to noon and x1 (match end, Stop, review).</summary>
        private void EndLastLight()
        {
            lastLight = false;
            authority.SetDamageMultiplier(1.0);
            if (street != null) street.SetDawn(0f);
            hud.SetBadgeLastLight(false);
        }

        /// <summary>Hold music gain on top of the normal hold duck: -suddenDeathMusicDuckDb during LAST LIGHT.</summary>
        private float LastLightMusicGain
        {
            get { return lastLight ? Mathf.Pow(10f, -(float)cfg.suddenDeathMusicDuckDb / 20f) : 1f; }
        }

        /// <summary>Hold heartbeat one-shot volume multiplier (x1.1 during LAST LIGHT; AudioSource caps at 1).</summary>
        private float LastLightHeartGain
        {
            get { return lastLight ? (float)cfg.suddenDeathHeartbeatGain : 1f; }
        }

        /// <summary>Heart monitor beat during the LAST LIGHT standoff: the spike after the flat line hits hard.</summary>
        private void LastLightStandoffBeat(int index)
        {
            if (!lastLight) return;
            float g = LastLightHeartGain;
            if (index == 0)
            {
                audio.Play("sfx_heartbeat", Mathf.Min(1f, g), 0.92f);
                audio.Play("sfx_tick_sub", 0.6f);   // low thump under it (silent if missing)
                PawHaptics.Impact(2);
                if (!reducedMotion)
                {
                    beatThump = (float)cfg.beatVignette * 1.6f;
                    beatThumpT = 0f;
                }
            }
            else
            {
                audio.Play("sfx_heartbeat", Mathf.Min(1f, 0.6f * g), 1f);
            }
        }

        // ------------------------------------------------------------------ review / bridge

        /// <summary>
        /// Review: sets both HP (hero = slot 0, bandit = slot 1) between exchanges, e.g. 20 20 to force LAST LIGHT.
        /// In a standoff the standoff restarts so the rule is re-evaluated now; in a result it applies from the
        /// next standoff. Refused mid-exchange (would change a round in flight) and outside a duel.
        /// </summary>
        public string ReviewSetHp(double heroHp, double banditHp)
        {
            if (State == Pres.Off || State == Pres.Review) return "error: no duel running (ReviewDirector.StartDuel first)";
            if (State == Pres.Live) return "error: an exchange is in flight; retry in the standoff or on the result card";
            if (State == Pres.MatchEnd || authority.Match.Over) return "error: the match is over";
            var m = authority.Match;
            m.Hp[0] = Clamp(heroHp, 1, m.MaxHp);
            m.Hp[1] = Clamp(banditHp, 1, m.MaxHp);
            hud.SetHpImmediate(0, (float)(m.Hp[0] / m.MaxHp));
            hud.SetHpImmediate(1, (float)(m.Hp[1] / m.MaxHp));
            bool sd = SuddenDeath.Triggers(cfg, m);
            if (State == Pres.Standoff) BeginStandoff(0.9f);
            return "ok: hp " + m.Hp[0].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "/" +
                   m.Hp[1].ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   (sd ? " -> LAST LIGHT" : "") + (State == Pres.Standoff ? " (now)" : " (next standoff)");
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (double.IsNaN(v)) return hi;
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Review still: a LAST LIGHT standoff (dawn, brass sign, flat-lining monitor, low HP).</summary>
        public void ReviewLastLight()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetHpImmediate(0, 0.18f);
            hud.SetHpImmediate(1, 0.12f);
            hud.SetRoundLabel(LastLightLabel);
            hud.SetBadgeLastLight(true);
            hero.Play("ready");
            bandit.Play("ready");
            hud.ShowHolster(true);
            if (street != null) street.SetDawn(1f);
            cues.ShowFlatLine((float)cfg.suddenDeathFlatLineSeconds);
        }
    }
}
