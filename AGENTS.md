# Repository Instructions

Official C# client for the ggscale API. Must run unmodified in Unity
(2021.3+), MonoGame, Godot 4, and plain .NET — the engine constraints
below are hard requirements, not preferences.

## Code Quality

- Use early returns; keep code simple and avoid clever abstractions.
- Idiomatic, modern C# (`LangVersion` 10, nullable enabled); warnings
  are errors — the build must be clean.
- Run `make lint` after significant new code (`dotnet format
  --verify-no-changes` plus build-time Roslyn analyzers).
- XML-document every public type and member in `src/` (enforced);
  non-doc comments only where context is really needed.
- Report failures as `GGScaleException` with structured status/code —
  never booleans/nulls that hide the cause; don't throw for expected
  control flow.

## SDK Constraints (engine compatibility)

- Zero runtime dependencies in `src/GGScale` (BCL only); JSON via the
  SDK's own minimal serializer — no System.Text.Json, no Newtonsoft.
- Core compiles against `netstandard2.1` (Unity profile); `net8.0` is
  secondary — no net8-only APIs.
- No engine (Unity/Godot/MonoGame) references in the core.
- AOT/IL2CPP-safe: no `Reflection.Emit`, runtime codegen, or
  reflection-driven serialization.
- Public async APIs take a `CancellationToken`; internal awaits use
  `.ConfigureAwait(false)`; never block on async.
- No static mutable state; a `Client` instance owns all state and is
  safe for concurrent use.
- No System.Threading.Channels (not in netstandard2.1) — use the
  internal `AsyncBoundedQueue<T>`.

## Testing

- Write failing tests before implementation (TDD); Arrange-Act-Assert;
  one assertion per test when practical.
- Name tests for behavior: `Method_does_x_when_y`.
- xUnit with `[Theory]`/`[InlineData]`; no mocking frameworks — use
  the hand-rolled `FakeTransport`.
- Unit tests (`tests/GGScale.Tests`) never touch the network;
  full-stack tests live in `tests/GGScale.IntegrationTests` and run
  via `make test-integration`.

## Project Notes

- `make check` is the CI gate (lint + build + unit tests). CI is
  Linux-only (runner cost).
- Wire contract source of truth: the generated
  [openapi.yaml](https://github.com/automoto/gg-scale/blob/main/openapi.yaml)
  in the gg-scale repo — don't keep a snapshot here. For gaps, consult
  the [server handlers](https://github.com/automoto/gg-scale/tree/main/internal/httpapi)
  and the [Go SDK](https://github.com/automoto/ggscale-go) (validated
  reference implementation).
- Wire JSON is snake_case; C# properties are PascalCase; the mapping
  lives in the hand-written converters — never rename a wire field.
- Failure classes live on `GGScaleException.Kind`; deterministic time
  goes through the internal `IGGClock` — never call
  `Task.Delay`/`DateTimeOffset.UtcNow` directly in retry, refresh, or
  reconnect logic.
