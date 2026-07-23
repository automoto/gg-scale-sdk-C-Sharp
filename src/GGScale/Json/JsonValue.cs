using System;
using System.Collections.Generic;
using System.Globalization;

namespace GGScale.Json
{
    /// <summary>The kind of a <see cref="JsonValue"/>.</summary>
    public enum JsonKind
    {
        /// <summary>JSON null.</summary>
        Null,

        /// <summary>JSON true/false.</summary>
        Bool,

        /// <summary>JSON number (stored as its raw text; see AsLong/AsDouble).</summary>
        Number,

        /// <summary>JSON string.</summary>
        String,

        /// <summary>JSON array.</summary>
        Array,

        /// <summary>JSON object (insertion-ordered).</summary>
        Object,
    }

    /// <summary>
    /// A parsed JSON document node. This is the SDK's raw-JSON currency:
    /// request payload fragments (game-session props, storage values,
    /// matchmaker attributes) and opaque response payloads are exposed as
    /// JsonValue. Numbers keep their raw text so 64-bit ids never lose
    /// precision. The type is reflection-free and IL2CPP/AOT-safe.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly bool _bool;
        private readonly string? _text;
        private readonly List<JsonValue>? _items;
        private readonly List<KeyValuePair<string, JsonValue>>? _props;

        /// <summary>The shared JSON null value.</summary>
        public static JsonValue Null { get; } = new JsonValue(JsonKind.Null, false, null, null, null);

        /// <summary>The shared JSON true value.</summary>
        public static JsonValue True { get; } = new JsonValue(JsonKind.Bool, true, null, null, null);

        /// <summary>The shared JSON false value.</summary>
        public static JsonValue False { get; } = new JsonValue(JsonKind.Bool, false, null, null, null);

        private JsonValue(JsonKind kind, bool b, string? text, List<JsonValue>? items, List<KeyValuePair<string, JsonValue>>? props)
        {
            Kind = kind;
            _bool = b;
            _text = text;
            _items = items;
            _props = props;
        }

        /// <summary>The kind of this value.</summary>
        public JsonKind Kind { get; }

        /// <summary>Wraps a boolean.</summary>
        public static JsonValue Of(bool value) => value ? True : False;

        /// <summary>Wraps a 64-bit integer without precision loss.</summary>
        public static JsonValue Of(long value) =>
            new JsonValue(JsonKind.Number, false, value.ToString(CultureInfo.InvariantCulture), null, null);

        /// <summary>Wraps a double.</summary>
        public static JsonValue Of(double value) =>
            new JsonValue(JsonKind.Number, false, value.ToString("R", CultureInfo.InvariantCulture), null, null);

        /// <summary>Wraps a string.</summary>
        public static JsonValue Of(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            return new JsonValue(JsonKind.String, false, value, null, null);
        }

        /// <summary>Creates an empty, mutable JSON object.</summary>
        public static JsonValue NewObject() =>
            new JsonValue(JsonKind.Object, false, null, null, new List<KeyValuePair<string, JsonValue>>());

        /// <summary>Creates an empty, mutable JSON array.</summary>
        public static JsonValue NewArray() =>
            new JsonValue(JsonKind.Array, false, null, new List<JsonValue>(), null);

        internal static JsonValue FromRawNumber(string raw) =>
            new JsonValue(JsonKind.Number, false, raw, null, null);

        /// <summary>
        /// Parses a JSON document. Throws <see cref="FormatException"/> on
        /// malformed input, trailing content, or pathological nesting.
        /// </summary>
        public static JsonValue Parse(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            return JsonParser.Parse(json);
        }

        /// <summary>The boolean value; throws unless <see cref="Kind"/> is Bool.</summary>
        public bool AsBool()
        {
            Require(JsonKind.Bool);
            return _bool;
        }

        /// <summary>The number as a 64-bit integer; throws unless Kind is Number.</summary>
        public long AsLong()
        {
            Require(JsonKind.Number);
            if (long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }
            return (long)double.Parse(_text!, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>The number as a double; throws unless Kind is Number.</summary>
        public double AsDouble()
        {
            Require(JsonKind.Number);
            return double.Parse(_text!, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>The string value; throws unless Kind is String.</summary>
        public string AsString()
        {
            Require(JsonKind.String);
            return _text!;
        }

        /// <summary>Element count of an array or member count of an object; 0 otherwise.</summary>
        public int Count => _items?.Count ?? _props?.Count ?? 0;

        /// <summary>Array element by index; throws unless Kind is Array.</summary>
        public JsonValue this[int index]
        {
            get
            {
                Require(JsonKind.Array);
                return _items![index];
            }
        }

        /// <summary>Object member by key; throws when missing or not an object.</summary>
        public JsonValue this[string key]
        {
            get
            {
                var v = Opt(key, includeNull: true)
                    ?? throw new KeyNotFoundException($"JSON object has no member \"{key}\"");
                return v;
            }
        }

        /// <summary>The members of an object, in document order (empty otherwise).</summary>
        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members =>
            (IReadOnlyList<KeyValuePair<string, JsonValue>>?)_props ?? System.Array.Empty<KeyValuePair<string, JsonValue>>();

        /// <summary>The elements of an array, in order (empty otherwise).</summary>
        public IReadOnlyList<JsonValue> Items =>
            (IReadOnlyList<JsonValue>?)_items ?? System.Array.Empty<JsonValue>();

        /// <summary>Appends an element to an array; returns this for chaining.</summary>
        public JsonValue Add(JsonValue item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }
            Require(JsonKind.Array);
            _items!.Add(item);
            return this;
        }

        /// <summary>Sets an object member (replacing in place); returns this for chaining.</summary>
        public JsonValue Set(string key, JsonValue value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            Require(JsonKind.Object);
            for (var i = 0; i < _props!.Count; i++)
            {
                if (_props[i].Key == key)
                {
                    _props[i] = new KeyValuePair<string, JsonValue>(key, value);
                    return this;
                }
            }
            _props.Add(new KeyValuePair<string, JsonValue>(key, value));
            return this;
        }

        /// <summary>
        /// The member value, or null when this is not an object, the key is
        /// absent, or the member is JSON null.
        /// </summary>
        public JsonValue? Opt(string key) => Opt(key, includeNull: false);

        private JsonValue? Opt(string key, bool includeNull)
        {
            if (Kind != JsonKind.Object)
            {
                return null;
            }
            for (var i = _props!.Count - 1; i >= 0; i--)
            {
                if (_props[i].Key == key)
                {
                    var v = _props[i].Value;
                    return !includeNull && v.Kind == JsonKind.Null ? null : v;
                }
            }
            return null;
        }

        /// <summary>The member as a string, or null when absent/null/not a string-typed member.</summary>
        public string? OptString(string key)
        {
            var v = Opt(key);
            return v != null && v.Kind == JsonKind.String ? v.AsString() : null;
        }

        /// <summary>The member as a long, or <paramref name="defaultValue"/> when absent.</summary>
        public long OptLong(string key, long defaultValue = 0)
        {
            var v = Opt(key);
            return v != null && v.Kind == JsonKind.Number ? v.AsLong() : defaultValue;
        }

        /// <summary>The member as a double, or <paramref name="defaultValue"/> when absent.</summary>
        public double OptDouble(string key, double defaultValue = 0)
        {
            var v = Opt(key);
            return v != null && v.Kind == JsonKind.Number ? v.AsDouble() : defaultValue;
        }

        /// <summary>The member as a bool, or <paramref name="defaultValue"/> when absent.</summary>
        public bool OptBool(string key, bool defaultValue = false)
        {
            var v = Opt(key);
            return v != null && v.Kind == JsonKind.Bool ? v.AsBool() : defaultValue;
        }

        /// <summary>The member parsed as an RFC 3339 timestamp, or null.</summary>
        public DateTimeOffset? OptTime(string key)
        {
            var s = OptString(key);
            if (s == null)
            {
                return null;
            }
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t
                : (DateTimeOffset?)null;
        }

        /// <summary>Serializes this value to compact JSON.</summary>
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(System.Text.StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    return;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    return;
                case JsonKind.Number:
                    sb.Append(_text);
                    return;
                case JsonKind.String:
                    WriteString(sb, _text!);
                    return;
                case JsonKind.Array:
                    sb.Append('[');
                    for (var i = 0; i < _items!.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        _items[i].Write(sb);
                    }
                    sb.Append(']');
                    return;
                default:
                    sb.Append('{');
                    for (var i = 0; i < _props!.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        WriteString(sb, _props[i].Key);
                        sb.Append(':');
                        _props[i].Value.Write(sb);
                    }
                    sb.Append('}');
                    return;
            }
        }

        private static void WriteString(System.Text.StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private void Require(JsonKind kind)
        {
            if (Kind != kind)
            {
                throw new InvalidOperationException($"JSON value is {Kind}, not {kind}");
            }
        }

        internal static JsonValue NewParsedObject(List<KeyValuePair<string, JsonValue>> props) =>
            new JsonValue(JsonKind.Object, false, null, null, props);

        internal static JsonValue NewParsedArray(List<JsonValue> items) =>
            new JsonValue(JsonKind.Array, false, null, items, null);
    }
}
