# gg-scale-sdk-C-Sharp

Official C# client for the [ggscale](https://github.com/automoto/gg-scale) API — engine-agnostic by design: Unity (2021.3+), MonoGame, Godot 4, and plain .NET consume the same core library.

> **Status:** feature-complete against the production v1 API — every endpoint the [Go SDK](https://github.com/automoto/ggscale-go) covers, validated by an integration suite that runs the full stack (127 unit tests + 11 full-stack tests). Engine packaging (Unity UPM, samples) is in progress; see [`docs/temp/mvp.md`](docs/temp/mvp.md).

## Quickstart

```csharp
using GGScale;

var apiKey = Environment.GetEnvironmentVariable("GGSCALE_API_KEY")!;
var client = new GGScaleClient(new GGScaleClientOptions
{
    BaseUrl = "http://localhost:8080",
    ApiKey = apiKey,
});
await client.LoginAsync(new AnonymousAuth(client.Transport, apiKey,
    new FileSessionStore(FileSessionStore.DefaultPath("my-game"))));

var top = await client.Leaderboards.TopAsync(1, 10);
var session = await client.GameSessions.CreateAsync(new GameSessionCreate
{
    PublicAddr = new GameSessionAddr("203.0.113.1", 7777),
    MaxPlayers = 4,
});
Console.WriteLine($"share this join code: {session.JoinCode}");
```

## Services

| Service | Methods |
|---|---|
| `client.Auth` | `SignupAsync`, `VerifyAsync`, `RefreshAsync`, `LogoutAsync` |
| `client.Storage` | `GetAsync`, `PutAsync` (OCC), `DeleteAsync`, `ListAsync` |
| `client.Leaderboards` | `SubmitAsync`, `SubmitForAsync`, `TopAsync`, `AroundMeAsync` |
| `client.Profile` | `GetAsync`, `UpdateAsync` |
| `client.Friends` | `ListAsync`, `RequestAsync`, `AcceptAsync`, `RejectAsync`, `RemoveAsync`, `BlockAsync`, `UnblockAsync`, `RemoteAddrsAsync` |
| `client.GameSessions` | `CreateAsync`, `GetAsync`, `ResolveAsync`, `JoinAsync`, `HeartbeatAsync`, `LeaveAsync` |
| `client.Invites` | `CreateAsync`, `ListAsync`, `DeleteAsync` |
| `client.Presence` | `SetAsync` |
| `client.Account` | `RemoteAddrsAsync`, `SetRemoteAddrsAsync` |
| `client.Matchmaker` | `CreateTicketAsync`, `GetTicketAsync`, `CancelTicketAsync`, `RequestMatchAsync` |
| `client.Fleets` | `SendHeartbeatAsync`, `ListServersAsync` |
| `client.Relay` | `GetCredentialsAsync` |
| `client.Server` | `VerifySessionAsync`, `PlayerRemoteAddrsAsync` (secret API key) |

Realtime pushes (`match_ready`, `game_invite`, `presence`) arrive via `client.DialRealtimeAsync()` → `ReadMessageAsync()`.

Errors are one type: `GGScaleException` with `Status`, `Code`, `RetryAfter`, `ConflictVersion` and helpers (`IsNotFound`, `IsConflict`, `IsRateLimited`, …).

## Design constraints

- **Zero runtime dependencies** — no System.Text.Json, no Newtonsoft; a minimal hand-written JSON layer keeps the core portable and IL2CPP/AOT-safe.
- **`netstandard2.1` core** (the Unity profile), with `net8.0` as a secondary target for MonoGame/Godot/plain .NET.
- **No engine references in the core** — engine adaptations (Unity UPM package, samples) live in their own folders.
- **Task-based async** with `CancellationToken` everywhere; no main-thread assumptions (marshal to your engine's main thread yourself).

## Development

```sh
make check             # lint + build + unit tests (the CI gate)
make build             # dotnet build (warnings are errors)
make test              # unit tests (no network needed)
make test-integration  # full-stack tests against a real server (Docker)
make lint              # dotnet format --verify-no-changes
make format            # auto-fix formatting
```

Requires the .NET 8+ SDK. Integration tests bring up Postgres + `buildwrangler/ggscale:latest` via docker compose on `127.0.0.1:18081`, seed a tenant/project/API keys directly (`integration/seed.sql`), and tear down afterwards. `KEEP_STACK=1` keeps the stack; `GGSCALE_IT_PULL=never` tests a locally built server image.

## Contributing / agents

Read [`CLAUDE.md`](CLAUDE.md) (or [`AGENTS.md`](AGENTS.md)) first — TDD is mandatory and the engine-compatibility constraints are hard requirements. Then pick up the next unchecked milestone item in [`docs/temp/mvp.md`](docs/temp/mvp.md).

## License

Apache 2.0.
