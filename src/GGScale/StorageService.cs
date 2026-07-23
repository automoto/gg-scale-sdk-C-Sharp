using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>A single key/value entry in per-player storage.</summary>
    public sealed class StorageObject
    {
        internal StorageObject(string key, JsonValue value, long version, DateTimeOffset updatedAt)
        {
            Key = key;
            Value = value;
            Version = version;
            UpdatedAt = updatedAt;
        }

        /// <summary>The object key.</summary>
        public string Key { get; }

        /// <summary>The stored raw JSON value.</summary>
        public JsonValue Value { get; }

        /// <summary>The version used for optimistic concurrency (If-Match).</summary>
        public long Version { get; }

        /// <summary>Last write time.</summary>
        public DateTimeOffset UpdatedAt { get; }

        internal static StorageObject FromJson(JsonValue v) =>
            new StorageObject(
                v.OptString("key") ?? string.Empty,
                v.Opt("value") ?? JsonValue.Null,
                v.OptLong("version"),
                v.OptTime("updated_at") ?? DateTimeOffset.MinValue);
    }

    /// <summary>One page of storage List results; NextCursor is empty on the last page.</summary>
    public sealed class StoragePage
    {
        internal StoragePage(IReadOnlyList<StorageObject> items, string nextCursor)
        {
            Items = items;
            NextCursor = nextCursor;
        }

        /// <summary>The page's objects, oldest first.</summary>
        public IReadOnlyList<StorageObject> Items { get; }

        /// <summary>Cursor for the next page; empty when done.</summary>
        public string NextCursor { get; }
    }

    /// <summary>Options for <see cref="StorageService.ListAsync"/>.</summary>
    public sealed class StorageListOptions
    {
        /// <summary>Filter keys by prefix.</summary>
        public string? KeyPrefix { get; set; }

        /// <summary>Page size; the server defaults to 50 and caps at 100.</summary>
        public int Limit { get; set; }

        /// <summary>NextCursor from the previous page (empty for the first call).</summary>
        public string? Cursor { get; set; }
    }

    /// <summary>
    /// Per-player JSON storage (/v1/storage/objects). Requires a player
    /// session. Reach it via <see cref="GGScaleClient.Storage"/>.
    /// </summary>
    public sealed class StorageService
    {
        private readonly GGScaleClient _client;

        internal StorageService(GGScaleClient client) => _client = client;

        /// <summary>Returns the object at key; IsNotFound when absent or deleted.</summary>
        public async Task<StorageObject> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = StoragePath(key),
            }, cancellationToken).ConfigureAwait(false);
            return StorageObject.FromJson(resp);
        }

        /// <summary>
        /// Writes value at key and returns the stored object with its new
        /// version. Pass <paramref name="ifMatchVersion"/> to enforce
        /// optimistic concurrency: a mismatch throws with IsConflict true.
        /// </summary>
        public async Task<StorageObject> PutAsync(string key, JsonValue value, long? ifMatchVersion = null, CancellationToken cancellationToken = default)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var req = new GGRequest
            {
                Method = "PUT",
                Path = StoragePath(key),
                Body = value,
            };
            if (ifMatchVersion != null)
            {
                req.IfMatch = ifMatchVersion.Value.ToString(CultureInfo.InvariantCulture);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            return StorageObject.FromJson(resp);
        }

        /// <summary>Soft-deletes the object at key; a later Get reports IsNotFound.</summary>
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            return _client.CallProtectedAsync(new GGRequest
            {
                Method = "DELETE",
                Path = StoragePath(key),
            }, cancellationToken);
        }

        /// <summary>Pages through the calling player's objects, oldest first.</summary>
        public async Task<StoragePage> ListAsync(StorageListOptions? options = null, CancellationToken cancellationToken = default)
        {
            var req = new GGRequest { Method = "GET", Path = "/v1/storage/objects" };
            if (!string.IsNullOrEmpty(options?.KeyPrefix))
            {
                req.AddQuery("key_prefix", options!.KeyPrefix!);
            }
            if (options?.Limit > 0)
            {
                req.AddQuery("limit", options.Limit.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(options?.Cursor))
            {
                req.AddQuery("cursor", options!.Cursor!);
            }
            var resp = await _client.CallProtectedAsync(req, cancellationToken).ConfigureAwait(false);
            var items = new List<StorageObject>();
            var arr = resp.Opt("items");
            if (arr != null)
            {
                foreach (var item in arr.Items)
                {
                    items.Add(StorageObject.FromJson(item));
                }
            }
            return new StoragePage(items, resp.OptString("next_cursor") ?? string.Empty);
        }

        private static string StoragePath(string key) => "/v1/storage/objects/" + Uri.EscapeDataString(key);
    }
}
