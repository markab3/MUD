# Session service on frontends.sock (card P7-1)

The Session service is how a front end in its own process (the telnet host of phase 7) reaches core. It is served by core on
`frontends.sock` only (D22) and is the wire form of `ICoreApi` (`MUD.CoreApi`). The contract is `contracts/mud/frontend.proto`.
Decision D66 (G21): the front end opens one server stream for output and session events and makes separate unary calls for
everything else. No two-way stream.

## Calls

| Call | Does |
|---|---|
| `Connect(FrontEndHello)` | First call. Presents the token, the front end name (for example `telnet`) and the contract version. Returns a `connection_id` that every later call carries. |
| `Events(SessionEventsRequest)` | Server stream, one per connection, for all its sessions: `output` (channel + markup + flush), `set_echo`, `prompt`, `set_terminal`, `close`, `ended`. |
| `OpenSession` | Core opens a session and returns its id (opaque, random, D46). `remote_key` is the throttle key (D43). |
| `Authenticate`, `CreatePlayer` | Log the session in or create a character. Wait for the game thread, bounded. Result is an `AuthResult`; any explanation text was already sent to the session as `output`. |
| `Line` | One line typed in the session; enqueue only. |
| `CloseSession` | The front end closed the session (`client_closed`, `idle`, `flood`). |
| `IsThrottled`, `RecordLoginOutcome` | The brute-force throttle in core, one limit shared with the web (D43). |
| `GetOnlineNames` | The names for the pre-login `who`. |

Channel tags on `output`: `say`, `tell`, `room`, `combat`, `system`, `chat` (D44). An empty or unknown tag means `system`.

## Rules

- **Token (D66).** A file with mode 600, default `~/.config/mud/frontend-token`; override with the `FrontEnds:TokenFile` setting or the
  `MUD_FRONTEND_TOKEN_FILE` variable. Core creates it with a random token on first start; the front end only reads it. Constant-time
  comparison; never logged. It guards against accidents, not a hostile process of the same user (D19). A warning is logged when the
  file is readable by others.
- **Contract version (D24).** Strict equality at `Connect`; a mismatch is refused and both numbers are logged.
- **Ownership.** A session can only be used by the connection that opened it (`PermissionDenied` otherwise). An unknown or closed
  connection id answers `Unauthenticated`, which is what a front end sees after core restarted.
- **Output never blocks (D42).** Core's sink for a remote session only puts the event on the connection's bounded queue
  (`FrontEnds:OutboundQueueCapacity`, default 2000). If the queue is full the front end is cut off: its sessions are closed in core as
  if its connections dropped. Single events are not dropped, because that would leave a screen out of step with the game.
- **Lost front end.** When a front end's event stream ends, its sessions are closed in core (players are logged out, as when a telnet
  connection drops today). Link-dead behavior is an open item and is not decided here. A newer connection with the same front end name replaces
  the older one.
- **Lost core.** `MUD.CoreApi.Client.GrpcCoreApi` ends every open session with `SessionEndReason.WorldStopping` (the telnet sink then says
  "world is restarting", D29) and raises `ConnectionLost` once. It does not reconnect; that is card P7-2.

## Client

`MUD.CoreApi.Client` (references `MUD.CoreApi` and `MUD.Contracts` only) holds `GrpcCoreApi : ICoreApi`. `SubmitLine`, `CloseSession` and
`RecordLoginOutcome` enqueue and are sent in order by one sender task; the other calls wait, with a deadline, and fail soft
(`TryAgainLater`, false, empty list).

## Not done in this card / not verified

- The telnet process itself, reconnect with backoff, the systemd unit and deploy changes (P7-2); the cut-over (P7-3).
- The Admin service is still not guarded by the token (see ADMIN_API.md, open points); the token guards the Session service.
- No MongoDB in the sandbox: tests run over a fake `ICoreApi`; no live telnet session was played against a running core.
