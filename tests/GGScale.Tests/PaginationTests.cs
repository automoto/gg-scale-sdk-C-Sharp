using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;
using Xunit;

namespace GGScale.Tests
{
    public class PaginationTests
    {
        private static GGScaleClient NewClientWithSession(FakeTransport ft)
        {
            var client = new GGScaleClient(new GGScaleClientOptions { ApiKey = "pk", Transport = ft });
            client.SetSession(new Session("tok", "ref", 1, DateTimeOffset.UtcNow.AddHours(1)));
            return client;
        }

        [Fact]
        public async Task Storage_ListAll_iterates_pages_until_cursor_empty()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"a\"},{\"key\":\"b\"}],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"c\"}],\"next_cursor\":\"\"}"));
            using var client = NewClientWithSession(ft);

            var keys = new List<string>();
            await foreach (var obj in client.Storage.ListAllAsync())
            {
                keys.Add(obj.Key);
            }

            Assert.Equal("a,b,c", string.Join(",", keys));
            Assert.Equal(2, ft.CallCount);
        }

        [Fact]
        public async Task Storage_ListAll_passes_prefix_and_limit_on_every_page()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"a\"}],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[],\"next_cursor\":\"\"}"));
            using var client = NewClientWithSession(ft);

            await foreach (var _ in client.Storage.ListAllAsync(new StorageListOptions { KeyPrefix = "save/", Limit = 10 }))
            {
            }

            Assert.Equal("save/", ft.Requests[0].QueryValue("key_prefix"));
            Assert.Equal("10", ft.Requests[1].QueryValue("limit"));
            Assert.Equal("c1", ft.Requests[1].QueryValue("cursor"));
        }

        [Fact]
        public async Task Storage_ListAll_stops_fetching_when_enumeration_abandoned()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"a\"},{\"key\":\"b\"}],\"next_cursor\":\"c1\"}"));
            using var client = NewClientWithSession(ft);

            await foreach (var obj in client.Storage.ListAllAsync())
            {
                if (obj.Key == "a")
                {
                    break;
                }
            }

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Storage_ListAll_observes_cancellation_between_pages()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"key\":\"a\"}],\"next_cursor\":\"c1\"}"));
            using var client = NewClientWithSession(ft);
            using var cts = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in client.Storage.ListAllAsync(null, cts.Token))
                {
                    cts.Cancel();
                }
            });

            Assert.Equal(1, ft.CallCount);
        }

        [Fact]
        public async Task Storage_ListAll_does_not_mutate_caller_options()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[],\"next_cursor\":\"c1\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[],\"next_cursor\":\"\"}"));
            using var client = NewClientWithSession(ft);
            var options = new StorageListOptions { KeyPrefix = "p" };

            await foreach (var _ in client.Storage.ListAllAsync(options))
            {
            }

            Assert.Null(options.Cursor);
        }

        [Fact]
        public async Task Friends_ListAll_iterates_pages_with_status_filter()
        {
            var ft = new FakeTransport();
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"id\":1}],\"next_cursor\":\"n\"}"));
            ft.EnqueueResult(JsonValue.Parse("{\"items\":[{\"id\":2}],\"next_cursor\":\"\"}"));
            using var client = NewClientWithSession(ft);

            var ids = new List<long>();
            await foreach (var f in client.Friends.ListAllAsync(new FriendsListOptions { Status = "pending" }))
            {
                ids.Add(f.Id);
            }

            Assert.Equal("1,2", string.Join(",", ids));
            Assert.Equal("pending", ft.Requests[0].QueryValue("status"));
            Assert.Equal("pending", ft.Requests[1].QueryValue("status"));
            Assert.Equal("n", ft.Requests[1].QueryValue("cursor"));
        }
    }
}
