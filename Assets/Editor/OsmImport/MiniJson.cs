using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LeGamez.Editor.OsmImport
{
    /// <summary>
    /// Minimal JSON reader. Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string / bool / null. Enough for Overpass output.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            var p = new Parser(json);
            var value = p.ReadValue();
            p.SkipWhitespace();
            if (!p.AtEnd) throw new FormatException($"Trailing data at {p.Pos}");
            return value;
        }

        class Parser
        {
            readonly string s;
            int i;

            public Parser(string json) { s = json; }
            public int Pos => i;
            public bool AtEnd => i >= s.Length;

            public void SkipWhitespace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd) throw new FormatException("Unexpected end of JSON");
                char c = s[i];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return ReadNumber();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException($"Expected '{word}' at {i}");
                i += word.Length;
            }

            Dictionary<string, object> ReadObject()
            {
                var dict = new Dictionary<string, object>();
                i++; // {
                SkipWhitespace();
                if (s[i] == '}') { i++; return dict; }
                while (true)
                {
                    SkipWhitespace();
                    string key = ReadString();
                    SkipWhitespace();
                    if (s[i] != ':') throw new FormatException($"Expected ':' at {i}");
                    i++;
                    dict[key] = ReadValue();
                    SkipWhitespace();
                    char c = s[i++];
                    if (c == '}') return dict;
                    if (c != ',') throw new FormatException($"Expected ',' or '}}' at {i - 1}");
                }
            }

            List<object> ReadArray()
            {
                var list = new List<object>();
                i++; // [
                SkipWhitespace();
                if (s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    char c = s[i++];
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException($"Expected ',' or ']' at {i - 1}");
                }
            }

            string ReadString()
            {
                if (s[i] != '"') throw new FormatException($"Expected string at {i}");
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                            break;
                        default: throw new FormatException($"Bad escape at {i - 1}");
                    }
                }
            }

            double ReadNumber()
            {
                int start = i;
                while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                if (start == i) throw new FormatException($"Unexpected '{s[i]}' at {i}");
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
