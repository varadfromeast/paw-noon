using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PawNoon.Core
{
    /// <summary>
    /// Tiny tolerant JSON reader. Produces Dictionary&lt;string, object&gt;, List&lt;object&gt;,
    /// double, string, bool or null. Never throws: malformed input returns null.
    /// </summary>
    public static class PnJson
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                var p = new Reader(text);
                p.SkipWs();
                return p.ReadValue();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object>;
        }

        // ---------------------------------------------------------------- helpers

        public static Dictionary<string, object> Obj(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        public static bool Has(object node, string key)
        {
            var d = node as Dictionary<string, object>;
            return d != null && d.ContainsKey(key);
        }

        public static double Num(object node, string key, double def)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v)) return def;
            return ToNum(v, def);
        }

        public static int Int(object node, string key, int def)
        {
            return (int)Math.Round(Num(node, key, def));
        }

        public static string Str(object node, string key, string def)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(object node, string key, bool def)
        {
            var d = node as Dictionary<string, object>;
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0.0;
            var s = v as string;
            if (s != null) return s == "true" || s == "1";
            return def;
        }

        public static double ToNum(object v, double def)
        {
            if (v is double) return (double)v;
            if (v is bool) return (bool)v ? 1.0 : 0.0;
            var s = v as string;
            double r;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        /// <summary>Reads [a, b] into two ints. Returns false (and leaves defaults) when absent.</summary>
        public static bool Pair(object node, string key, ref int a, ref int b)
        {
            var arr = Arr(node, key);
            if (arr == null || arr.Count < 2) return false;
            a = (int)Math.Round(ToNum(arr[0], a));
            b = (int)Math.Round(ToNum(arr[1], b));
            return true;
        }

        public static double[] NumArray(object node, string key)
        {
            var arr = Arr(node, key);
            if (arr == null) return null;
            var r = new double[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = ToNum(arr[i], 0.0);
            return r;
        }

        // ---------------------------------------------------------------- reader

        private sealed class Reader
        {
            private readonly string s;
            private int i;
            private readonly StringBuilder sb = new StringBuilder(64);

            public Reader(string text) { s = text; i = 0; }

            public void SkipWs()
            {
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '﻿') { i++; continue; }
                    if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                    {
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    break;
                }
            }

            public object ReadValue()
            {
                if (i >= s.Length) throw new FormatException("eof");
                char c = s[i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't' && Match("true")) return true;
                if (c == 'f' && Match("false")) return false;
                if (c == 'n' && Match("null")) return null;
                return ReadNumber();
            }

            private bool Match(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) == 0)
                {
                    i += word.Length;
                    return true;
                }
                throw new FormatException("bad literal");
            }

            private Dictionary<string, object> ReadObject()
            {
                var d = new Dictionary<string, object>();
                i++; // {
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (i < s.Length)
                {
                    SkipWs();
                    if (s[i] == '}') { i++; return d; } // trailing comma tolerance
                    string key = ReadString();
                    SkipWs();
                    if (s[i] != ':') throw new FormatException("expected :");
                    i++;
                    SkipWs();
                    d[key] = ReadValue();
                    SkipWs();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("expected , or }");
                }
                return d;
            }

            private List<object> ReadArray()
            {
                var l = new List<object>();
                i++; // [
                SkipWs();
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (i < s.Length)
                {
                    SkipWs();
                    if (s[i] == ']') { i++; return l; }
                    l.Add(ReadValue());
                    SkipWs();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("expected , or ]");
                }
                return l;
            }

            private string ReadString()
            {
                if (s[i] != '"') throw new FormatException("expected string");
                i++;
                sb.Length = 0;
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break; // \" \\ \/
                    }
                }
                throw new FormatException("unterminated string");
            }

            private double ReadNumber()
            {
                int start = i;
                while (i < s.Length)
                {
                    char c = s[i];
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') i++;
                    else break;
                }
                if (i == start) throw new FormatException("unexpected char");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
