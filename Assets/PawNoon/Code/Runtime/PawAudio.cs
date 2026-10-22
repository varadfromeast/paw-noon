using System.Collections.Generic;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// SFX pool (no allocations per play), two-source music crossfade with ducking, stings,
    /// ambience loops and the hold heartbeat. Missing clips are skipped after one warning.
    /// </summary>
    public sealed class PawAudio : MonoBehaviour
    {
        public enum Track { None, Title, Standoff, DuelBed, DuelHold }

        private const int PoolSize = 16;
        private AudioSource[] pool;
        private int next;
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource musicA, musicB, ambWind, ambTown, ambDuel, heartbeat, draw;
        private float duelAmbTarget;
        private bool aIsCurrent = true;
        private Track track = Track.None;
        private bool classic;
        private float musicVol = 0.6f;
        private float fadeLen = 1.2f;
        private float duck = 1f, duckTarget = 1f;
        private float fade = 1f; // crossfade progress 0..1
        private float heartbeatTarget;
        public bool MusicOn = true;
        public bool SfxOn = true;

#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void PawAudio_UsePlaybackSession();
#endif

        /// <summary>iOS: play through the silent switch (Playback category; mixes with the player's own music).
        /// Called at start and on every resume, because iOS/Unity may restore their own category after an interruption.</summary>
        public static void ApplySession()
        {
#if UNITY_IOS && !UNITY_EDITOR
            PawAudio_UsePlaybackSession();
#endif
        }

        public void Init()
        {
            ApplySession();
            pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++) pool[i] = NewSource("sfx" + i, false);
            poolBasePitch = new float[PoolSize];
            for (int i = 0; i < PoolSize; i++) poolBasePitch[i] = 1f;
            musicA = NewSource("musicA", true);
            musicB = NewSource("musicB", true);
            ambWind = NewSource("ambWind", true);
            ambTown = NewSource("ambTown", true);
            ambDuel = NewSource("ambDuel", true);
            heartbeat = NewSource("heartbeat", true);
            draw = NewSource("drawCue", false);
            draw.priority = 0;

            string[] sfx =
            {
                "sfx_tick_3", "sfx_tick_2", "sfx_tick_1", "sfx_tick_sub", "sfx_draw", "sfx_gun_hero_1", "sfx_gun_hero_2", "sfx_gun_hero_3",
                "sfx_gun_bandit_1", "sfx_gun_bandit_2", "sfx_gun_bandit_3", "sfx_cock", "sfx_holster", "sfx_holster_draw", "sfx_misfire",
                "sfx_hit", "sfx_hit_cat", "sfx_shell", "sfx_bodyfall", "sfx_spurs", "sfx_stamp",
                "sfx_whoosh", "sfx_lightning", "sfx_hp_tick", "sfx_ui_tap", "sfx_ui_back", "sfx_ui_toggle", "sfx_heartbeat",
                "sfx_meow_whiskers", "sfx_meow_pawtruth",  // per-character "meow signature" (SPEC_V2 §8.2, Tools/AudioGen/gen_voice.py)
                "sfx_peacemaker_draw", "sfx_gun_peacemaker_1", "sfx_gun_peacemaker_2", "sfx_gun_peacemaker_3",  // M7 (gen_peacemaker.py)
                "sfx_streak", "sfx_new_best"   // outcome moments (gen_sfx.py, Docs/FEEL.md)
            };
            // every key Play() is called with must be here (Play only looks up preloaded clips; Tools/Lint/check_cs.py checks it)
            for (int i = 0; i < sfx.Length; i++) Clip("Audio/SFX/" + sfx[i], sfx[i]);
            draw.clip = Get("sfx_draw");
        }

        private AudioSource NewSource(string n, bool loop)
        {
            var go = new GameObject(n);
            go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.loop = loop;
            s.spatialBlend = 0f;
            s.dopplerLevel = 0f;
            return s;
        }

        private AudioClip Clip(string path, string key)
        {
            AudioClip c;
            if (clips.TryGetValue(key, out c)) return c;
            c = Resources.Load<AudioClip>(path);
            if (c == null) ArtLib.Warn("audio:" + path, "Missing audio Resources/" + path + " (skipped)");
            clips[key] = c;
            return c;
        }

        private AudioClip Get(string key)
        {
            AudioClip c;
            return clips.TryGetValue(key, out c) ? c : null;
        }

        public void Play(string key, float volume = 1f, float pitch = 1f)
        {
            if (!SfxOn) return;
            var c = Get(key);
            if (c == null) return;
            var s = pool[next];
            next = (next + 1) % PoolSize;
            s.Stop();
            s.clip = c;
            s.volume = volume;
            poolBasePitch[(next + PoolSize - 1) % PoolSize] = pitch;
            s.pitch = pitch * curPitch;
            s.Play();
        }

        /// <summary>
        /// A character's "meow signature" when his speech bubble pops: volume 0.6, pitch +-4 %. Keys outside the
        /// registered set (a new cat in characters.json) are loaded from Resources/Audio/SFX once, then cached.
        /// </summary>
        public void PlayMeow(string key)
        {
            if (!SfxOn || string.IsNullOrEmpty(key)) return;
            if (!clips.ContainsKey(key)) Clip("Audio/SFX/" + key, key);
            Play(key, 0.6f, Random.Range(0.96f, 1.04f));
        }

        /// <summary>Plays key_1..key_count at random with slight pitch variance.</summary>
        public void PlayVariant(string prefix, int count, float volume = 1f)
        {
            if (!SfxOn || count <= 0) return;
            string[] keys;
            if (!variants.TryGetValue(prefix, out keys) || keys.Length != count)
            {
                keys = new string[count];
                for (int i = 0; i < count; i++) keys[i] = prefix + "_" + (i + 1);
                variants[prefix] = keys;
            }
            Play(keys[Random.Range(0, count)], volume, Random.Range(0.96f, 1.04f));
        }

        private readonly Dictionary<string, string[]> variants = new Dictionary<string, string[]>();

        /// <summary>The DRAW sound, scheduled on the DSP clock to coincide with the estimated photon time.</summary>
        public void PlayDrawAt(double dspTime)
        {
            if (!SfxOn || draw.clip == null) return;
            draw.Stop();
            draw.volume = 1f;
            if (dspTime <= AudioSettings.dspTime) draw.Play();
            else draw.PlayScheduled(dspTime);
        }

        public void SetMusicStyle(bool classicStyle)
        {
            if (classic == classicStyle) return;
            classic = classicStyle;
            var t = track;
            track = Track.None;
            PlayMusic(t);
        }

        private AudioClip MusicClip(Track t)
        {
            if (t == Track.Title) return classic ? Clip("Audio/Music/Classic/Lobby", "m_lobby") : Clip("Audio/Music/music_title", "m_title");
            if (t == Track.Standoff) return classic ? Clip("Audio/Music/Classic/Standoff", "m_cstandoff") : Clip("Audio/Music/music_standoff", "m_standoff");
            // composed duel score (v3): bed from round start, crossfade to the hold loop after "1"
            if (t == Track.DuelBed) return classic ? Clip("Audio/Music/Classic/Standoff", "m_cstandoff") : (Clip("Audio/Music/music_duel_a", "m_duel_a") ?? Clip("Audio/Music/music_standoff", "m_standoff"));
            if (t == Track.DuelHold) return classic ? null : Clip("Audio/Music/music_duel_hold", "m_duel_hold");
            return null;
        }

        /// <summary>Crossfade length for the next PlayMusic (seconds); resets to 1.2 after use.</summary>
        public float NextFadeSeconds = 1.2f;

        public void PlayMusic(Track t)
        {
            if (track == t) return;
            track = t;
            var incoming = aIsCurrent ? musicB : musicA;
            var clip = MusicClip(t);
            incoming.Stop();
            incoming.clip = clip;
            incoming.volume = 0f;
            if (clip != null) incoming.Play();
            aIsCurrent = !aIsCurrent;
            fade = 0f;
            fadeLen = Mathf.Max(0.05f, NextFadeSeconds);
            NextFadeSeconds = 1.2f;
        }

        public void Sting(string key, float volume = 0.9f)
        {
            if (!MusicOn) return;
            // cached lookup first: building the Resources path on every sting allocated a string per call
            AudioClip c;
            if (!clips.TryGetValue(key, out c)) c = Clip("Audio/Music/" + key, key);
            if (c == null) return;
            var s = pool[next];
            next = (next + 1) % PoolSize;
            s.Stop();
            s.clip = c;
            s.volume = volume;
            poolBasePitch[(next + PoolSize - 1) % PoolSize] = 1f;
            s.pitch = 1f;
            s.Play();
        }

        public void StartAmbience()
        {
            StartLoop(ambWind, "Audio/Loops/amb_wind", "amb_wind");
            StartLoop(ambTown, "Audio/Loops/amb_town", "amb_town");
            StartLoop(ambDuel, "Audio/Loops/amb_duel", "amb_duel");
            var hb = Clip("Audio/Loops/heartbeat", "heartbeat");
            heartbeat.clip = hb;
            heartbeat.volume = 0f;
            if (hb != null) heartbeat.Play();
        }

        private void StartLoop(AudioSource s, string path, string key)
        {
            var c = Clip(path, key);
            if (c == null) return;
            s.clip = c;
            s.volume = 0f;
            s.Play();
        }

        /// <summary>Pitch for SFX/ambience under slow-mo (music and the DRAW cue are left alone).</summary>
        public void SetPitch(float p)
        {
            p = Mathf.Clamp(p, 0.5f, 1f);
            if (Mathf.Abs(p - curPitch) < 0.002f) return;
            curPitch = p;
            for (int i = 0; i < pool.Length; i++) if (pool[i] != null) pool[i].pitch = p * poolBasePitch[i];
            ambWind.pitch = p; ambTown.pitch = p; ambDuel.pitch = p; heartbeat.pitch = p;
        }
        private float curPitch = 1f;
        private float[] poolBasePitch;

        public void Duck(float target) { duckTarget = Mathf.Clamp01(target); }
        /// <summary>1 = duel soundscape (dry wind + creaks, no music); 0 = menu ambience.</summary>
        public void DuelAmbience(float target) { duelAmbTarget = Mathf.Clamp01(target); }
        public void Heartbeat(float target) { heartbeatTarget = Mathf.Clamp01(target); }

        private void Update()
        {
            float dt = PawTime.UnscaledDt;
            fade = Mathf.Min(1f, fade + dt / fadeLen);
            duck = Mathf.MoveTowards(duck, duckTarget, dt * 1.5f);
            float m = MusicOn ? musicVol * duck : 0f;
            var cur = aIsCurrent ? musicA : musicB;
            var old = aIsCurrent ? musicB : musicA;
            cur.volume = m * fade;
            old.volume = m * (1f - fade);
            if (fade >= 1f && old.isPlaying) old.Stop();

            float amb = SfxOn ? 1f : 0f;
            ambWind.volume = Mathf.MoveTowards(ambWind.volume, 0.35f * amb * (1f - duelAmbTarget), dt);
            ambTown.volume = Mathf.MoveTowards(ambTown.volume, 0.22f * amb * (0.5f + 0.5f * duck) * (1f - duelAmbTarget), dt);
            ambDuel.volume = Mathf.MoveTowards(ambDuel.volume, 0.9f * amb * duelAmbTarget, dt * 0.7f);
            heartbeat.volume = Mathf.MoveTowards(heartbeat.volume, SfxOn ? heartbeatTarget * 1.0f : 0f, dt * 0.9f);
        }
    }
}
