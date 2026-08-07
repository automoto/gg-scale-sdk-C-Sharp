# gg-scale C# SDK — MVP implementation plan

This is the working plan for a coding agent implementing the C# SDK.
Work milestone by milestone, strictly TDD (failing tests first — see
CLAUDE.md), and check items off here as they land. Keep `make check`
green after every task.

## Goal

A solid, engine-agnostic C# client for the ggscale `/v1` API with the
same surface and semantics as the validated Go SDK
(`~/code/ggscale-go`). It must work today in Unity (2021.3+), MonoGame,
and plain .NET, and in Godot 4 — with no per-engine forks of the core.
Inspiration for shape and packaging: heroiclabs/nakama-unity — but
unlike Nakama's, this SDK treats Unity as one consumer among several,
not the primary target.

## Non-goals (MVP)

- WebGL (no ClientWebSocket there; needs a JS socket adapter — later).
- Console platform certification.
- Control panel/admin APIs (`/v1` game surface only, like the Go SDK).
- Code generation from the OpenAPI spec. The spec
  (`docs/openapi.yaml`) is the route/status catalog, but several
  response schemas are untyped in it; the appendix below and the Go
  SDK are the payload truth. DTOs are written by hand.

## Architecture decisions (settled — do not relitigate)

1. **Targets**: `src/GGScale` multi-targets `netstandard2.1;net8.0`.
   netstandard2.1 is the Unity profile and the compatibility floor;
   never use BCL APIs beyond it in `src/`.
2. **Zero runtime dependencies**: no NuGet references in the core. No
   System.Text.Json (Unity doesn't ship it), no Newtonsoft. JSON is a
   small hand-written tokenizer/writer in `GGScale.Json` (see M1) —
   reflection-free, IL2CPP/AOT-safe.
3. **Transport abstraction**: `ITransport { Task<Response> SendAsync(Request, CancellationToken) }`
   mirroring the Go SDK's `Transport`. Default implementation
   `HttpTransport` wraps `HttpClient` (available on netstandard2.1).
   Engines that need `UnityWebRequest` can plug their own transport
   without touching the core.
4. **Errors**: one exception type, `GGScaleException`, carrying
   `Status` (int), `Code` (string), `Message`, `RetryAfter`
   (TimeSpan?), `ConflictVersion` (long?), plus convenience getters
   (`IsUnauthorized`, `IsForbidden`, `IsNotFound`, `IsConflict`,
   `IsRateLimited`, `IsBadRequest`) mirroring the Go sentinels.
5. **Async model**: Task-based, `CancellationToken` on every public
   API, `.ConfigureAwait(false)` on every internal await, no
   sync-over-async anywhere. No engine main-thread assumptions: the
   SDK completes tasks on the thread pool; callers marshal to their
   engine's main thread themselves (document this prominently).
6. **Client shape**: `GGScaleClient` (entry point) owning the session
   + auto-refresh, with service properties mirroring the Go SDK:
   `Auth`, `Storage`, `Leaderboards`, `Profile`, `Matchmaker`,
   `Relay`, `Fleets`, `Friends`, `GameSessions`, `Invites`,
   `Presence`, `Account`, `Server`.
7. **Sessions**: `Session { AccessToken, RefreshToken, PlayerId, ExpiresAt }`.
   Proactive refresh when within 30 s of expiry; on a 401, refresh
   once and retry once (copy the Go `callProtected` logic including
   its locking semantics). `ISessionStore` abstraction for
   persistence; `FileSessionStore` default (Unity consumers point it
   at `Application.persistentDataPath`).
8. **Paths**: no trailing slashes (`/v1/friends`, `/v1/game-session`),
   matching chi's route matching and the Go SDK.
9. **Naming**: wire snake_case ↔ C# PascalCase, mapped explicitly in
   the hand-written converters. Never "fix" a wire name.

## References

- `docs/openapi.yaml` — spec snapshot (copied 2026-07-06 from
  `~/code/ggscale/openapi.yaml`; regenerate there with `make openapi`).
- **Appendix A below** — payload shapes the spec leaves untyped.
- `~/code/ggscale-go` — reference implementation; its unit tests
  define the request-building behavior and its integration suite has
  passed against a live server. When in doubt, do what it does.
- `~/code/ggscale/internal/httpapi/` — server handlers (ground truth).
- https://github.com/heroiclabs/nakama-unity — packaging/ergonomics
  inspiration only; we do not copy its API.

---

## Milestones

### M0 — Repo scaffolding ✅

- [x] Solution + `src/GGScale` (netstandard2.1;net8.0) + xUnit test
      projects (unit, integration)
- [x] `Directory.Build.props`: nullable, warnings-as-errors,
      analyzers (`latest-recommended`), `EnforceCodeStyleInBuild`,
      CA2007 as error; test overrides in `tests/Directory.Build.props`
- [x] `.editorconfig`, `.gitignore`, `Makefile`
      (`check`/`build`/`test`/`lint`/`format`/`test-integration`)
- [x] CLAUDE.md / AGENTS.md
- [x] Integration compose stack + seed.sql + runner script
      (port 18081, compose project `ggscale-cs-it`)
- [x] `make check` green

### M1 — Core plumbing: JSON, transport, errors ✅

- [x] `GGScale.Json`: minimal JSON layer — `JsonValue` object model,
      hand-written parser (depth cap 128, strict numbers, surrogate
      escapes) and compact writer; numbers keep raw text so int64 ids
      never lose precision; `Opt*` accessors for DTO mapping.
- [x] `GGRequest` (method, path, query, body, ApiKey, SessionToken,
      IfMatch) and `ITransport`.
- [x] `GGScaleException` with Is* helpers + Go-parity error mapping
      (error/message/retry_after_seconds/version/current_version JSON
      envelope, text fallback, Retry-After header wins).
- [x] `HttpTransport` over `HttpClient` (owned or injected), tested
      via a stub `HttpMessageHandler` — no live server.
- [x] `FakeTransport` test double.
- Note: CA1510-13 and CA1720 disabled repo-wide (netstandard2.1
  multi-target / JSON-kind naming); private consts are PascalCase.

### M2 — Client, sessions, auth ✅

- [x] `Session`, `ISessionStore`, `FileSessionStore` (0600 on unix
      via net8 File.SetUnixFileMode; refresh-token-less sessions load
      as null).
- [x] `GGScaleClient` (+`GGScaleClientOptions`), IDisposable, all
      service properties wired; InternalsVisibleTo for test projects.
- [x] Protected-call pipeline: proactive 30 s refresh under a
      SemaphoreSlim with double-checked window, 401 → refresh once →
      retry once, original 401 surfaced when refresh fails.
      Concurrency test proves one refresh per expiry boundary.
- [x] Authenticators: AnonymousAuth (optional store),
      EmailPasswordAuth, CustomTokenAuth, OfflineAuth.
- [x] `AuthService`: Signup / Verify(email, code) / Refresh / Logout.

### M3 — Storage, Profile, Leaderboards ✅

- [x] `StorageService`: Get/Put (OCC via ifMatchVersion → IsConflict
      + ConflictVersion)/Delete/List (limit/cursor/key_prefix).
- [x] `ProfileService`: Get, Update(ProfilePatch{Email?, Xuid?});
      DTO is `PlayerProfile`.
- [x] `LeaderboardsService`: Submit, SubmitFor (server-tier), Top,
      AroundMe (self_rank -1 handling).

### M4 — Social: Friends, Presence, Invites, Account ✅

- [x] `FriendsService`: List/Request(returns status)/Accept/Reject/
      Remove/Block/Unblock/RemoteAddrs. DTO is `FriendInfo`
      (CA1716: "Friend" is a VB keyword).
- [x] `PresenceService.SetAsync(status, sessionId?)`.
- [x] `InvitesService`: Create → invite id, List, Delete.
- [x] `AccountService`: RemoteAddrs / SetRemoteAddrs (shared
      `RemoteAddr` type; scope stripped on writes).

### M5 — GameSessions, Matchmaker, Fleets, Relay, Server-tier ✅

- [x] `GameSessionsService`: Create/Get/Resolve/Join/Heartbeat
      (null qos → `{}`)/Leave.
- [x] `MatchmakerService`: CreateTicket/GetTicket/CancelTicket.
      **GA sync (2026-07-22):** the full request/result contract — modes
      (`MatchMode`), roster `RosterEntry` with `users[].attributes`,
      `host_player_id`, `failure_reason`; `WaitForMatchAsync` (push +
      poll recovery, unified `MatchResult`) replacing the `match_ready`
      loop with `matchmaker_matched`; `ConnectP2PAsync` (waits, fetches
      match-scoped relay creds, joins game_session); 409
      `ticket_already_active` surfaced via `IsTicketAlreadyActive` /
      `ActiveTicketId` (transport now parses Huma problem-details).
- [x] `FleetsService`: SendHeartbeat (client-side required-field
      guard), ListServers. DTOs `FleetHeartbeat`/`GameServerInfo`.
- [x] `RelayService.GetCredentialsAsync` → `RelayCredentials`; a
      `GetCredentialsAsync(matchId)` overload scopes creds to a match.
- [x] `ServerService`: VerifySession (empty-token guard before any
      network call), PlayerRemoteAddrs.

### M6 — Realtime WebSocket ✅

- [x] `ISocketAdapter` + default `WebSocketAdapter`
      (ClientWebSocket, netstandard2.1-safe).
- [x] `RealtimeClient` via `client.DialRealtimeAsync()`: ws/wss URL
      derived from BaseUrl; ReadMessageAsync → `RealtimeMessage`
      (null once closed). Note: unlike REST calls, a 401 on the WS
      upgrade is not auto-retried (ClientWebSocket hides the status
      on netstandard2.1) — the proactive refresh still runs first.
- [x] `MatchmakerService.RequestMatchAsync`: dial-before-ticket,
      skip non-match_ready envelopes, best-effort cancel on close/
      cancellation, socket closed on all paths.
- Note: unit tests use a scripted `FakeSocketAdapter` (HttpListener
  WebSockets are Windows-only); the real adapter is covered by the
  integration suite's dial test.

### M7 — Integration suite ✅

- [x] Ported the Go integration suite: 11 tests across auth (refresh
      rotation), profile xuid, storage OCC, leaderboards (publishable
      403 / SubmitFor / Top / AroundMe), presence, two-player
      game-session lifecycle, invites, linked-account 403 gates,
      server verify (valid / garbage / key tier), realtime dial.
- [x] Two shared anonymous players via an xUnit collection fixture
      (`ItFixture`); the collection serializes tests, staying under
      the per-IP auth limiter.
- [x] Keys/base-url via env with seed.sql defaults.
- Validated 2026-07-06: 11/11 green against a locally built server
  image (`GGSCALE_IT_PULL=never`); Docker Hub `:latest` was still the
  stale 2026-05-04 image at the time.

### M8 — Engine packaging & samples

- [ ] Unity: UPM package layout (`package.json`, asmdef, source-drop
      of `src/GGScale` or DLL) under `unity/`; document IL2CPP
      readiness (`link.xml` not needed — no reflection); smoke-test
      in a Unity 2021.3 LTS project (manual step, document it).
- [ ] MonoGame sample under `examples/monogame/` (plain NuGet/project
      reference; quickstart: anonymous login → leaderboard top).
- [ ] Godot 4 sample under `examples/godot/` (later; note
      main-thread marshaling via `CallDeferred`).
- Acceptance: documented consumption path per engine in README.

### M9 — Docs & release polish

- [ ] README: quickstart, service table, error-handling idiom,
      session persistence, engine matrix, development targets.
- [ ] XML docs complete (`make build` enforces); doc comments explain
      auth tiers (publishable vs secret) on every server-tier API.
- [ ] NuGet packaging metadata + `make pack`; version 0.2.0.
- [x] Wire-contract drift check: re-diff DTOs against
      `docs/openapi.yaml` regenerated from the server repo
      (done 2026-08-07 as part of M10).

### M10 — v0.9.3 GA parity + SDK best-practices compliance ✅

Driven by the SDK client guide
(`~/notes/resources/ggscale-docs/monthly-notes/august-docs/sdk-client-best-practices.md`)
and the server v0.9.3 GA release. SDK version bumped to 0.2.0.

- [x] Transport foundation: `ITransport` now returns a `GGResponse`
      envelope (status, value, ETag, X-Request-Id). `GGScaleException`
      gained `Kind` (`GGFailureKind`: connection/timeout/decode/
      handshake vs HTTP), problem-details `ProblemType`/`Title`/
      `Instance`, `RequestId`, bounded `RawBody`. Configurable
      user agent, per-attempt timeout, response size cap (4 MiB);
      Retry-After parsed in both delta-seconds and HTTP-date forms.
      **Breaking**: custom `ITransport`/`ISocketAdapter`
      implementations must adapt.
- [x] Retry engine (`RetryingTransport`): 3 total attempts by default,
      full jitter `random(0, min(10s, 250ms × 2^n))`, Retry-After as a
      minimum wait, overall deadline (default 100 s) across attempts
      and backoff, GET/HEAD/PUT/DELETE only (POST/PATCH never;
      `GGRequest.Idempotent` opt-in), 401/403 never retried, refresh
      never auto-retried. One stable client-generated `X-Request-Id`
      per logical call.
- [x] Telemetry: `IGGScaleLogger` hook (silent by default) — one
      completion record per call, per-retry records, WS lifecycle
      events; records carry route templates, never URLs/tokens/bodies.
- [x] `ConfigService` (`GET /v1/config`) with ETag/If-None-Match and
      304 → `NotModified`.
- [x] Pagination iterators: `Storage.ListAllAsync`,
      `Friends.ListAllAsync` (`IAsyncEnumerable`).
- [x] v0.9.3 endpoint parity: Steam sign-in (`SteamAuth`), account
      linking, change-password, self-disable, password reset, resend
      verification; `PlayersService` (directory + friend codes);
      profile display name + `RegenerateFriendCodeAsync`; leaderboard
      discovery/friends/periods + submit metadata; public session
      browser + P2P signaling; server-tier score submit + player
      storage; relay `stun_urls`; `expires_at`/`display_name` DTO
      fields; int64-safe ids throughout; null-array tolerance.
- [x] Managed realtime client: continuous read loop (server pings
      answered), bounded drop-oldest queue (`AsyncBoundedQueue`,
      hand-rolled — System.Threading.Channels is not in the
      netstandard2.1 BCL), 1 MiB inbound cap, close-code-based
      reconnect (abnormal/1001/1006/1011–1013; terminal on
      1000/4xxx/auth), first retry random 0–5 s then capped full
      jitter, handshake Retry-After honored, backoff reset only after
      a stable connection, session refresh before every reconnect,
      `StateChanged` resync signal (`Connected` + `IsReconnect`).
      On net8 the handshake status enriches errors
      (`CollectHttpResponseDetails`); netstandard2.1 cannot observe
      it (status 0 = retryable-unknown).
- [x] `docs/openapi.yaml` replaced with the server v0.9.3 spec
      (70 operations, clean Huma operationIds); integration image
      pinned to `buildwrangler/ggscale:v0.9.3`.
- [x] Conformance/unit coverage: retry matrix, both Retry-After
      forms, deadline/cancel during backoff, ETag/304, pagination
      iterators, redaction, WS reconnect/overflow/graceful close,
      unknown fields/events. Deterministic via `FakeClock` +
      scripted `FakeTransport`/`FakeSocketAdapter`.
- [x] Review hardening (2026-08-07): overall deadline now cancels
      in-flight attempts; ReconnectTimeout bounds the WS connect
      itself; a failed pre-dial session refresh is terminal for
      reconnect (never replays the rotating token); CloseAsync/
      DisposeAsync idempotent; exactly one completion record per
      logical call across the 401 refresh-and-retry; route-template
      Operation set on every request builder; TLS/certificate
      failures classified `certificate_error` and never retried;
      DisableAsync password optional (session-only for anonymous
      players); `IsValidationError` (422) helper.
- [x] Integration suite extension for the new endpoints: config ETag
      304 round-trip, friend-code regenerate/resolve, players batch
      resolve, leaderboard discovery/periods + server-tier submit
      with metadata, public session browser + signals round-trip,
      server-tier player storage CRUD + OCC, password-reset 202
      smoke. Validated 2026-08-07: 18/18 green against
      `buildwrangler/ggscale:v0.9.3` (seed.sql updated for the
      integer tenant tier; presence validation asserts 422 per the
      Huma migration).

---

## Appendix A — wire contract details the spec lacks

The OpenAPI snapshot types most requests, but several responses appear
as bare `object`. These shapes come from reading the server handlers
(`~/code/ggscale/internal/httpapi/*.go`) and are validated by the Go
SDK's integration suite. All timestamps are RFC 3339 strings.

### §1 Auth

- Session response (login, refresh, custom-token):
  `{"access_token": s, "refresh_token": s, "player_id": n, "expires_at": ts}`
- Anonymous (`POST /v1/auth/anonymous`, no body): same + `"external_id": s`.
- Signup: `{"email": s, "password": s}` → **202**, empty body.
- Verify: `{"email": s, "code": s}` → `{"player_id": n, "verified": true}`.
- Logout: `{"refresh_token": s}` → **204**.
- Refresh tokens rotate: the old one is revoked on every refresh.
- Auth routes carry a per-IP rate limiter (≈10/min, burst 10) →
  integration tests must reuse players.

### §2 Storage

- Object: `{"key": s, "value": <raw json>, "version": n, "updated_at": ts}`
- List: `{"items": [object], "next_cursor": s}` — query
  `limit`/`cursor`/`key_prefix`; `next_cursor` empty on last page.
- Put with `If-Match: <version>` header; mismatch → **412**.
- Delete → **204**; Get after delete → **404**.

### §3 Leaderboards

- Submit `{"score": n, "metadata": obj?}` → **201** empty. `score` is
  required (422 when absent). Publishable keys get **403** unless the
  board sets `client_submissions`.
- Entry: `{"player_id": n, "score": n, "rank": n,
  "display_name": s?, "metadata": obj?}` — rank is 0-based.
- Around-me: `{"entries": [entry], "self_rank": n}` — `self_rank` is
  −1 when the caller has no score.
- Discovery (`GET /v1/leaderboards`, no paging):
  `{"leaderboards": [{"id", "name", "sort_order", "score_operator",
  "client_submissions", "score_min"?, "score_max"?, "attempt_cap"?,
  "reset_schedule", "current_period", "period_started_at"?,
  "next_reset_at"?, "metadata"?}]}`.
- Friends view: `GET /v1/leaderboards/{id}/friends` → entries ranked
  0-based within caller + accepted friends.
- Periods: `GET /v1/leaderboards/{id}/periods?limit=&cursor=` →
  `{"current_period", "reset_schedule", "periods": [{"period",
  "started_at", "ended_at"}], "next_cursor"}`;
  `GET .../periods/{period}/top?limit=` → entries (404 for a period
  the board has not reached).
- Server tier: `POST /v1/server/leaderboards/{id}/scores`
  `{"player_id": n, "score": n, "metadata": obj?}` → **201**.

### §4 Friends / Presence / Invites / Remote addresses

- Friends list (`GET /v1/friends?status=&limit=&cursor=`; `status` ∈
  pending|accepted|rejected|blocked, default accepted — documented in
  the v0.9.3 spec):
  `{"items": [friend], "next_cursor": s}` where friend =
  `{"id": n, "account_id": uuid, "player_id": n?, "status": s,
    "email": s?, "display_name": s?,
    "presence": {"status": s, "session_id": s|null}?,
    "created_at": ts, "updated_at": ts}`
  `presence` only on accepted friendships; `player_id` only when the
  friend has a player in the caller's project.
- Request/accept/reject/block/unblock (POST, no body) →
  `{"status": s}`; delete → **204**. Accept/reject of a non-pending
  edge → **409**. A blocked or unknown target → **404** (deliberately
  indistinguishable). All friend APIs → **403** with message
  "link a gg-scale account to use friends" for anonymous players.
- Presence: `PUT /v1/presence` `{"status": s(1–32 chars),
  "session_id": s|null}` → `{"ok": true}`.
- Invite create: `{"to_email": s, "session_id": s}` → **201**
  `{"invite_id": n}`. Recipient must be an accepted friend (**403**),
  sender must be in the session (**403**), session must be open
  (**409**). List: `{"invites": [{"invite_id": n, "from_email": s?,
  "from_xuid": s?, "session_id": s, "join_code": s, "expires_at": ts}]}`.
  Delete → **204** (sender cancels or recipient dismisses).
- Remote addresses payload (GET/PUT `/v1/account/remote-addrs`,
  GET `/v1/friends/{player_id}/remote-addrs`,
  GET `/v1/server/players/{player_id}/remote-addrs`):
  `{"addresses": [{"type": "ip_lan"|"ip_public"|"dns"|"iroh",
  "scope": s (server-derived, ignored on PUT), "address": s}]}` —
  max 4 entries, one per type.

### §5 Game sessions & matchmaker

- Create: `{"title_id": s, "public_addr": {"ip": s, "port": n(1–65535)},
  "props": <raw json>, "max_players": n(≤64, default 2), "private": b}`
  → **201** session. Project cap reached → **429**.
- Session: `{"session_id": s, "join_code": s, "state": "open"|"ended",
  "expires_at": ts, "peers": [{"player_id": n, "xuid": s?,
  "display_name": s?, "addr": {"ip": s, "port": n}}]}` — the per-peer
  `relay` field was removed in v0.9.3; credentials come from
  `POST /v1/relay/credentials` (response now includes `stun_urls`).
- Public browser: `GET /v1/game-sessions?title_id=&limit=&cursor=` →
  `{"items": [{"session_id", "title_id"?, "props", "player_count",
  "max_players", "host_player_id", "host_display_name"?,
  "created_at"}], "next_cursor"}` — open, public, non-full sessions
  with a recent heartbeat.
- P2P signals: `POST /v1/game-session/{id}/signals`
  `{"to_player_id": n, "negotiation_id": s, "kind":
  offer|answer|restart_offer|restart_answer, "payload": s(≤64 KiB)}`
  → **201** `{"id": n}`; 30/min per sender per session → **429**.
  `GET .../signals?after_id=` → `{"signals": [entry]}` addressed to
  the caller, id-ascending.
- Resolve: `GET /v1/game-session?joinCode=X` → `{"session_id": s}`
  (note camelCase query param). Private sessions resolve only for
  host/members/invitees; others → **404**.
- Join: `{"public_addr": addr}` → session. Full → **409**;
  ended/expired → **410**.
- Heartbeat: `{"qos": <raw json>}` (omit/null preserves stored value —
  serialize null as `{}`) → `{"ok": true, "peers": [peer]}` with
  stale peers pruned. Non-member → **404**.
- Leave → **204**. Host leaving sets state "ended" and clears roster.
- Matchmaker ticket: request `{"fleet": s, "region": s, "game_mode": s,
  "attributes": obj?}` → **201** ticket `{"id": n, "status": s,
  "region": s, "game_mode": s, "attributes": obj?, "match_address": s,
  "protocol_hint": s?, "created_at": ts, "matched_at": ts?}`.

### §6 Realtime WebSocket (`GET /v1/ws`)

- Envelope: `{"type": s, "payload": <raw json>}`.
- `matchmaker_matched` (replaced `match_ready` in v0.9.3):
  `{"ticket_id": n, "match_id": s, "mode": s, "address": s?,
  "protocol_hint": s?, "session_id": s?, "join_code": s?,
  "host_player_id": n?, "users": [{"player_id": n, "region": s,
  "string_properties"?, "numeric_properties"?, "attributes"?}]}`.
- `game_invite`: `{"invite_id": n, "session_id": s, "join_code": s}`.
- `presence`: `{"player_id": n, "status": s, "session_id": s|null}`
  (fanned out to accepted friends only).
- Server pings every 30 s; inbound messages are capped at 1 MiB and
  currently ignored (push-only channel). Tenant-cap 503 rejections
  carry `Retry-After: 5`.
- Dial BEFORE creating a matchmaking ticket or the push can be lost.

### §7 Server-tier (`/v1/server/*`, secret key required — 403 otherwise)

- Verify: `{"session_token": s}` →
  `{"player_id": n, "external_id": s, "email": s?}`.
  Every failure mode (bad token, wrong tenant, disabled player,
  malformed body) → the same opaque **401**
  `{"error": "invalid session"}` — never distinguish.
- Player storage (v0.9.3):
  `GET/PUT /v1/server/players/{player_id}/storage/objects/{key}` and
  `GET .../storage/objects?key_prefix=&limit=&cursor=` — same object
  and page shapes as §2, including `If-Match` OCC on PUT.

### §8 Errors in general

- Most error bodies are `text/plain` message + status; some (429s)
  are JSON. Copy the tolerant parse order from
  `ggscale-go/transport_stdnet.go`: try JSON `{code, message}`, fall
  back to raw text; read `Retry-After` for 429; capture the current
  version on 412 where the server provides it.
- Auth: `Authorization: Bearer <api key>` always; `X-Session-Token`
  on player routes. 401 = bad/missing credential, 403 = key type or
  scope or linked-account requirement.
- Since the Huma migration (v0.9.2+): errors are
  `application/problem+json` (`type`, `title`, `status`, `detail`,
  `instance`, `errors[]`), and request-validation failures answer
  **422** (`IsValidationError`), not 400.
