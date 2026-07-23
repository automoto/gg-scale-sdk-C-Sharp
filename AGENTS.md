# Repository Instructions

## Code Quality

- Use early returns to reduce nesting.
- Write idiomatic, modern C# (nullable enabled) and keep the build
  warning-free — warnings are errors.
- Keep code simple and concise. Avoid clever abstractions unless they
  remove real complexity.
- Run `make lint` after significant new code; it runs `dotnet format
  --verify-no-changes` on top of build-time Roslyn analyzers.
- XML-document every public type and member in `src/` (enforced).
- Report failures as `GGScaleException` with structured status/code.

## SDK Constraints

- Zero runtime dependencies in `src/GGScale`; JSON via the SDK's own
  minimal serializer (no System.Text.Json, no Newtonsoft).
- Core compiles against `netstandard2.1` (Unity profile); `net8.0` is
  the secondary target.
- No engine (Unity/Godot/MonoGame) references in the core.
- AOT/IL2CPP-safe: no Reflection.Emit, no reflection-driven
  serialization.
- All public async APIs take a `CancellationToken`; internal awaits
  use `.ConfigureAwait(false)`; never block on async.

## Testing Conventions

- Write tests before implementation for new features.
- Use Arrange-Act-Assert structure; one assertion per test when
  practical.
- Name tests for behavior: `Method_does_x_when_y`.
- xUnit with `[Theory]`/`[InlineData]` for table-driven cases; no
  mocking frameworks — use the hand-rolled `FakeTransport`.
- Unit tests never touch the network; full-stack tests run via
  `make test-integration`.

## Project Notes

- CI cost constraint: Linux only in CI.
- After completing milestone work, update `docs/temp/mvp.md`.
- Wire contract: `docs/openapi.yaml` + the appendix in
  `docs/temp/mvp.md`; the Go SDK at `~/code/ggscale-go` is the
  validated reference implementation.
