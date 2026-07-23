# gg-scale C# SDK — project instructions

This repo is the official C# client for the ggscale API. It must run
unmodified in Unity (2021.3+), MonoGame, Godot 4, and plain .NET — the
engine constraints below are hard requirements, not preferences.

## Code Quality

- Use early returns to reduce nesting.
- Write idiomatic, modern C# (`LangVersion` 10, nullable reference types
  enabled). Warnings are errors; the build must be clean.
- Keep code simple and concise. Avoid clever abstractions unless they
  remove real complexity. Prefer plain classes and interfaces over
  frameworks and metaprogramming.
- Code must pass `make lint` (`dotnet format --verify-no-changes` plus
  the Roslyn analyzers enforced at build time). Run `make lint` after
  creating significant new code.
- Every public type and member carries an XML doc comment (the build
  enforces this for `src/`). Document behavior and error cases, not
  implementation.
- Add non-doc comments only where extra context is really needed.
- Report failures as exceptions carrying structured data
  (`GGScaleException` with status/code), never as booleans or nulls
  that hide the cause. Do not throw for expected control flow.

## SDK constraints (engine compatibility)

- **Zero runtime dependencies.** `src/GGScale` references nothing
  beyond the base class library. No NuGet packages, no
  System.Text.Json (not shipped by Unity), no Newtonsoft. JSON is
  handled by the SDK's own minimal serializer.
- **The core must compile against `netstandard2.1`** (the Unity
  profile). Don't use BCL APIs newer than that in `src/`; `net8.0` is
  a secondary target, not a license to use net8-only APIs.
- **No engine references in the core.** Unity/Godot/MonoGame
  adaptations live in separate integration folders/packages, never in
  `src/GGScale`.
- **AOT/IL2CPP-safe.** No `Reflection.Emit`, no runtime code
  generation, no reflection-driven serialization. Anything that byte-
  patches or emits at runtime breaks IL2CPP and console targets.
- **Async discipline.** All I/O is `Task`-based `async`; every public
  async API takes a `CancellationToken`; every internal `await` uses
  `.ConfigureAwait(false)` (CA2007 is an error). Never block on async
  (`.Result`, `.Wait()`).
- **No static mutable state.** A `Client` instance owns all state and
  is safe for concurrent use.

## Testing Conventions

### TDD Workflow

- Always write failing tests BEFORE implementation.
- Use the AAA pattern: Arrange-Act-Assert.
- One assertion per test when practical.
- Test names describe behavior: `Method_does_x_when_y`
  (CA1707 is disabled for test projects to allow underscores).
- xUnit; `[Theory]`/`[InlineData]` for table-driven cases.
- No mocking frameworks: use the hand-rolled `FakeTransport` (mirrors
  the Go SDK's test approach) to capture requests and stage responses.
- Unit tests (`tests/GGScale.Tests`) must not require a network or a
  running server. Full-stack tests live in
  `tests/GGScale.IntegrationTests` and run via `make test-integration`.

## Project Specific Instructions

- Build/lint/test through the Makefile: `make check` is the gate CI
  runs (lint + build + unit tests).
- CI runner cost constraint: Linux only in CI; macOS/Windows runners
  cost money.
- After each task is completed, update the planning document
  (`docs/temp/mvp.md`) to reflect finished milestone items.
- Wire-contract source of truth, in order: `docs/openapi.yaml`
  (snapshot of the generated spec), the wire appendix in
  `docs/temp/mvp.md`, the server handlers in
  `~/code/ggscale/internal/httpapi/`, and the Go SDK reference
  implementation at `~/code/ggscale-go` (its integration suite has
  been validated against a live server).
- JSON field names on the wire are snake_case; C# properties are
  PascalCase. The mapping lives in the hand-written converters —
  never rename a wire field to "clean it up".
