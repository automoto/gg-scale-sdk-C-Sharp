# GGScale C# SDK

Official, engine-agnostic C# client for the [ggscale](https://github.com/automoto/gg-scale) multiplayer game backend.

## Features

- Authentication: anonymous, email/password, custom token, and Steam, plus account linking
- Player profiles, storage, friends, invites, presence, and friend codes
- Leaderboards, matchmaking, public sessions, join codes, and P2P signaling
- Peer-to-peer gameplay with TURN/STUN relay support
- Realtime events over WebSocket with automatic reconnection
- Remote config, player discovery, and remote-address exchange
- Dedicated-server support via plugin-based fleet discovery and game-server APIs

`GGScaleClient` exposes a typed service for each, with automatic session refresh, safe retries, structured `GGScaleException` errors, optional `IGGScaleLogger` telemetry, and `CancellationToken` support.

## Game engine support

The library targets `netstandard2.1` (the Unity profile) and `net8.0`, with no runtime dependencies or engine references:

| Engine | Target | Notes |
|---|---|---|
| Unity 2021.3+ | `netstandard2.1` | Mono and IL2CPP; AOT-safe serialization with no runtime reflection or codegen |
| Godot 4 (C#) | `net8.0` | Reference from a .NET-enabled Godot project |
| MonoGame | `net8.0` | Plain project reference; no adapter needed |
| Plain .NET | `net8.0` | Clients, tools, and dedicated game servers |

The SDK does not assume a main thread. Marshal callbacks to your engine's thread where the engine requires it.

## Requirements

- A running gg-scale server instance
- A publishable API key for game clients, or a secret key for server workloads
- A project targeting `netstandard2.1` or `net8.0`
- .NET 8 SDK when building from source

## Getting started

NuGet and Unity UPM packages are not published yet. Clone the repository and reference the core project:

```sh
git clone https://github.com/automoto/gg-scale-sdk-C-Sharp.git
dotnet add path/to/YourGame.csproj reference ./gg-scale-sdk-C-Sharp/src/GGScale/GGScale.csproj
```

For Unity, build the `netstandard2.1` target and copy `GGScale.dll` from the build output into `Assets/Plugins`:

```sh
dotnet build gg-scale-sdk-C-Sharp/src/GGScale/GGScale.csproj -c Release -f netstandard2.1
```

Create a client and establish a player session:

```csharp
using System;
using System.Threading;
using GGScale;

var cancellationToken = CancellationToken.None;
var apiKey = Environment.GetEnvironmentVariable("GGSCALE_API_KEY")!;
var store = new FileSessionStore(FileSessionStore.DefaultPath("my-game"));

using var client = new GGScaleClient(new GGScaleClientOptions
{
    BaseUrl = "http://localhost:8080",
    ApiKey = apiKey,
    OnSessionUpdate = session =>
    {
        if (session is not null)
        {
            store.Save(session);
        }
    },
});

await client.LoginAsync(
    new AnonymousAuth(client.Transport, apiKey, store),
    cancellationToken);

var profile = await client.Profile.GetAsync(cancellationToken);
```

Other login strategies: `EmailPasswordAuth`, `CustomTokenAuth`, `SteamAuth`, and `OfflineAuth`. After login, call `DialRealtimeAsync` for realtime matchmaking, invite, party, and presence events.

## Services

| Service | Methods |
|---|---|
| `Auth` | `SignupAsync`, `VerifyAsync`, `ResendVerificationAsync`, `RefreshAsync`, `LogoutAsync`, `LinkEmailAsync`, `LinkSteamAsync`, `ChangePasswordAsync`, `RequestPasswordResetAsync`, `ConfirmPasswordResetAsync`, `DisableAsync`, `RequestDeleteAsync`, `CancelDeleteAsync` |
| `Config` | `GetAsync` (`ETag` / `If-None-Match`, no player login) |
| `Storage` | `GetAsync`, `PutAsync`, `DeleteAsync`, `ListAsync`, `ListAllAsync` |
| `Leaderboards` | `ListAsync`, `SubmitAsync`, `SubmitForAsync`, `TopAsync`, `AroundMeAsync`, `FriendsAsync`, `PeriodsAsync`, `ListAllPeriodsAsync`, `PeriodTopAsync` |
| `Profile` | `GetAsync`, `UpdateAsync`, `RegenerateFriendCodeAsync` |
| `Players` | `GetAsync`, `ResolveAsync`, `ResolveFriendCodeAsync` |
| `Friends` | `ListAsync`, `ListAllAsync`, `RequestAsync`, `AcceptAsync`, `RejectAsync`, `RemoveAsync`, `BlockAsync`, `UnblockAsync`, `RemoteAddrsAsync` |
| `GameSessions` | `CreateAsync`, `ListAsync`, `ListAllAsync`, `GetAsync`, `ResolveAsync`, `JoinAsync`, `HeartbeatAsync`, `LeaveAsync`, and the P2P signal calls |
| `Invites` | `CreateAsync`, `ListAsync`, `DeleteAsync` |
| `Presence` | `SetAsync` |
| `Account` | `RemoteAddrsAsync`, `SetRemoteAddrsAsync` |
| `Matchmaker` | `CreateTicketAsync`, `GetTicketAsync`, `CancelTicketAsync`, `WaitForMatchAsync`, `ConnectP2PAsync` |
| `Parties` | `CreateAsync`, `CurrentAsync`, `GetAsync`, `UpdateAsync`, `DisbandAsync`, `HeartbeatAsync`, `JoinByCodeAsync`, `LeaveAsync`, `SetReadyAsync`, `KickAsync`, `CreateCodeAsync`, `RevokeCodeAsync`, `InviteFriendAsync`, `ListInvitesAsync`, `AcceptInviteAsync`, `DeclineInviteAsync`, `QueueAsync`, `CancelQueueAsync`, `RematchAsync`, `WatchAsync`, `WaitForMatchAsync` |
| `Fleets` | `ListServersAsync` (`SendHeartbeatAsync` is obsolete; use `Server.FleetHeartbeatAsync`) |
| `Relay` | `GetCredentialsAsync` |
| `Realtime` | `CreateTicketAsync` (one-time WebSocket ticket) |
| `Server` | `VerifySessionAsync`, `FleetHeartbeatAsync`, `SubmitScoreAsync`, `PlayerRemoteAddrsAsync`, `GetPlayerStorageAsync`, `PutPlayerStorageAsync`, `ListPlayerStorageAsync`, `ListAllStorageAsync` (secret API key) |
| `Health` | `GetAsync` |

Every service is a property of `GGScaleClient`, which is safe for concurrent use. A session refreshes when it is less than 30 seconds from expiry, and a 401 causes one refresh and one retry.

## Which key?

Game builds ship the publishable key. The secret key belongs only on a game server or backend. `GGScaleClient.Server` needs the secret key, and a publishable key gets 403 there. Every other service accepts either key.

## Realtime events

`DialRealtimeAsync` returns a `RealtimeClient`. Each message from `ReadMessageAsync` has a `Type` and a JSON `Payload`. Decode the payload with the `FromPayload` method of its type:

| `Type` | Payload type |
|---|---|
| `RealtimeEvents.MatchmakerMatched` | read by `Matchmaker.WaitForMatchAsync` |
| `RealtimeEvents.Presence` | `PresenceEvent` |
| `RealtimeEvents.GameInvite` | `GameInviteEvent` |
| `RealtimeEvents.PartyChanged` | `PartyChangedEvent` |
| `RealtimeEvents.PartyInvite` | `PartyInviteEvent` |

Events are best effort. To recover a missed event, call the matching GET.

### One socket per player

The server keeps one realtime socket per player, and a new connection closes the old one. `DialRealtimeAsync`, `Matchmaker.WaitForMatchAsync`, `Parties.WatchAsync`, and `Parties.WaitForMatchAsync` each open a socket, so use only one at a time. In a party game, make `Parties.WatchAsync` the only realtime reader.

### Browser and WebGL builds

Browsers cannot set WebSocket headers, so implement `ITicketSocketAdapter`. On every connect, reconnects included, the client gets a one-time ticket from `Realtime.CreateTicketAsync` and gives the adapter the URL `/v1/ws?ticket=...`, with no headers. The SDK does not ship a WebGL adapter. Add your game's page origin to the Game Project's allowed origins (in the dashboard, or with the MCP `set_allowed_origins` tool), or the server refuses the WebSocket.

## Parties

A party queues as one unit. Most writes take the party version you last saw. A stale version throws a `GGScaleException` with `IsStaleVersion` set, so read the party again and retry. Only the leader can update, disband, kick, invite, create or revoke codes, queue, cancel the queue, and rematch.

```csharp
var party = await leader.Parties.CreateAsync(new MatchRequest { Mode = MatchMode.MatchOnly, MinCount = 2, MaxCount = 4 });
var code = await leader.Parties.CreateCodeAsync(party.Id, party.Version);
// Share code.Code; a friend calls member.Parties.JoinByCodeAsync(code.Code).

// Each member: watch the party. The watch also sends the required heartbeat.
await foreach (var ev in member.Parties.WatchAsync(party.Id, cancellationToken: cancellationToken))
{
    if (ev.Party != null)
    {
        // newer party state: members, readiness, state
    }
    else if (ev.Match != null)
    {
        // the party matched; ev.Match is the same result as WaitForMatchAsync
    }
    else if (ev.Removed)
    {
        // kicked, left, disbanded, or removed by the sweep; the loop ends
    }
}
```

- Each member must send a heartbeat within 30 seconds, or the server removes them. `WatchAsync` sends one every 10 seconds while you enumerate it, so keep the loop body short. With party id 0, it reports only party invites.
- `QueueAsync` and `RematchAsync` send an `Idempotency-Key`. Pass your own key to retry a call safely, or null to let the SDK generate one. Both throw with `IsPartyEnqueueDisabled` when the server turns off party queueing.
- `JoinByCodeAsync` throws with `IsCodeCooldown` after too many wrong codes. Wait for the server-set `RetryAfter`.
- `ListInvitesAsync` is the source of truth for invites. Inviting a friend who already has a pending invite sends no new `party_invite` event.
- `PartyMember.Attributes` come back exactly as the member sent them, HTML included. Escape them before you display them.

## Defaults

### HTTP retries

A call makes up to three attempts, with capped full-jitter exponential backoff, inside one call budget (`GGScaleClientOptions.OverallTimeout`, 100 seconds). Configure retries with `GGScaleClientOptions.Retry`.

The SDK retries only requests that are safe to repeat: `GET`, `HEAD`, and writes with an `Idempotency-Key` (`Parties.QueueAsync`, `Parties.RematchAsync`) or `GGRequest.Idempotent`. It retries on connection failures, timeouts, and 408, 429, 502, 503, and 504, and it follows the server's `Retry-After`. It never retries other writes, because a lost response does not show whether the write ran, and it never retries a 503 `party_enqueue_disabled`.

One exception: the SDK retries any request, writes included, when the request provably never reached the server. That covers a temporary name-resolution failure or a connection that could not open (refused, no route). These failures have `Code` `connect_failed` and use the same backoff, budget, and attempt limit. A connect timeout, a reset after the connection opened, and a TLS or certificate failure do not count. A host name that does not exist has `Code` `host_not_found` and is never retried.

### Realtime reconnect

Reconnect is on by default. After an abnormal close, the client reconnects with capped full-jitter backoff, up to 5 attempts per outage (`RealtimeOptions.MaxReconnectAttempts`). It follows a handshake `Retry-After` and refreshes the session before each connect. Set `RealtimeOptions.AutoReconnect = false` to turn it off.

The server does not resend events missed during an outage. When `RealtimeClient.StateChanged` reports `Connected` with `IsReconnect` true, read tickets, invites, party, and presence again.

## Errors

Every failure is a `GGScaleException`. `Kind` gives the class of failure: `HttpError` (a response arrived), `Connection`, `Timeout`, `Decode`, `Handshake` (a WebSocket connect failed), or `ConnectionClosed` (an open WebSocket dropped and the client stopped; see `CloseCode`). HTTP errors carry `Status`, `Detail`, `Details`, `RetryAfter`, and `RequestId`, plus these flags: `IsUnauthorized`, `IsForbidden`, `IsNotFound`, `IsConflict`, `IsRateLimited`, `IsBadRequest`, `IsValidationError`, `IsTicketAlreadyActive`, `IsStaleVersion`, `IsPartyEnqueueDisabled`, `IsCodeCooldown`, `IsDeleteRequestedByTeam`.

`Auth.CancelDeleteAsync` throws with `IsDeleteRequestedByTeam` (and `IsForbidden`) when the game's team requested the deletion, because only the team can cancel it. A 403 for a revoked key or a disabled tenant does not set `IsDeleteRequestedByTeam`.

## Development

```sh
make check             # lint + build + unit tests (the CI gate)
make test              # unit tests only
make openapi-check     # every operation in the server spec has a wrapper
make test-integration  # full-stack tests against a real server (Docker)
```

### API contract

`openapi.yaml` in the [gg-scale repository](https://github.com/automoto/gg-scale/blob/main/openapi.yaml) is the only contract for this SDK, and there is no copy here. `make openapi-check` downloads the spec at server tag `SPEC_REF` (currently `v0.9.71`) and runs `ContractTests`. They check that every operation appears as `Operation = "METHOD path"` in `src/GGScale`, that every secret-key operation is in `ServerService.cs`, and that no other operation appears only there. CI runs this check.

To use a local spec, run `make openapi-check SPEC=../../ggscale/openapi.yaml`. Syncing to a new server release changes only `SPEC_REF`. Without `GGSCALE_SPEC`, `make test` skips the spec check, so unit tests stay offline.

### Integration tests

`make test-integration` starts Postgres and `ghcr.io/automoto/gg-scale:v0.9.71` with docker compose (set `GGSCALE_IMAGE` to use another image). It seeds a tenant, a project, and API keys from `integration/seed.sql`, runs `tests/GGScale.IntegrationTests` against `127.0.0.1:18081`, and then stops the stack. Set `KEEP_STACK=1` to leave it running.

## License

Apache 2.0.
