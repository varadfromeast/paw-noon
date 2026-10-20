using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>
    /// One playable cat's persona: the name the player sees, the "meow signature" SFX played when he talks,
    /// and his banter lines. Presentation only; never touches the rules.
    /// </summary>
    public sealed class CharacterProfile
    {
        public string id;
        /// <summary>Upper-case display name (HUD name plate, result card, messages).</summary>
        public string name;
        /// <summary>SFX key (Resources/Audio/SFX/&lt;meow&gt;.wav) played when his speech bubble pops.</summary>
        public string meow;
        public BanterLines lines;
    }

    /// <summary>
    /// The launch roster (SPEC_V2 §2, §8.2), read from Resources/Data/characters.json:
    /// <code>{ "whiskers": { "name": "WHISKERS", "meow": "sfx_meow_whiskers", "lines": { "matchStart": [...], ... } },
    ///   "pawtruth": { "name": "PAWTRUTH", "meow": "sfx_meow_pawtruth", "lines": { ... } } }</code>
    /// Fallbacks, per field: a missing file / entry / key never breaks the game.
    /// <list type="bullet">
    /// <item>Pawtruth: name = characters.json, else duel_config.json botName, else "PAWTRUTH"; each banter context =
    /// characters.json, else the legacy duel_config.json "blackpawLines" (kept for one release), else the built-ins.</item>
    /// <item>Whiskers: name = characters.json, else "WHISKERS"; each context = characters.json, else
    /// <see cref="BanterLines.WhiskersDefaults"/>.</item>
    /// </list>
    /// The old persona id "blackpaw" (still used by recorded-duel payloads) resolves to Pawtruth.
    /// </summary>
    public sealed class CharacterRoster
    {
        public const string Whiskers = "whiskers";
        public const string Pawtruth = "pawtruth";
        /// <summary>Pre-rename id of Pawtruth (server recordings, old saves).</summary>
        public const string LegacyPawtruth = "blackpaw";

        private readonly Dictionary<string, CharacterProfile> map = new Dictionary<string, CharacterProfile>();

        /// <summary>True when characters.json was present and parsed.</summary>
        public bool FromFile { get; private set; }

        /// <summary>Every id in the roster (built-in ones first, then any extra entries from the file).</summary>
        public readonly List<string> Ids = new List<string>();

        /// <summary>Built-in roster plus the legacy config fallback (cfg may be null).</summary>
        public static CharacterRoster Load(string charactersJson, DuelConfig cfg)
        {
            var r = new CharacterRoster();
            var root = PnJson.ParseObject(charactersJson);
            r.FromFile = root != null;

            var pawLegacy = cfg != null && cfg.blackpawLines != null ? cfg.blackpawLines : new BanterLines();
            string pawName = cfg != null && !string.IsNullOrEmpty(cfg.botName) ? cfg.botName : "PAWTRUTH";
            r.Add(Read(root, Pawtruth, pawName, "sfx_meow_pawtruth", pawLegacy));
            r.Add(Read(root, Whiskers, "WHISKERS", "sfx_meow_whiskers", BanterLines.WhiskersDefaults()));

            // any further cats in the file (future roster); they fall back to Pawtruth's lines
            if (root != null)
            {
                foreach (var kv in root)
                {
                    string id = Normalize(kv.Key);
                    if (id.Length == 0 || r.map.ContainsKey(id) || !(kv.Value is Dictionary<string, object>)) continue;
                    r.Add(Read(root, kv.Key, kv.Key.ToUpperInvariant(), "sfx_meow_pawtruth", pawLegacy));
                }
            }
            return r;
        }

        private static CharacterProfile Read(Dictionary<string, object> root, string key, string defName, string defMeow, BanterLines defLines)
        {
            var node = root != null ? PnJson.Obj(root, key) : null;
            var p = new CharacterProfile();
            p.id = Normalize(key);
            p.name = PnJson.Str(node, "name", defName);
            if (string.IsNullOrEmpty(p.name)) p.name = defName;
            p.meow = PnJson.Str(node, "meow", defMeow);
            p.lines = BanterLines.FromJson(node != null ? PnJson.Obj(node, "lines") : null, defLines);
            return p;
        }

        private void Add(CharacterProfile p)
        {
            if (!map.ContainsKey(p.id)) Ids.Add(p.id);
            map[p.id] = p;
        }

        /// <summary>Lower-case, trimmed; "blackpaw" maps to "pawtruth".</summary>
        public static string Normalize(string id)
        {
            string s = (id ?? "").Trim().ToLowerInvariant();
            return s == LegacyPawtruth ? Pawtruth : s;
        }

        public bool Has(string id) { return map.ContainsKey(Normalize(id)); }

        /// <summary>The profile for an id; unknown ids get Pawtruth (never null).</summary>
        public CharacterProfile Get(string id)
        {
            CharacterProfile p;
            if (map.TryGetValue(Normalize(id), out p)) return p;
            return map[Pawtruth];
        }

        /// <summary>Solo: the cat across the street is the one the player did not pick.</summary>
        public static string OpponentOf(string playerId)
        {
            return Normalize(playerId) == Pawtruth ? Whiskers : Pawtruth;
        }
    }
}
