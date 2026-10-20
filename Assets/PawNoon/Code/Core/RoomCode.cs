namespace PawNoon.Core
{
    /// <summary>
    /// Friend-duel room codes (SPEC_V2 §4.4, server: supabase/functions/_shared/rooms.ts): exactly 4 letters A-Z.
    /// The server never generates I or O, but joining accepts all 26 letters (the keypad has every letter).
    /// </summary>
    public static class RoomCode
    {
        public const int Length = 4;

        public static bool IsLetter(char c) { return c >= 'A' && c <= 'Z'; }

        /// <summary>Upper case A-Z for a-z / A-Z, '\0' for anything else.</summary>
        public static char Upper(char c)
        {
            if (c >= 'a' && c <= 'z') return (char)(c - 32);
            return IsLetter(c) ? c : '\0';
        }

        /// <summary>Exactly 4 upper-case letters A-Z.</summary>
        public static bool IsValid(string s)
        {
            if (s == null || s.Length != Length) return false;
            for (int i = 0; i < Length; i++) if (!IsLetter(s[i])) return false;
            return true;
        }

        /// <summary>Upper-cases and drops blanks / dashes (pasted "ab-cd"), null unless 4 letters remain.</summary>
        public static string Normalize(string s)
        {
            if (s == null) return null;
            var buf = new char[Length];
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' || c == '-' || c == '\t') continue;
                char u = Upper(c);
                if (u == '\0' || n == Length) return null;
                buf[n++] = u;
            }
            return n == Length ? new string(buf) : null;
        }
    }

    /// <summary>
    /// The 4-slot code being typed on the pixel keypad (no OS keyboard). Allocation-free while typing:
    /// <see cref="Chars"/> is the live buffer (feed BitmapText.SetChars); <see cref="Code"/> builds a string
    /// only when asked.
    /// </summary>
    public sealed class RoomCodeEntry
    {
        private readonly char[] buf = new char[RoomCode.Length];
        private string cached;

        public int Count { get; private set; }
        public bool Complete { get { return Count == RoomCode.Length; } }
        public bool Empty { get { return Count == 0; } }
        /// <summary>Typed letters in slots 0..Count-1 (the rest is stale, ignore it).</summary>
        public char[] Chars { get { return buf; } }

        public char this[int slot] { get { return slot >= 0 && slot < Count ? buf[slot] : '\0'; } }

        /// <summary>Appends a letter (a-z is upper-cased). False when full or not a letter.</summary>
        public bool Add(char c)
        {
            char u = RoomCode.Upper(c);
            if (u == '\0' || Count >= RoomCode.Length) return false;
            buf[Count++] = u;
            cached = null;
            return true;
        }

        public bool Backspace()
        {
            if (Count == 0) return false;
            Count--;
            cached = null;
            return true;
        }

        public void Clear()
        {
            Count = 0;
            cached = null;
        }

        /// <summary>Replaces the entry with up to 4 letters of <paramref name="s"/> (review stills). Non-letters stop it.</summary>
        public void Set(string s)
        {
            Clear();
            if (s == null) return;
            for (int i = 0; i < s.Length && Count < RoomCode.Length; i++)
                if (!Add(s[i])) break;
        }

        /// <summary>The code once complete, else null.</summary>
        public string Code
        {
            get
            {
                if (!Complete) return null;
                if (cached == null) cached = new string(buf, 0, RoomCode.Length);
                return cached;
            }
        }
    }

    /// <summary>
    /// The A-Z pixel keypad: 7 columns x 4 rows. Rows 0-2 hold A-U, row 3 holds V-Z in columns 0-4 and DEL
    /// across columns 5-6. Key index 0..25 = 'A'..'Z', 26 = DEL. Pure integer maths (whole pixels), on <see cref="PxRect"/> (Banter.cs).
    /// </summary>
    public static class KeypadLayout
    {
        public const int Columns = 7;
        public const int Rows = 4;
        public const int KeyCount = 27;
        public const int Delete = 26;

        public static char LetterAt(int index) { return index >= 0 && index < 26 ? (char)('A' + index) : '\0'; }

        public static int Width(int keyW, int gap) { return Columns * keyW + (Columns - 1) * gap; }
        public static int Height(int keyH, int gap) { return Rows * keyH + (Rows - 1) * gap; }

        /// <summary>The key's rectangle; DEL is two keys wide (plus the gap between them).</summary>
        public static PxRect KeyRect(int index, int originX, int originY, int keyW, int keyH, int gap)
        {
            if (index < 0 || index >= KeyCount) return new PxRect(originX, originY, 0, 0);
            int col, row, span = 1;
            if (index == Delete) { col = 5; row = 3; span = 2; }
            else { col = index % Columns; row = index / Columns; }
            return new PxRect(originX + col * (keyW + gap), originY + row * (keyH + gap), span * keyW + (span - 1) * gap, keyH);
        }

        /// <summary>Key under a canvas point, or -1 (gaps and the empty space count as no key).</summary>
        public static int HitTest(int px, int py, int originX, int originY, int keyW, int keyH, int gap)
        {
            int dx = px - originX, dy = py - originY;
            if (dx < 0 || dy < 0) return -1;
            int col = dx / (keyW + gap), row = dy / (keyH + gap);
            if (col >= Columns || row >= Rows) return -1;
            if (dx - col * (keyW + gap) >= keyW && !(row == 3 && col == 5)) return -1;   // the gap inside DEL is DEL
            if (dy - row * (keyH + gap) >= keyH) return -1;
            if (row == 3 && col >= 5) return Delete;
            int index = row * Columns + col;
            return index < 26 ? index : -1;
        }
    }
}
