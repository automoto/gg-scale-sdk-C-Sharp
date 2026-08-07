using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    public class ConfigTests
    {
        private static GGScaleClient NewClient(FakeTransport ft) =>
            new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });

        [Fact]
        public async Task Get_returns_value_and_etag()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"maintenance_mode\":false}"), etag: "\"remote-config-abc\"");
            using var client = NewClient(ft);

            var got = await client.Config.GetAsync();

            Assert.False(got.NotModified);
            Assert.False(got.Value.OptBool("maintenance_mode"));
            Assert.Equal("\"remote-config-abc\"", got.ETag);
        }

        [Fact]
        public async Task Get_sends_api_key_without_session()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.NewObject(), etag: "\"e\"");
            using var client = NewClient(ft);

            await client.Config.GetAsync();

            Assert.Equal("GET", ft.LastRequest!.Method);
            Assert.Equal("/v1/config", ft.LastRequest.Path);
            Assert.Equal("pk", ft.LastRequest.ApiKey);
            Assert.Null(ft.LastRequest.SessionToken);
        }

        [Fact]
        public async Task Get_passes_if_none_match_header()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.NewObject(), etag: "\"e2\"");
            using var client = NewClient(ft);

            await client.Config.GetAsync("\"e1\"");

            Assert.Equal("\"e1\"", ft.LastRequest!.IfNoneMatch);
        }

        [Fact]
        public async Task Get_maps_304_to_not_modified_with_null_value()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Null, status: 304, etag: "\"e1\"");
            using var client = NewClient(ft);

            var got = await client.Config.GetAsync("\"e1\"");

            Assert.True(got.NotModified);
            Assert.Equal(JsonKind.Null, got.Value.Kind);
            Assert.Equal("\"e1\"", got.ETag);
        }

        [Fact]
        public async Task Get_without_validator_omits_if_none_match()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.NewObject(), etag: "\"e\"");
            using var client = NewClient(ft);

            await client.Config.GetAsync();

            Assert.Null(ft.LastRequest!.IfNoneMatch);
        }
    }
}
