# gg-scale-sdk-C-Sharp

Official C# client for the [ggscale](https://github.com/automoto/gg-scale) API — engine-agnostic by design: Unity (2021.3+), MonoGame, Godot 4, and plain .NET consume the same core library.

> **Status:** targets ggscale server **v0.9.3** (GA) with full endpoint parity — remote config, Steam sign-in, account linking, password reset, players directory, friend codes, leaderboard discovery/periods, the public session browser, P2P signaling, and the server tier — plus the SDK best-practices layer: automatic retries with jittered backoff, structured telemetry hooks, and a managed reconnecting WebSocket client. Engine packaging (Unity UPM, samples) is in progress; see [`docs/temp/mvp.md`](docs/temp/mvp.md).

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
| `client.Auth` | `SignupAsync`, `VerifyAsync`, `RefreshAsync`, `LogoutAsync`, `LinkEmailAsync`, `LinkSteamAsync`, `ChangePasswordAsync`, `DisableAsync`, `RequestPasswordResetAsync`, `ConfirmPasswordResetAsync`, `ResendVerificationAsync` |
| `client.Config` | `GetAsync` (ETag/304 revalidation; works before login) |
| `client.Storage` | `GetAsync`, `PutAsync` (OCC), `DeleteAsync`, `ListAsync`, `ListAllAsync` |
| `client.Leaderboards` | `SubmitAsync`, `SubmitForAsync` (both take optional metadata), `TopAsync`, `AroundMeAsync`, `ListAsync`, `FriendsAsync`, `PeriodsAsync`, `PeriodTopAsync` |
| `client.Profile` | `GetAsync`, `UpdateAsync` (email/xuid/display name), `RegenerateFriendCodeAsync` |
| `client.Players` | `GetAsync`, `ResolveAsync`, `ResolveFriendCodeAsync` |
| `client.Friends` | `ListAsync`, `ListAllAsync`, `RequestAsync`, `AcceptAsync`, `RejectAsync`, `RemoveAsync`, `BlockAsync`, `UnblockAsync`, `RemoteAddrsAsync` |
| `client.GameSessions` | `CreateAsync`, `GetAsync`, `ResolveAsync`, `JoinAsync`, `HeartbeatAsync`, `LeaveAsync`, `ListAsync` (public browser), `SendSignalAsync`, `PollSignalsAsync` |
| `client.Invites` | `CreateAsync`, `ListAsync`, `DeleteAsync` |
| `client.Presence` | `SetAsync` |
| `client.Account` | `RemoteAddrsAsync`, `SetRemoteAddrsAsync` |
| `client.Matchmaker` | `CreateTicketAsync`, `GetTicketAsync`, `CancelTicketAsync`, `WaitForMatchAsync`, `ConnectP2PAsync` |
| `client.Fleets` | `SendHeartbeatAsync`, `ListServersAsync` |
| `client.Relay` | `GetCredentialsAsync` (TURN + STUN URLs) |
| `client.Server` | `VerifySessionAsync`, `PlayerRemoteAddrsAsync`, `SubmitScoreAsync`, `GetPlayerStorageAsync`, `PutPlayerStorageAsync`, `ListPlayerStorageAsync` (secret API key) |

Realtime pushes (`matchmaker_matched`, `game_invite`, `presence`) arrive via `client.DialRealtimeAsync()` → `ReadMessageAsync()`. The returned `RealtimeClient` runs a continuous read loop (so server pings are answered), buffers pushes in a bounded queue, and reconnects automatically with jittered backoff after retryable drops — refreshing the session token first. Subscribe to `StateChanged`: a `Connected` change with `IsReconnect == true` means delivery gaps are possible — re-read pending invites, active matchmaker tickets, and presence over REST. Configure with `RealtimeOptions` (`AutoReconnect`, `QueueCapacity`, reconnect delays); pass `AutoReconnect = false` to manage recovery yourself.

## Reliability and observability

- **Retries**: safe-to-replay requests (GET/HEAD/PUT/DELETE) retry automatically on connection failures, timeouts, and HTTP 408/429/502/503/504 — 3 total attempts by default, full-jitter backoff (`random(0, min(10s, 250ms × 2^n))`), and `Retry-After` honored as a minimum wait. POST/PATCH are never retried automatically; set `GGRequest.Idempotent` only through your own transport wrapper if you know a mutation is replayable. TLS/certificate validation failures are never retried. Tune via `GGScaleClientOptions.Retry`.
- **Deadlines**: `Timeout` bounds each attempt (default 30 s); `OverallTimeout` bounds the whole logical call including backoff (default 100 s) and cancels an in-flight attempt when the budget runs out. Response bodies are capped at `MaxResponseBytes` (default 4 MiB).
- **Telemetry**: the SDK is silent by default. Set `GGScaleClientOptions.Logger` (`IGGScaleLogger`) to receive exactly one completion record per logical call — including across the automatic 401 refresh-and-retry — plus per-retry records and WebSocket lifecycle events. Records carry the route template, method, status, duration, attempts, request id, and SDK version; never URLs, headers, bodies, or tokens. Every call carries a client-generated `X-Request-Id` that stays stable across retries — quote it in support requests.
- **Errors**: one exception type, `GGScaleException`, carrying `Status`, `Code`, problem-details `ProblemType`/`Title`/`Instance`, `RequestId`, `RetryAfter`, `ConflictVersion`, and a `Kind` (`Connection`, `Timeout`, `Decode`, `Handshake`, `HttpError`) that separates transport failures from HTTP errors. Caller cancellation surfaces as `OperationCanceledException`.

Game-session lifetime: a session lives in a one-hour sliding window — member
`HeartbeatAsync` calls extend it while the match runs, and an idle session
expires within the hour. When the match ends, the host should call
`LeaveAsync` (DELETE) so the session stops counting against the project's
open-session limit immediately.

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

Requires the .NET 8+ SDK. Integration tests bring up Postgres + `buildwrangler/ggscale:v0.9.3` via docker compose on `127.0.0.1:18081`, seed a tenant/project/API keys directly (`integration/seed.sql`), and tear down afterwards. `KEEP_STACK=1` keeps the stack; `GGSCALE_IT_PULL=never` tests a locally built server image.

## Contributing / agents

Read [`CLAUDE.md`](CLAUDE.md) (or [`AGENTS.md`](AGENTS.md)) first — TDD is mandatory and the engine-compatibility constraints are hard requirements. Then pick up the next unchecked milestone item in [`docs/temp/mvp.md`](docs/temp/mvp.md).

## License

Apache 2.0.
