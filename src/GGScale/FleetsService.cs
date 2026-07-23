using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GGScale.Json;

namespace GGScale
{
    /// <summary>
    /// The payload a game-server process sends to announce liveness.
    /// AgonesName is the unique upsert key — use the Agones GameServer CR
    /// name (or any stable per-process id) so duplicates upsert instead of
    /// double-listing.
    /// </summary>
    public sealed class FleetHeartbeat
    {
        /// <summary>Unique server key (required).</summary>
        public string? AgonesName { get; set; }

        /// <summary>Fleet the server belongs to (required).</summary>
        public string? Fleet { get; set; }

        /// <summary>Player-reachable address (required).</summary>
        public string? Address { get; set; }

        /// <summary>Region label.</summary>
        public string? Region { get; set; }

        /// <summary>Display name.</summary>
        public string? Name { get; set; }

        /// <summary>Current player count.</summary>
        public int CurrentPlayers { get; set; }

        /// <summary>Player capacity (required, &gt; 0).</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Game mode label.</summary>
        public string? GameMode { get; set; }

        /// <summary>Level/map label.</summary>
        public string? Level { get; set; }

        /// <summary>Server build version.</summary>
        public string? Version { get; set; }

        internal JsonValue ToJson() =>
            JsonValue.NewObject()
                .Set("agones_name", JsonValue.Of(AgonesName ?? string.Empty))
                .Set("fleet", JsonValue.Of(Fleet ?? string.Empty))
                .Set("address", JsonValue.Of(Address ?? string.Empty))
                .Set("region", JsonValue.Of(Region ?? string.Empty))
                .Set("name", JsonValue.Of(Name ?? string.Empty))
                .Set("current_players", JsonValue.Of((long)CurrentPlayers))
                .Set("max_players", JsonValue.Of((long)MaxPlayers))
                .Set("game_mode", JsonValue.Of(GameMode ?? string.Empty))
                .Set("level", JsonValue.Of(Level ?? string.Empty))
                .Set("version", JsonValue.Of(Version ?? string.Empty));
    }

    /// <summary>One entry in a ListServersAsync response.</summary>
    public sealed class GameServerInfo
    {
        internal GameServerInfo(string name, string address, string region, int currentPlayers, int maxPlayers, string gameMode, string level, string version)
        {
            Name = name;
            Address = address;
            Region = region;
            CurrentPlayers = currentPlayers;
            MaxPlayers = maxPlayers;
            GameMode = gameMode;
            Level = level;
            Version = version;
        }

        /// <summary>Display name.</summary>
        public string Name { get; }

        /// <summary>Player-reachable address.</summary>
        public string Address { get; }

        /// <summary>Region label.</summary>
        public string Region { get; }

        /// <summary>Current player count.</summary>
        public int CurrentPlayers { get; }

        /// <summary>Player capacity.</summary>
        public int MaxPlayers { get; }

        /// <summary>Game mode label.</summary>
        public string GameMode { get; }

        /// <summary>Level/map label.</summary>
        public string Level { get; }

        /// <summary>Server build version.</summary>
        public string Version { get; }

        internal static GameServerInfo FromJson(JsonValue v) =>
            new GameServerInfo(
                v.OptString("name") ?? string.Empty,
                v.OptString("address") ?? string.Empty,
                v.OptString("region") ?? string.Empty,
                (int)v.OptLong("current_players"),
                (int)v.OptLong("max_players"),
                v.OptString("game_mode") ?? string.Empty,
                v.OptString("level") ?? string.Empty,
                v.OptString("version") ?? string.Empty);
    }

    /// <summary>
    /// Server-browser endpoints: game clients list live servers with a
    /// player session; game-server processes heartbeat with a secret API
    /// key (every ~5 s; entries expire after ~15 s without one). Reach it
    /// via <see cref="GGScaleClient.Fleets"/>.
    /// </summary>
    public sealed class FleetsService
    {
        private readonly GGScaleClient _client;

        internal FleetsService(GGScaleClient client) => _client = client;

        /// <summary>
        /// Announces this game-server's liveness and player count. Requires
        /// a secret-tier API key on the client; no player session.
        /// </summary>
        public Task SendHeartbeatAsync(FleetHeartbeat heartbeat, CancellationToken cancellationToken = default)
        {
            if (heartbeat == null)
            {
                throw new ArgumentNullException(nameof(heartbeat));
            }
            if (string.IsNullOrEmpty(heartbeat.AgonesName) || string.IsNullOrEmpty(heartbeat.Fleet) || string.IsNullOrEmpty(heartbeat.Address))
            {
                throw new ArgumentException("heartbeat requires AgonesName, Fleet, and Address", nameof(heartbeat));
            }
            if (heartbeat.MaxPlayers <= 0)
            {
                throw new ArgumentException("heartbeat MaxPlayers must be > 0", nameof(heartbeat));
            }
            return _client.Transport.CallAsync(new GGRequest
            {
                Method = "POST",
                Path = "/v1/fleets/heartbeat",
                ApiKey = _client.ApiKey,
                Body = heartbeat.ToJson(),
            }, cancellationToken);
        }

        /// <summary>Returns the live game-servers for the fleet. Requires a player session.</summary>
        public async Task<IReadOnlyList<GameServerInfo>> ListServersAsync(string fleet, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fleet))
            {
                throw new ArgumentException("fleet is required", nameof(fleet));
            }
            var resp = await _client.CallProtectedAsync(new GGRequest
            {
                Method = "GET",
                Path = "/v1/fleets/" + Uri.EscapeDataString(fleet) + "/servers",
            }, cancellationToken).ConfigureAwait(false);
            var servers = new List<GameServerInfo>();
            var arr = resp.Opt("servers");
            if (arr != null)
            {
                foreach (var s in arr.Items)
                {
                    servers.Add(GameServerInfo.FromJson(s));
                }
            }
            return servers;
        }
    }
}
