using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// One typed connectivity address a player publishes so peers or game
    /// servers can reach them directly. Type is one of ip_lan, ip_public,
    /// dns, iroh (at most one address per type). Scope is derived
    /// server-side and ignored on writes.
    /// </summary>
    public sealed class RemoteAddr
    {
        /// <summary>Creates an address entry for publishing.</summary>
        public RemoteAddr(string type, string address, string scope = "")
        {
            Type = type;
            Address = address;
            Scope = scope;
        }

        /// <summary>Address type: ip_lan, ip_public, dns, or iroh.</summary>
        public string Type { get; }

        /// <summary>The address value.</summary>
        public string Address { get; }

        /// <summary>Server-derived scope (e.g. lan, public); ignored on writes.</summary>
        public string Scope { get; }

        internal static IReadOnlyList<RemoteAddr> ListFromJson(JsonValue resp)
        {
            var addrs = new List<RemoteAddr>();
            var arr = resp.Opt("addresses");
            if (arr != null)
            {
                foreach (var a in arr.Items)
                {
                    addrs.Add(new RemoteAddr(
                        a.OptString("type") ?? string.Empty,
                        a.OptString("address") ?? string.Empty,
                        a.OptString("scope") ?? string.Empty));
                }
            }
            return addrs;
        }

        internal static JsonValue ListToJson(IReadOnlyList<RemoteAddr> addrs)
        {
            var arr = JsonValue.NewArray();
            foreach (var a in addrs)
            {
                arr.Add(JsonValue.NewObject()
                    .Set("type", JsonValue.Of(a.Type))
                    .Set("address", JsonValue.Of(a.Address)));
            }
            return JsonValue.NewObject().Set("addresses", arr);
        }
    }

    /// <summary>
    /// The /v1/account endpoints operating on the calling player's linked
    /// account. Anonymous players get IsForbidden. Reach it via
    /// <see cref="GGScaleClient.Account"/>.
    /// </summary>
    public sealed class AccountService
    {
        private readonly GGScaleClient _client;

        internal AccountService(GGScaleClient client) => _client = client;

        /// <summary>Returns the calling player's published remote addresses.</summary>
        public async Task<IReadOnlyList<RemoteAddr>> RemoteAddrsAsync(CancellationToken cancellationToken = default)
        {
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/account/remote-addrs",
            }, cancellationToken).ConfigureAwait(false);
            return RemoteAddr.ListFromJson(resp);
        }

        /// <summary>
        /// Replaces the published address set (max 4, one per type) and
        /// returns the canonical list with server-derived scopes.
        /// </summary>
        public async Task<IReadOnlyList<RemoteAddr>> SetRemoteAddrsAsync(IReadOnlyList<RemoteAddr> addrs, CancellationToken cancellationToken = default)
        {
            if (addrs == null)
            {
                throw new System.ArgumentNullException(nameof(addrs));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "PUT",
                Path = "/v1/account/remote-addrs",
                Body = RemoteAddr.ListToJson(addrs),
            }, cancellationToken).ConfigureAwait(false);
            return RemoteAddr.ListFromJson(resp);
        }
    }
}
