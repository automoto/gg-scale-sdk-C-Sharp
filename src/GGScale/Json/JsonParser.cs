using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GGScale.Json
{
    /// <summary>Hand-written recursive-descent JSON parser (RFC 8259).</summary>
    internal static class JsonParser
    {
        private const int MaxDepth = 128;

        internal static JsonValue Parse(string s)
        {
            var i = 0;
            var v = ParseValue(s, ref i, 0);
            SkipWhitespace(s, ref i);
            if (i != s.Length)
            {
                throw Error(s, i, "trailing content");
            }
            return v;
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("JSON nesting exceeds maximum depth");
            }
            SkipWhitespace(s, ref i);
            if (i >= s.Length)
            {
                throw Error(s, i, "unexpected end of input");
            }
            switch (s[i])
            {
                case '{':
                    return ParseObject(s, ref i, depth);
                case '[':
                    return ParseArray(s, ref i, depth);
                case '"':
                    return JsonValue.Of(ParseString(s, ref i));
                case 't':
                    Expect(s, ref i, "true");
                    return JsonValue.True;
                case 'f':
                    Expect(s, ref i, "false");
                    return JsonValue.False;
                case 'n':
                    Expect(s, ref i, "null");
                    return JsonValue.Null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            i++; // '{'
            var props = new List<KeyValuePair<string, JsonValue>>();
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}')
            {
                i++;
                return JsonValue.NewParsedObject(props);
            }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != '"')
                {
                    throw Error(s, i, "expected object key");
                }
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':')
                {
                    throw Error(s, i, "expected ':'");
                }
                i++;
                var value = ParseValue(s, ref i, depth + 1);
                props.Add(new KeyValuePair<string, JsonValue>(key, value));
                SkipWhitespace(s, ref i);
                var c = Peek(s, i);
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    i++;
                    return JsonValue.NewParsedObject(props);
                }
                throw Error(s, i, "expected ',' or '}'");
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            i++; // '['
            var items = new List<JsonValue>();
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']')
            {
                i++;
                return JsonValue.NewParsedArray(items);
            }
            while (true)
            {
                items.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                var c = Peek(s, i);
                if (c == ',')
                {
                    i++;
                    continue;
                }
                if (c == ']')
                {
                    i++;
                    return JsonValue.NewParsedArray(items);
                }
                throw Error(s, i, "expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length)
                {
                    throw Error(s, i, "unterminated string");
                }
                var c = s[i++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    if (c < ' ')
                    {
                        throw Error(s, i - 1, "raw control character in string");
                    }
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length)
                {
                    throw Error(s, i, "unterminated escape");
                }
                var e = s[i++];
                switch (e)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case '/':
                        sb.Append('/');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > s.Length)
                        {
                            throw Error(s, i, "truncated \\u escape");
                        }
                        if (!ushort.TryParse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error(s, i, "invalid \\u escape");
                        }
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw Error(s, i - 1, "invalid escape");
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            var start = i;
            if (Peek(s, i) == '-')
            {
                i++;
            }
            var intDigits = 0;
            var firstIntDigit = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                i++;
                intDigits++;
            }
            if (intDigits == 0)
            {
                throw Error(s, i, "invalid number");
            }
            if (intDigits > 1 && s[firstIntDigit] == '0')
            {
                throw Error(s, firstIntDigit, "leading zero in number");
            }
            if (i < s.Length && s[i] == '.')
            {
                i++;
                var fracDigits = 0;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    i++;
                    fracDigits++;
                }
                if (fracDigits == 0)
                {
                    throw Error(s, i, "invalid number fraction");
                }
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    i++;
                }
                var expDigits = 0;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    i++;
                    expDigits++;
                }
                if (expDigits == 0)
                {
                    throw Error(s, i, "invalid number exponent");
                }
            }
            return JsonValue.FromRawNumber(s.Substring(start, i - start));
        }

        private static void Expect(string s, ref int i, string keyword)
        {
            if (i + keyword.Length > s.Length || string.CompareOrdinal(s, i, keyword, 0, keyword.Length) != 0)
            {
                throw Error(s, i, $"expected '{keyword}'");
            }
            i += keyword.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
            {
                i++;
            }
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static FormatException Error(string s, int i, string reason) =>
            new FormatException($"invalid JSON at offset {Math.Min(i, s.Length)}: {reason}");
    }
}
