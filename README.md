# GGScale C# SDK

The GGScale C# SDK is the official, engine-agnostic client for the [ggscale](https://github.com/automoto/gg-scale) multiplayer game backend. It provides authentication, player data, social features, leaderboards, matchmaking, game sessions, realtime events, relay credentials, and server APIs. SDK `0.2.0` supports Unity, Godot, MonoGame, and .NET against ggscale server `v0.9.3`.

## Features

- Anonymous, email/password, custom-token, and Steam authentication
- Account linking, player profiles, storage, friends, invites, and presence
- Remote config, player discovery, friend codes, and remote-address exchange
- Leaderboards, matchmaking, public sessions, join codes, and P2P signaling
- Peer to peer gameplay support with support for relays when needed. TURN/STUN support provided.
- Managed realtime events with automatic WebSocket reconnection
- Dedicated server support through a plugin based  fleet discovery, and game-server APIs

## SDK

`GGScaleClient` provides typed services for authentication, config, players, social features, leaderboards, matchmaking, sessions, relay, fleets, and server workloads.

The core library includes:

- Automatic session refresh, safe retries, and bounded deadlines
- Structured `GGScaleException` errors and optional `IGGScaleLogger` telemetry
- Task-based APIs with `CancellationToken` support
- Zero runtime dependencies and no engine references
- AOT/IL2CPP-safe serialization without runtime reflection

## Requirements

- A ggscale `v0.9.3` server URL
- A publishable API key for game clients or a secret key for server workloads
- A project compatible with `netstandard2.1` or `net8.0`
- .NET 8 SDK when building the library from source

## Supported engines

| Runtime | Target | Notes |
|---|---|---|
| Unity 2021.3+ | `netstandard2.1` | Mono and IL2CPP |
| Godot 4 C# | `net8.0` | .NET-enabled projects |
| MonoGame | `net8.0` | No engine adapter required |
| Plain .NET | `net8.0` | Clients, tools, and game servers |

The SDK does not assume a main thread. Marshal callbacks to the engine thread when required.

## Getting started

NuGet and Unity UPM packages are not published yet. Clone the repository and reference the core project from .NET, Godot, or MonoGame:

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

Other login strategies are available through `EmailPasswordAuth`, `CustomTokenAuth`, `SteamAuth`, and `OfflineAuth`. After login, call `DialRealtimeAsync` for realtime matchmaking, invite, and presence events.

## License

Apache 2.0.
