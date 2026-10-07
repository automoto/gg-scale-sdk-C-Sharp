# Changelog

All notable changes to the GGScale C# SDK are documented here. The project
is pre-1.0; minor versions may contain breaking changes until v1.0.0.

## [0.3.0]

Synchronizes the SDK with ggscale server **v0.9.71**: player data deletion,
parties, realtime events, WebSocket tickets, and the same HTTP and realtime
defaults as `ggscale-go` v0.7.0. All 92 operations in the server's
`openapi.yaml` have a wrapper.

### Breaking

- HTTP retries: only requests that are safe to repeat are retried: `GET`,
  `HEAD`, writes with an `Idempotency-Key` header, and writes with
  `GGRequest.Idempotent = true`. `PUT` and `DELETE` are no longer retried by
  default, because a lost response does not show whether the write ran. A 503
  `party_enqueue_disabled` is never retried, because it is a server setting.
  Any method, writes too, is retried when the failure proves that the request
  never reached the server: a temporary DNS failure, or a connection that
  could not open (refused, no route). Such a failure now has `Code`
  `connect_failed` (it was `connection_error`). A host name that does not
  exist has `Code` `host_not_found` and is not retried for any method,
  because a retry cannot help. A connect timeout, a reset after the
  connection opened, and TLS or certificate failures are not retried for
  writes. The check reads the socket error that `HttpClient` puts directly in
  its `HttpRequestException`; a custom `ITransport` or a platform that does
  not report it that way gets no write retry.
- Realtime reconnect stops after 5 attempts for one outage
  (`RealtimeOptions.MaxReconnectAttempts`; 0 means no limit). Before, it tried
  until `CloseAsync`. Reconnect stays on by default; set
  `RealtimeOptions.AutoReconnect = false` to turn it off.
- When the realtime connection drops and the client stops (a terminal close
  code, reconnect off, or the attempts ran out), the `Closed` state change now
  has a `GGScaleException` with the new kind `GGFailureKind.ConnectionClosed`
  and `CloseCode` in `Error`. Before, `Error` was null. A failed connect
  stays `GGFailureKind.Handshake`. A close by the caller still has no error.
  Code that switches over `GGFailureKind` gets a new value.
- `Fleets.SendHeartbeatAsync` is obsolete; use `Server.FleetHeartbeatAsync`.
  The heartbeat needs a secret key, so it is on the server client now. The old
  method still works and calls the new one. With warnings as errors, a call to
  the old method stops the build.

### Added

- `GGScaleClient.Parties` with all 19 party operations: `CreateAsync`,
  `CurrentAsync`, `GetAsync`, `UpdateAsync`, `DisbandAsync`, `HeartbeatAsync`,
  `JoinByCodeAsync`, `LeaveAsync`, `SetReadyAsync`, `KickAsync`,
  `CreateCodeAsync`, `RevokeCodeAsync`, `InviteFriendAsync`,
  `ListInvitesAsync`, `AcceptInviteAsync`, `DeclineInviteAsync`, `QueueAsync`,
  `CancelQueueAsync`, `RematchAsync`. Writes take the party version.
  `QueueAsync` and `RematchAsync` send an `Idempotency-Key` (given, or made by
  the SDK), so the SDK retries them like reads. Types: `Party`, `PartyMember`,
  `PartySettings`, `PartyCode`, `PartyInvite`, `PartyProperties`,
  `PartyState`.
- `Parties.WatchAsync` (an `IAsyncEnumerable<PartyEvent>`) reports newer party
  versions, party invites, the party's match, and removal on one realtime
  connection, and sends the heartbeat every 10 seconds. An invites-only watch
  (party id 0) throws the connect error when the connection cannot open; a
  party watch logs it and continues on the heartbeat.
  `Parties.WaitForMatchAsync` returns the party's match, or throws
  `MatchFailedException` or `NotPartyMemberException`.
- `GGScaleException.IsStaleVersion` (409), `IsPartyEnqueueDisabled` (503),
  `IsCodeCooldown` (429; read `RetryAfter`) and `IsDeleteRequestedByTeam`
  (403 with that slug only; a plain 403 does not match).
- Realtime event names in `RealtimeEvents` and payload types
  `PresenceEvent`, `GameInviteEvent`, `PartyChangedEvent`, `PartyInviteEvent`
  (each with `FromPayload`).
- `Realtime.CreateTicketAsync` (`POST /v1/ws/ticket`). A socket adapter that
  implements the new `ITicketSocketAdapter` gets a new one-time ticket URL for
  each connect, reconnects included, and sends no headers. The default
  `WebSocketAdapter` still sends headers. The SDK ships no WebGL adapter.
- `Auth.RequestDeleteAsync` schedules permanent deletion of the calling
  player's data in the current project (`POST /v1/auth/delete`), returns
  `PendingDelete`, and clears the local session. `Auth.CancelDeleteAsync`
  cancels it with email and password (`POST /v1/auth/delete/cancel`) and sends
  no session token.
- `Health.GetAsync` (`GET /v1/healthz`).
- `GameSessions.ListAllAsync`, `Leaderboards.ListAllPeriodsAsync` and
  `Server.ListAllStorageAsync` read every page.
- `Server.FleetHeartbeatAsync`.
- `Ticket.EntryId`, `Ticket.PartyId`, `Ticket.Query`,
  `Ticket.StringProperties`, `Ticket.NumericProperties`,
  `RosterEntry.QueueEntryId` and `RosterEntry.PartyId`.
- `GGRequest.Headers` for extra request headers.
- `RealtimeOptions.MaxReconnectAttempts` and `GGScaleException.CloseCode`.

### Changed

- `openapi.yaml` in the gg-scale repository is the only contract. The new
  `ContractTests` reads the spec from `GGSCALE_SPEC` and checks that each
  operation occurs as `Operation = "METHOD path"` in `src/GGScale`, that each
  secret-key operation is in `ServerService.cs`, and that no other operation
  is only there. `make openapi-check` downloads the spec for `SPEC_REF`
  (`v0.9.71`), and CI runs it. Without `GGSCALE_SPEC` the test does nothing,
  so `make test` stays offline. YamlDotNet is a test-only dependency; the core
  still has no dependencies.
- A test checks that `SdkVersion.Value` and the package version are the same.
- Integration tests default to `ghcr.io/automoto/gg-scale:v0.9.71`; override
  with `GGSCALE_IMAGE`. The seed gives the publishable key the `matchmaker`
  and `p2p_relay` scopes, as in the Go SDK. New tests cover the party flow, a
  version conflict, kick, leave, disband, the party-code cooldown, one-time
  WebSocket tickets, and the delete flow.
- Doc comments: `FriendInfo.Email` is set only for accepted friends;
  `Players.ResolveFriendCodeAsync` lists all its 404 cases.
- README: services table, "Which key?", "Realtime events", "Parties" and
  "Defaults" sections.

## [0.2.0]

Synchronizes the SDK with ggscale server v0.9.3. Earlier versions have no
changelog entries.
