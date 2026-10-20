using System.Globalization;
using System.Text;

namespace PawNoon.Core
{
    /// <summary>
    /// Minimal compact JSON writer (the reading side is <see cref="PnJson"/>). Keys are written in call
    /// order; integers are exact (long), doubles use round-trip formatting. Not for hot paths: it builds a
    /// string once per request.
    /// </summary>
    public sealed class JsonOut
    {
        private readonly StringBuilder sb;
        private bool needComma;

        public JsonOut(int capacity = 512) { sb = new StringBuilder(capacity); }

        public override string ToString() { return sb.ToString(); }

        private void Sep()
        {
            if (needComma) sb.Append(',');
            needComma = true;
        }

        public JsonOut BeginObject() { Sep(); sb.Append('{'); needComma = false; return this; }
        public JsonOut EndObject() { sb.Append('}'); needComma = true; return this; }
        public JsonOut BeginArray() { Sep(); sb.Append('['); needComma = false; return this; }
        public JsonOut EndArray() { sb.Append(']'); needComma = true; return this; }

        /// <summary>Writes "key": and leaves the next value to follow without a comma.</summary>
        public JsonOut Key(string k)
        {
            Sep();
            Quote(k);
            sb.Append(':');
            needComma = false;
            return this;
        }

        public JsonOut Str(string v)
        {
            Sep();
            if (v == null) sb.Append("null");
            else Quote(v);
            return this;
        }

        public JsonOut Long(long v) { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public JsonOut Num(double v)
        {
            Sep();
            if (double.IsNaN(v) || double.IsInfinity(v)) sb.Append('0');
            else sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            return this;
        }

        public JsonOut Bool(bool v) { Sep(); sb.Append(v ? "true" : "false"); return this; }
        public JsonOut Null() { Sep(); sb.Append("null"); return this; }

        /// <summary>Inserts an already-serialised JSON value verbatim.</summary>
        public JsonOut Raw(string json) { Sep(); sb.Append(string.IsNullOrEmpty(json) ? "null" : json); return this; }

        public JsonOut Prop(string k, string v) { return Key(k).Str(v); }
        public JsonOut Prop(string k, long v) { return Key(k).Long(v); }
        public JsonOut Prop(string k, int v) { return Key(k).Long(v); }
        public JsonOut Prop(string k, double v) { return Key(k).Num(v); }
        public JsonOut Prop(string k, bool v) { return Key(k).Bool(v); }

        private void Quote(string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Quotes and escapes one string as a JSON value.</summary>
        public static string Quoted(string s)
        {
            var j = new JsonOut(s != null ? s.Length + 2 : 4);
            j.Str(s);
            return j.ToString();
        }
    }
}
