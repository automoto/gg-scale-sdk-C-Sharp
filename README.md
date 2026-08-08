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

Other login strategies: `EmailPasswordAuth`, `CustomTokenAuth`, `SteamAuth`, and `OfflineAuth`. After login, call `DialRealtimeAsync` for realtime matchmaking, invite, and presence events.

## API contract

The canonical API contract is the [OpenAPI specification in the source ggscale repository](https://github.com/automoto/gg-scale/blob/main/openapi.yaml). Copies in client SDK repositories can go stale — treat the source spec as authoritative.

## License

Apache 2.0.
