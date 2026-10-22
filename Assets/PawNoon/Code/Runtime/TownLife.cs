using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>Per-match weather (SPEC_V2 §8.8).</summary>
    public enum TownWeather { Clear, Dusk, Storm }

    /// <summary>Townsfolk and weather tunables (feel_config.json, keys town* / weather*). Presentation only.</summary>
    public sealed class TownTuning
    {
        public bool townEnabled = true;
        /// <summary>Match start to the first peek (also waits for the match intro).</summary>
        public double peekDelaySeconds = 0.9;
        /// <summary>Ducked frame shown before a hidden onlooker pops into the peek (the "rising" beat).</summary>
        public double riseSeconds = 0.18;
        public double blinkMinSeconds = 2.4;
        public double blinkMaxSeconds = 4.8;
        public double blinkSeconds = 0.14;
        /// <summary>Idle sway: 1 px left / right every this many seconds (ambient time, so it stops in the hold).</summary>
        public double swaySeconds = 1.7;
        public double duckStaggerSeconds = 0.07;
        public double peekBackSeconds = 0.45;
        public double peekBackStaggerSeconds = 0.2;
        public double cheerSeconds = 1.1;
        public int popPx = 3;
        public int hatFlyPx = 4;
        public double creakVolume = 0.45;
        public bool weatherEnabled = true;
        public int duskOneIn = 6;
        public int stormOneIn = 10;
        /// <summary>No weather until this many matches have been started on the install (tutorial never counts).</summary>
        public int weatherMinMatches = 3;
        /// <summary>Dawn plate floor during a dusk match (stepped to 1/8 by the street).</summary>
        public double duskDawn = 0.75;
        /// <summary>Added to the pipeline's warm grade during dusk (clamped to 1).</summary>
        public double duskWarm = 0.3;
        public double stormDarken = 0.1;
        public int stormMotes = 24;
        public double stormWindPx = 46;
        public double stormTumbleSway = 1.5;

        public static TownTuning Read(object root)
        {
            var t = new TownTuning();
            if (root == null) return t;
            t.townEnabled = PnJson.Bool(root, "townEnabled", t.townEnabled);
            t.peekDelaySeconds = PnJson.Num(root, "townPeekDelaySeconds", t.peekDelaySeconds);
            t.riseSeconds = PnJson.Num(root, "townRiseSeconds", t.riseSeconds);
            t.blinkMinSeconds = PnJson.Num(root, "townBlinkMinSeconds", t.blinkMinSeconds);
            t.blinkMaxSeconds = PnJson.Num(root, "townBlinkMaxSeconds", t.blinkMaxSeconds);
            t.blinkSeconds = PnJson.Num(root, "townBlinkSeconds", t.blinkSeconds);
            t.swaySeconds = PnJson.Num(root, "townSwaySeconds", t.swaySeconds);
            t.duckStaggerSeconds = PnJson.Num(root, "townDuckStaggerSeconds", t.duckStaggerSeconds);
            t.peekBackSeconds = PnJson.Num(root, "townPeekBackSeconds", t.peekBackSeconds);
            t.peekBackStaggerSeconds = PnJson.Num(root, "townPeekBackStaggerSeconds", t.peekBackStaggerSeconds);
            t.cheerSeconds = PnJson.Num(root, "townCheerSeconds", t.cheerSeconds);
            t.popPx = PnJson.Int(root, "townPopPx", t.popPx);
            t.hatFlyPx = PnJson.Int(root, "townHatFlyPx", t.hatFlyPx);
            t.creakVolume = PnJson.Num(root, "townCreakVolume", t.creakVolume);
            t.weatherEnabled = PnJson.Bool(root, "weatherEnabled", t.weatherEnabled);
            t.duskOneIn = PnJson.Int(root, "weatherDuskOneIn", t.duskOneIn);
            t.stormOneIn = PnJson.Int(root, "weatherStormOneIn", t.stormOneIn);
            t.weatherMinMatches = PnJson.Int(root, "weatherMinMatches", t.weatherMinMatches);
            t.duskDawn = PnJson.Num(root, "weatherDuskDawn", t.duskDawn);
            t.duskWarm = PnJson.Num(root, "weatherDuskWarm", t.duskWarm);
            t.stormDarken = PnJson.Num(root, "weatherStormDarken", t.stormDarken);
            t.stormMotes = PnJson.Int(root, "weatherStormMotes", t.stormMotes);
            t.stormWindPx = PnJson.Num(root, "weatherStormWindPx", t.stormWindPx);
            t.stormTumbleSway = PnJson.Num(root, "weatherStormTumbleSway", t.stormTumbleSway);
            t.Sanitize();
            return t;
        }

        public void Sanitize()
        {
            if (blinkMaxSeconds < blinkMinSeconds) blinkMaxSeconds = blinkMinSeconds;
            if (swaySeconds < 0.2) swaySeconds = 0.2;
            popPx = Mathf.Clamp(popPx, 0, 6);
            hatFlyPx = Mathf.Clamp(hatFlyPx, 0, 8);
            stormMotes = Mathf.Clamp(stormMotes, 0, 60);
            weatherMinMatches = Mathf.Max(0, weatherMinMatches);
        }

        /// <summary>
        /// The match's weather from its seed alone (ghost / recorded duels replay the same sky): one uniform draw,
        /// [0, 1/duskOneIn) dusk, then [.., + 1/stormOneIn) storm, else clear. A one-in of 0 or less disables that kind.
        /// </summary>
        public static TownWeather Pick(int seed, int duskOneIn, int stormOneIn)
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= h >> 15;
            h *= 0x85EBCA77u;
            h ^= h >> 13;
            h *= 0xC2B2AE3Du;
            h ^= h >> 16;
            double u = (h & 0xFFFFFF) / 16777216.0;
            double dusk = duskOneIn > 0 ? 1.0 / duskOneIn : 0.0;
            double storm = stormOneIn > 0 ? 1.0 / stormOneIn : 0.0;
            if (u < dusk) return TownWeather.Dusk;
            if (u < dusk + storm) return TownWeather.Storm;
            return TownWeather.Clear;
        }
    }

    /// <summary>
    /// "The town comes alive" (SPEC_V2 §8.3) and the weather pass (§8.8). Presentation only: driven by DuelDirector
    /// hooks (DuelDirector.Town.cs), never reads or changes timing, input or rules.
    /// Two onlookers (fx.json town_bonnet / town_bowler, cover patches cut from the plate drawn over them) peek
    /// during the standoff (slow blink + 1 px sway on ambient time), duck at "1" with a few pixels of dust, peek back
    /// after the exchange, cheer on LIGHTNING (pop up, the bowler hat flies 4 px and bounces). Docs/TOWN.md.
    /// </summary>
    public sealed class TownLife : MonoBehaviour
    {
        private enum Mode { Hidden, Peek, Duck }

        private sealed class Onlooker
        {
            public SheetDef sheet;
            public SpriteRenderer body, hat, coverNoon, coverDawn;
            public Vector2Int at, coverAt, hatOffset;
            public int duckDrop;
            public Mode mode = Mode.Hidden;
            public Mode pending = Mode.Hidden;
            public float pendingT = -1f;    // seconds until `pending` applies (< 0 = none)
            public float riseT;             // > 0: hidden -> peek shows the ducked frame first
            public float blinkIn, blinkLeft;
            public float swayT;
            public int swayX;
            public float cheerT = -1f;
            public int frameShown = -1;
        }

        private const int OrderBody = 3, OrderHat = 4, OrderCover = 5, OrderCoverDawn = 6, OrderDust = 7;
        private static readonly int[] HatTimesMs = { 60, 200, 260, 300, 360, 420 };
        private static readonly int[] HatFlyPx = { -2, -4, -3, -1, 0, -1, 0 };   // stepped: up 4, land, 1-px bounce

        private readonly Onlooker[] folk = new Onlooker[2];
        private TownTuning cfg = new TownTuning();
        private StreetScene street;
        private PixelParticles particles;
        private PixelPipeline pipe;
        private PawAudio audioMgr;
        private MatchIntro intro;
        private float baseWarm = 0.6f;
        private bool reducedMotion;
        private bool active;
        private float firstPeekT = -1f;
        private bool ducking;
        private bool cheerHold;
        private TownWeather weather = TownWeather.Clear;

        public TownWeather Weather { get { return weather; } }
        public bool Active { get { return active; } }

        public void Init(SpriteCatalog fx, StreetScene s, PixelParticles parts, PixelPipeline p, PawAudio audio, TownTuning tuning)
        {
            street = s;
            particles = parts;
            pipe = p;
            audioMgr = audio;
            if (tuning != null) cfg = tuning;
            baseWarm = p != null ? p.Warm : 0.6f;
            var manifest = ArtLib.Json("Art/FX/fx", false);
            folk[0] = Build(fx, manifest, "town_bonnet", "TownBonnet");
            folk[1] = Build(fx, manifest, "town_bowler", "TownBowler");
            HideAll();
        }

        /// <summary>The first peek waits while the match intro walks the cats in.</summary>
        public void SetIntro(MatchIntro i) { intro = i; }

        private Onlooker Build(SpriteCatalog fx, object manifest, string name, string goName)
        {
            if (fx == null) return null;
            var sheet = fx.TryGet(name);
            if (sheet == null || sheet.Count < 3) return null;
            var entry = PnJson.Obj(manifest, name);
            var o = new Onlooker();
            o.sheet = sheet;
            int a = 0, b = 0;
            if (!PnJson.Pair(entry, "at", ref a, ref b)) return null;
            o.at = new Vector2Int(a, b);
            o.duckDrop = PnJson.Int(entry, "duckDrop", 8);
            o.body = PawGfx.NewSprite(goName, transform, OrderBody);
            o.body.sprite = sheet.Frames[0];
            string hatName = PnJson.Str(entry, "hat", "");
            var hatSheet = hatName.Length > 0 ? fx.TryGet(hatName) : null;
            if (hatSheet != null)
            {
                a = 0; b = -11;
                PnJson.Pair(entry, "hatOffset", ref a, ref b);
                o.hatOffset = new Vector2Int(a, b);
                o.hat = PawGfx.NewSprite(goName + "Hat", transform, OrderHat);
                o.hat.sprite = hatSheet.First;
            }
            string coverName = PnJson.Str(entry, "cover", "");
            var cover = coverName.Length > 0 ? fx.TryGet(coverName) : null;
            if (cover != null)
            {
                var ce = PnJson.Obj(manifest, coverName);
                a = 0; b = 0;
                PnJson.Pair(ce, "at", ref a, ref b);
                o.coverAt = new Vector2Int(a, b);
                o.coverNoon = PawGfx.NewSprite(goName + "Cover", transform, OrderCover);
                o.coverNoon.sprite = cover.First;
                PawGfx.Place(o.coverNoon, a, b);
                if (cover.Count > 1)
                {
                    o.coverDawn = PawGfx.NewSprite(goName + "CoverDawn", transform, OrderCoverDawn);
                    o.coverDawn.sprite = cover.Frames[1];
                    PawGfx.Place(o.coverDawn, a, b);
                }
            }
            else return null;   // without its cover the onlooker would float in front of the barrel
            o.blinkIn = Random.Range((float)cfg.blinkMinSeconds, (float)cfg.blinkMaxSeconds);
            return o;
        }

        public void SetReducedMotion(bool on)
        {
            if (reducedMotion == on) return;
            reducedMotion = on;
            if (weather == TownWeather.Storm) ApplyWeather(weather);   // storm motes off / back on
        }

        // ------------------------------------------------------------------ match hooks

        /// <summary>Match start: picks the weather (seeded) unless gated, hides the onlookers and schedules the first peek + creak.</summary>
        public void BeginMatch(int matchSeed, bool allowWeather)
        {
            HideAll();
            active = cfg.townEnabled && (folk[0] != null || folk[1] != null);
            firstPeekT = active ? Mathf.Max(0.05f, (float)cfg.peekDelaySeconds) : -1f;
            ducking = false;
            cheerHold = false;
            var w = TownWeather.Clear;
            if (allowWeather && cfg.weatherEnabled && PawSettings.MatchesPlayed >= cfg.weatherMinMatches)
                w = TownTuning.Pick(matchSeed, cfg.duskOneIn, cfg.stormOneIn);
            PawSettings.CountMatch();
            ApplyWeather(w);
        }

        /// <summary>Every standoff: anyone still ducked peeks back out.</summary>
        public void Standoff()
        {
            ducking = false;
            if (!active) return;
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null || o.mode == Mode.Hidden) continue;
                if (o.mode == Mode.Duck && o.pending != Mode.Peek) Request(o, Mode.Peek, i * (float)cfg.peekBackStaggerSeconds);
            }
        }

        /// <summary>Countdown "1": both duck behind their cover, a pinch of dust off the rim.</summary>
        public void Duck()
        {
            ducking = true;
            if (!active) return;
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null || o.mode == Mode.Hidden) continue;
                Request(o, Mode.Duck, i * (float)cfg.duckStaggerSeconds);
            }
        }

        /// <summary>NO SHOT: one onlooker (left) peeks early and a tumbleweed rolls through.</summary>
        public void NoShot()
        {
            if (street != null) street.LaunchTumbleweed();
            if (!active) return;
            var o = folk[0] ?? folk[1];
            if (o != null && o.mode == Mode.Duck) Request(o, Mode.Peek, 0.12f);
        }

        /// <summary>Exchange over (round card): peek back out; LIGHTNING = both pop up at once and the bowler flies.</summary>
        public void ExchangeOver(bool lightning)
        {
            ducking = false;
            if (!active) return;
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null || o.mode == Mode.Hidden) continue;
                if (lightning)
                {
                    o.pending = Mode.Hidden;
                    o.pendingT = -1f;
                    SetMode(o, Mode.Peek);
                    o.cheerT = 0f;
                }
                else if (o.mode == Mode.Duck)
                {
                    Request(o, Mode.Peek, (float)cfg.peekBackSeconds + i * (float)cfg.peekBackStaggerSeconds);
                }
            }
        }

        /// <summary>Back to the title / review reset: onlookers gone, sky clear.</summary>
        public void EndMatch()
        {
            active = false;
            firstPeekT = -1f;
            HideAll();
            ApplyWeather(TownWeather.Clear);
        }

        // ------------------------------------------------------------------ weather

        public void ApplyWeather(TownWeather w)
        {
            weather = w;
            if (street != null)
            {
                street.SetWeatherDawn(w == TownWeather.Dusk ? (float)cfg.duskDawn : 0f);
                street.SetPlateTint(w == TownWeather.Storm ? 1f - (float)cfg.stormDarken : 1f);
                street.SetWindSway(w == TownWeather.Storm ? (float)cfg.stormTumbleSway : 1f);
            }
            if (pipe != null) pipe.Warm = w == TownWeather.Dusk ? Mathf.Clamp01(baseWarm + (float)cfg.duskWarm) : baseWarm;
            if (particles != null)
            {
                particles.ClearWind();
                if (w == TownWeather.Storm && !reducedMotion) particles.SpawnWind(cfg.stormMotes, 66, (float)cfg.stormWindPx);
            }
        }

        // ------------------------------------------------------------------ review

        /// <summary>Review stills: "peek", "duck", "cheer" (animated), "cheerhold" (popped, hat at the top), "hide".</summary>
        public string ReviewSet(string mode)
        {
            if (folk[0] == null && folk[1] == null) return "error: townsfolk art missing (fx.json town_bonnet / town_bowler + covers)";
            active = true;
            firstPeekT = -1f;
            cheerHold = false;
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null) continue;
                o.pending = Mode.Hidden;
                o.pendingT = -1f;
                o.riseT = 0f;
                o.cheerT = -1f;
                switch (mode)
                {
                    case "duck": SetMode(o, Mode.Duck); break;
                    case "cheer": SetMode(o, Mode.Peek); o.cheerT = 0f; break;
                    case "cheerhold": SetMode(o, Mode.Peek); o.cheerT = 0.1f; break;
                    case "hide": SetMode(o, Mode.Hidden); break;
                    default: SetMode(o, Mode.Peek); break;
                }
                o.riseT = 0f;   // stills: no rising beat
            }
            cheerHold = mode == "cheerhold";
            if (mode == "hide") active = false;
            Refresh();
            return "ok: townsfolk " + mode + " (weather " + weather + ")";
        }

        // ------------------------------------------------------------------ internals

        private void Request(Onlooker o, Mode m, float delay)
        {
            o.pending = m;
            o.pendingT = Mathf.Max(0f, delay);
        }

        private void SetMode(Onlooker o, Mode m)
        {
            if (o.mode == m) return;
            if (m == Mode.Duck && o.mode == Mode.Peek && particles != null)
                particles.Pixels(new Vector2(o.at.x, o.at.y - 5), OrderDust, 3, Pal.Sand0, 16f, 40f);
            if (m == Mode.Peek && o.mode == Mode.Hidden) o.riseT = (float)cfg.riseSeconds;
            if (m != Mode.Peek) o.cheerT = -1f;
            o.mode = m;
            o.frameShown = -1;
        }

        private void HideAll()
        {
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null) continue;
                o.mode = Mode.Hidden;
                o.pending = Mode.Hidden;
                o.pendingT = -1f;
                o.cheerT = -1f;
                o.riseT = 0f;
                o.frameShown = -1;
                o.body.enabled = false;
                if (o.hat != null) o.hat.enabled = false;
                o.coverNoon.enabled = false;
                if (o.coverDawn != null) o.coverDawn.enabled = false;
            }
        }

        private void Update()
        {
            if (!active) return;
            float dt = PawTime.UnscaledDt;
            float adt = PawTime.AmbientDt;

            if (firstPeekT > 0f && !ducking && (intro == null || !intro.Active))
            {
                firstPeekT -= dt;
                if (firstPeekT <= 0f)
                {
                    firstPeekT = -1f;
                    if (audioMgr != null) audioMgr.Play("sfx_door_creak", (float)cfg.creakVolume);
                    for (int i = 0; i < folk.Length; i++)
                        if (folk[i] != null) Request(folk[i], Mode.Peek, i * 0.35f);
                }
            }

            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null) continue;
                if (o.pendingT >= 0f)
                {
                    o.pendingT -= dt;
                    if (o.pendingT < 0f) SetMode(o, o.pending);
                }
                if (o.riseT > 0f) o.riseT -= dt;
                if (o.cheerT >= 0f && !cheerHold)
                {
                    o.cheerT += dt;
                    if (o.cheerT > (float)cfg.cheerSeconds) o.cheerT = -1f;
                }
                if (o.mode == Mode.Peek && adt > 0f)
                {
                    // slow idle on ambient time: blink and a 1-px sway (both freeze with the street in the hold)
                    if (o.blinkLeft > 0f) o.blinkLeft -= adt;
                    else
                    {
                        o.blinkIn -= adt;
                        if (o.blinkIn <= 0f)
                        {
                            o.blinkLeft = (float)cfg.blinkSeconds;
                            o.blinkIn = Random.Range((float)cfg.blinkMinSeconds, (float)cfg.blinkMaxSeconds);
                        }
                    }
                    o.swayT += adt;
                    float period = (float)cfg.swaySeconds;
                    if (o.swayT >= period)
                    {
                        o.swayT -= period;
                        o.swayX = reducedMotion ? 0 : (o.swayX == 0 ? (i == 0 ? 1 : -1) : 0);
                    }
                }
            }
            Refresh();
        }

        /// <summary>Frames, whole-pixel positions, tint and the dawn cover alpha. No allocations.</summary>
        private void Refresh()
        {
            float tint = street != null ? street.PlateTint : 1f;
            float dawnQ = street != null ? street.DawnShown : 0f;
            var body = new Color(tint, tint, tint, 1f);
            for (int i = 0; i < folk.Length; i++)
            {
                var o = folk[i];
                if (o == null) continue;
                bool shown = active && o.mode != Mode.Hidden;
                o.body.enabled = shown;
                o.coverNoon.enabled = shown;
                if (o.coverDawn != null) o.coverDawn.enabled = shown && dawnQ > 0f;
                if (o.hat != null) o.hat.enabled = shown;
                if (!shown) continue;

                bool ducked = o.mode == Mode.Duck || o.riseT > 0f;
                int f = ducked ? 1 : (o.blinkLeft > 0f ? 2 : 0);
                if (f != o.frameShown)
                {
                    o.frameShown = f;
                    o.body.sprite = o.sheet.Frames[f];
                }
                int lift = 0, hatDy = 0;
                if (o.cheerT >= 0f && !ducked)
                {
                    lift = o.cheerT < 0.05f ? 1 : cfg.popPx;
                    if (o.hat != null && !reducedMotion) hatDy = HatAt(o.cheerT) * cfg.hatFlyPx / 4;
                }
                int sx = ducked ? 0 : o.swayX;
                o.body.color = body;
                PawGfx.Place(o.body, o.at.x + sx, o.at.y - lift);
                if (o.hat != null)
                {
                    o.hat.color = body;
                    int drop = ducked ? o.duckDrop : 0;
                    PawGfx.Place(o.hat, o.at.x + sx + o.hatOffset.x, o.at.y + o.hatOffset.y + drop - lift + hatDy);
                }
                o.coverNoon.color = body;
                if (o.coverDawn != null && dawnQ > 0f) o.coverDawn.color = new Color(tint, tint, tint, dawnQ);
            }
        }

        /// <summary>Stepped hat flight (px, negative = up) at time t of the cheer; cheerhold freezes at the top.</summary>
        private int HatAt(float t)
        {
            if (cheerHold) return -4;
            int ms = (int)(t * 1000f);
            for (int k = 0; k < HatTimesMs.Length; k++)
                if (ms < HatTimesMs[k]) return HatFlyPx[k];
            return 0;
        }
    }
}
