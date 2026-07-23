using System;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests.Json
{
    public class JsonValueParseTests
    {
        [Fact]
        public void Parse_reads_nested_object()
        {
            var v = JsonValue.Parse("{\"a\":{\"b\":[1,2,3]},\"c\":\"x\"}");

            Assert.Equal(JsonKind.Object, v.Kind);
            Assert.Equal(3, v["a"]["b"].Count);
            Assert.Equal(2L, v["a"]["b"][1].AsLong());
            Assert.Equal("x", v["c"].AsString());
        }

        [Theory]
        [InlineData("null", JsonKind.Null)]
        [InlineData("true", JsonKind.Bool)]
        [InlineData("false", JsonKind.Bool)]
        [InlineData("0", JsonKind.Number)]
        [InlineData("-12.5e3", JsonKind.Number)]
        [InlineData("\"s\"", JsonKind.String)]
        [InlineData("[]", JsonKind.Array)]
        [InlineData("{}", JsonKind.Object)]
        public void Parse_recognizes_every_kind(string json, JsonKind kind)
        {
            Assert.Equal(kind, JsonValue.Parse(json).Kind);
        }

        [Fact]
        public void Parse_preserves_long_precision()
        {
            // 2^60 + 1 is not representable as a double.
            var v = JsonValue.Parse("{\"id\":1152921504606846977}");
            Assert.Equal(1152921504606846977L, v["id"].AsLong());
        }

        [Fact]
        public void Parse_reads_unicode_escapes()
        {
            var v = JsonValue.Parse("\"a\\u00e9b\\n\\t\\\"\\\\\"");
            Assert.Equal("aéb\n\t\"\\", v.AsString());
        }

        [Fact]
        public void Parse_reads_surrogate_pair_escapes()
        {
            var v = JsonValue.Parse("\"\\ud83d\\ude00\"");
            Assert.Equal("\U0001F600", v.AsString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("[1,]")]
        [InlineData("{\"a\":}")]
        [InlineData("{\"a\" 1}")]
        [InlineData("tru")]
        [InlineData("01")]
        [InlineData("1 2")]
        [InlineData("\"unterminated")]
        [InlineData("{\"a\":1}}")]
        public void Parse_rejects_malformed_input(string json)
        {
            Assert.Throws<FormatException>(() => JsonValue.Parse(json));
        }

        [Fact]
        public void Parse_rejects_pathological_nesting()
        {
            var deep = new string('[', 200) + new string(']', 200);
            Assert.Throws<FormatException>(() => JsonValue.Parse(deep));
        }

        [Fact]
        public void Parse_last_duplicate_key_wins()
        {
            var v = JsonValue.Parse("{\"a\":1,\"a\":2}");
            Assert.Equal(2L, v["a"].AsLong());
        }
    }

    public class JsonValueWriteTests
    {
        [Fact]
        public void ToString_round_trips_document()
        {
            const string json = "{\"a\":[1,2.5,true,null,\"s\"],\"b\":{\"c\":\"d\"}}";
            Assert.Equal(json, JsonValue.Parse(json).ToString());
        }

        [Fact]
        public void ToString_escapes_control_and_quote_characters()
        {
            var v = JsonValue.Of("a\"b\\c\nd");
            Assert.Equal("\"a\\\"b\\\\c\\nd\\u0001\"", v.ToString());
        }

        [Fact]
        public void Builders_compose_objects_and_arrays()
        {
            var obj = JsonValue.NewObject()
                .Set("name", JsonValue.Of("x"))
                .Set("count", JsonValue.Of(3L))
                .Set("tags", JsonValue.NewArray().Add(JsonValue.Of("a")).Add(JsonValue.Of("b")));

            Assert.Equal("{\"name\":\"x\",\"count\":3,\"tags\":[\"a\",\"b\"]}", obj.ToString());
        }

        [Fact]
        public void Set_replaces_existing_key_in_place()
        {
            var obj = JsonValue.NewObject().Set("a", JsonValue.Of(1L)).Set("b", JsonValue.Of(2L));
            obj.Set("a", JsonValue.Of(9L));
            Assert.Equal("{\"a\":9,\"b\":2}", obj.ToString());
        }
    }

    public class JsonValueAccessTests
    {
        [Fact]
        public void OptString_returns_null_for_missing_or_null()
        {
            var v = JsonValue.Parse("{\"a\":null,\"b\":\"x\"}");
            Assert.Null(v.OptString("a"));
            Assert.Null(v.OptString("missing"));
            Assert.Equal("x", v.OptString("b"));
        }

        [Fact]
        public void OptLong_defaults_when_missing()
        {
            var v = JsonValue.Parse("{\"n\":7}");
            Assert.Equal(7L, v.OptLong("n"));
            Assert.Equal(0L, v.OptLong("missing"));
        }

        [Fact]
        public void OptBool_and_OptDouble_read_values()
        {
            var v = JsonValue.Parse("{\"b\":true,\"d\":1.5}");
            Assert.True(v.OptBool("b"));
            Assert.Equal(1.5, v.OptDouble("d"));
        }

        [Fact]
        public void Opt_skips_json_null_members()
        {
            var v = JsonValue.Parse("{\"a\":null,\"b\":{}}");
            Assert.Null(v.Opt("a"));
            Assert.NotNull(v.Opt("b"));
        }

        [Fact]
        public void AsString_throws_on_wrong_kind()
        {
            Assert.Throws<InvalidOperationException>(() => JsonValue.Parse("1").AsString());
        }

        [Fact]
        public void OptTime_parses_rfc3339()
        {
            var v = JsonValue.Parse("{\"t\":\"2026-07-06T12:05:00Z\"}");
            var t = v.OptTime("t");
            Assert.NotNull(t);
            Assert.Equal(new DateTimeOffset(2026, 7, 6, 12, 5, 0, TimeSpan.Zero), t!.Value);
        }
    }
}
