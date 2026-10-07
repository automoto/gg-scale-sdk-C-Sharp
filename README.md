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

`GGScaleClient` exposes typed services for all of the above, with automatic session refresh, safe retries, structured `GGScaleException` errors, optional `IGGScaleLogger` telemetry, and `CancellationToken` support throughout.

## Game engine support

One codebase runs unmodified in every major C# game environment. The library ships two targets — `netstandard2.1` (Unity profile) and `net8.0` — with **zero runtime dependencies and no engine references**, so nothing needs to be ported or shimmed per engine.

| Engine | Target | How it works |
|---|---|---|
| Unity 2021.3+ | `netstandard2.1` | Works on Mono and IL2CPP; serialization is AOT-safe with no runtime reflection or codegen |
| Godot 4 (C#) | `net8.0` | Reference directly from a .NET-enabled Godot project |
| MonoGame | `net8.0` | Plain project reference; no adapter required |
| Plain .NET | `net8.0` | Clients, tools, and dedicated game servers |

The SDK is engine-agnostic by design: it never touches engine APIs and does not assume a main thread. Marshal callbacks to your engine's thread where the engine requires it.

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

Every service is a property of `GGScaleClient`. The client is safe for concurrent use. A session refreshes when it is less than 30 seconds from expiry, and a 401 causes one refresh and one more try.

## Which key?

A game ships the **publishable key**. Use the **secret key** only on a game server or backend, never in a game build. Operations on `GGScaleClient.Server` need the secret key, and a publishable key gets 403 there. Every other service takes the publishable key. A secret key also works there, but it must never ship in a game.

## Realtime events

`DialRealtimeAsync` returns a `RealtimeClient`. Each message from `ReadMessageAsync` has a `Type` and a JSON `Payload`. Read the payload with the `FromPayload` method of its type:

| `Type` | Payload type |
|---|---|
| `RealtimeEvents.MatchmakerMatched` | read by `Matchmaker.WaitForMatchAsync` |
| `RealtimeEvents.Presence` | `PresenceEvent` |
| `RealtimeEvents.GameInvite` | `GameInviteEvent` |
| `RealtimeEvents.PartyChanged` | `PartyChangedEvent` |
| `RealtimeEvents.PartyInvite` | `PartyInviteEvent` |

Events are best effort. A client that misses one gets the state again with the matching GET.

**One socket per player.** The server keeps one realtime socket for each player. A new connection closes the older socket of that player. `DialRealtimeAsync`, `Matchmaker.WaitForMatchAsync`, `Parties.WatchAsync` and `Parties.WaitForMatchAsync` each open a socket, so use only one of them at a time. In a party game, make `Parties.WatchAsync` the single realtime reader.

**Browser and WebGL builds.** A browser cannot set WebSocket headers. For these platforms, write a socket adapter that implements `ITicketSocketAdapter`. The client then gets a one-time ticket with `Realtime.CreateTicketAsync` for each connect, reconnects included, and gives the adapter the URL `/v1/ws?ticket=...`. The adapter sends no headers. The SDK does not ship a WebGL adapter. Add the page origin of your game to the Game Project's allowed origins (in the dashboard, or with the MCP `set_allowed_origins` tool), or the server refuses the WebSocket.

## Parties

A party queues as one unit. Most writes take the party version you last saw. A stale version throws a `GGScaleException` with `IsStaleVersion` set: read the party again and try again. Only the leader can update, disband, kick, invite, create or revoke codes, queue, cancel the queue, and rematch.

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

- Each member must send a heartbeat within 30 seconds, or the server removes the member. `WatchAsync` sends one every 10 seconds while you enumerate it, so keep the loop body short. With party id 0 it reports party invites only.
- `QueueAsync` and `RematchAsync` send an `Idempotency-Key`. Give your own key to try a call again safely, or null to let the SDK make one. They throw with `IsPartyEnqueueDisabled` when the server turns party queue off.
- `JoinByCodeAsync` throws with `IsCodeCooldown` after too many wrong codes. Wait for `RetryAfter`; the server sets the cooldown.
- `ListInvitesAsync` is the source of truth for invites. A new invite to a friend who has a pending invite sends no new `party_invite` event.
- `PartyMember.Attributes` come back exactly as the member sent them, HTML included. Escape them before you show them.

## Defaults

- **HTTP retries:** up to three attempts in total, with capped full-jitter exponential backoff, inside one call budget (`GGScaleClientOptions.OverallTimeout`, 100 seconds). Only requests that are safe to repeat are retried: `GET` and `HEAD`, and writes with an `Idempotency-Key` (`Parties.QueueAsync`, `Parties.RematchAsync`) or `GGRequest.Idempotent`. Only connection failures, timeouts, and 408, 429, 502, 503 and 504 are retried, and a `Retry-After` from the server is followed. Other writes are never retried, because a lost response does not show whether the write ran. A 503 `party_enqueue_disabled` is never retried. One more case: any request, a write too, is retried when the failure proves that the request never reached the server: name resolution failed for now, or the connection could not open (refused, no route). Such an exception has `Code` `connect_failed`. A host name that does not exist has `Code` `host_not_found` and is not retried for any method, because a retry cannot help. A connect timeout, a reset after the connection opened, and a TLS or certificate failure are not this case. The same backoff, budget and attempt limit apply. Configure with `GGScaleClientOptions.Retry`.
- **Realtime reconnect:** on. After an abnormal close, the client reconnects with capped full-jitter backoff, at most 5 attempts for one outage (`RealtimeOptions.MaxReconnectAttempts`). A handshake `Retry-After` is followed. The session refreshes before each connect. The server does not send again the events of an outage, so when `RealtimeClient.StateChanged` reports `Connected` with `IsReconnect` true, read the state again (tickets, invites, party, presence). Set `RealtimeOptions.AutoReconnect = false` to turn reconnect off.

## Errors

Every failure is a `GGScaleException`. `Kind` tells the class of failure: `HttpError` (a response came), `Connection`, `Timeout`, `Decode`, `Handshake` (a WebSocket connect failed), and `ConnectionClosed` (an open WebSocket dropped and the client stopped; see `CloseCode`). For an HTTP error, read `Status`, `Detail`, `Details`, `RetryAfter` and `RequestId`, and use the flags: `IsUnauthorized`, `IsForbidden`, `IsNotFound`, `IsConflict`, `IsRateLimited`, `IsBadRequest`, `IsValidationError`, `IsTicketAlreadyActive`, `IsStaleVersion`, `IsPartyEnqueueDisabled`, `IsCodeCooldown`, `IsDeleteRequestedByTeam`.

`Auth.CancelDeleteAsync` throws with `IsDeleteRequestedByTeam` (and `IsForbidden`) when the game's team requested the deletion. Only the team can cancel it. A 403 for a revoked key or a disabled tenant does not set `IsDeleteRequestedByTeam`.

## Development

```sh
make check             # lint + build + unit tests (the CI gate)
make test              # unit tests only
make openapi-check     # every operation in the server spec has a wrapper
make test-integration  # full-stack tests against a real server (Docker)
```

### API contract

The [gg-scale repository](https://github.com/automoto/gg-scale/blob/main/openapi.yaml) owns `openapi.yaml`, the only contract for this SDK. There is no copy of it here. `make openapi-check` downloads the spec of the server tag `SPEC_REF` (now `v0.9.71`) and runs `ContractTests`. The test checks that each operation occurs as `Operation = "METHOD path"` in `src/GGScale`, that each secret-key operation is in `ServerService.cs`, and that no other operation is only there. CI runs it.

Use a local spec with `make openapi-check SPEC=../../ggscale/openapi.yaml`. A sync with a new server release changes only `SPEC_REF`. Without `GGSCALE_SPEC`, `make test` does not check the spec, so unit tests stay offline.

### Integration tests

`make test-integration` starts Postgres and `ghcr.io/automoto/gg-scale:v0.9.71` with docker compose (set `GGSCALE_IMAGE` for another image), seeds a tenant, a project, and API keys with `integration/seed.sql`, runs `tests/GGScale.IntegrationTests` against `127.0.0.1:18081`, and stops the stack. Set `KEEP_STACK=1` to keep the stack running.

## License

Apache 2.0.
