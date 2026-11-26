# Website: Blazor WebAssembly with Fluent UI (design spec)

Status: **design, not built.** Card t_d73059a1 (docs only). Nothing in this file has been compiled or run; every
statement about the Blazor/Fluent packages is from the design, not from a build here. Items to verify at implementation
time are marked **[verify]** and collected in "Open questions".

Replaces the Razor Pages implementation of `MUD.Web` described in [WEB.md](WEB.md). The behaviour in WEB.md and
[WEB_EDITING.md](WEB_EDITING.md) stays the requirement; this file changes the delivery mechanism only.

Owner decisions this spec implements (not re-opened here):

1. Replace Razor Pages with Blazor WebAssembly using `Microsoft.FluentUI.AspNetCore.Components`.
2. Phone use is first class: mobile-first, touch targets of at least 44px, no horizontal page scroll at 360px width,
   navigation collapses to a drawer or bottom bar, forms and tables reflow, long pages usable one-handed, map supports
   touch pan and pinch zoom.
3. The browser never reaches core. The server host keeps `IAdminClient` and the session authentication; the WASM client
   calls a JSON API on the host (backend-for-frontend). `NoDatabaseAccessTests` still holds.
4. Preserve behaviour, policies, antiforgery, the plaintext-password caveat, audit-first admin calls and every
   user-visible message. Same URLs. Localhost bind. `scripts/deploy.sh` still produces one release. No deploy in this card.

## 1. Facts about the current site (inventory)

Established from the repo at `d5ed91a` (`hermes`).

### 1.1 Pages (`MUD.Web/Pages`)

| Route | Page model | Auth | Handlers / purpose | Lines (model) |
|---|---|---|---|---|
| `/` | `Index` | none | redirect to `/character` or `/login` | 10 |
| `/login` | `Login` | anonymous | GET; POST `WebAuthenticate`, sets cookie, generic/retired/unavailable errors, 500 ms delay on failure | 62 |
| `/logout` | `Logout` | none | GET redirects `/`; POST (antiforgery) `Logout` + delete cookie | 26 |
| `/access-denied` | `AccessDenied` | anonymous | 403 page | 15 |
| `/character` | `Character` | signed in | `Hello (<PlayerName>)!` | 16 |
| `/account/password` | `Password` | signed in | POST `ChangePassword`, per-IP `LoginThrottle` (5 failures / 5 min) + 500 ms delay | 85 |
| `/creator` | `Creator` | CreatorOrAdmin | create-room form (Key, Short, Long, Area, X/Y/Z, up to 4 exits, dangling-exit checkbox); GET `ListRooms` for area suggestions; POST `CreateRoom` via `RoomCreationService` | 98 |
| `/creator/map` | `CreatorMap` | CreatorOrAdmin | `ListRooms` (+ `ListTerrainColors` when a generated room is on the level); `RoomMapLayout` renders SVG; `?area=&z=` | 79 |
| `/creator/rooms/{key}` | `RoomEdit` | CreatorOrAdmin | GET `GetRoom`; Save (`UpdateRoom`), Move (`MoveRoom`), Delete (`DeleteRoom`), Regen (`RegenerateRooms`), Update (`UpdateRooms`); AddExit/RemoveExit/AddFeature/RemoveFeature/MoveFeatureUp/Down are no-write round trips | 463 |
| `/creator/players` | `PlayerList` | CreatorOrAdmin | `ListPlayers` (`q`, `sort`, `page`, 50 per page) | 71 |
| `/creator/players/{nameKey}` | `PlayerEdit` | CreatorOrAdmin | `GetPlayer`, `UpdatePlayer`; `PlayerRules` hides controls | 184 |
| `/creator/modules` | `Modules` | CreatorOrAdmin (buttons admin only) | `ListModules`; POST `Start`/`Stop`/`Restart` Module; `TempData["Notice"]` | 108 |
| `/creator/terrain` | `TerrainList` | CreatorOrAdmin | tabs + filters over fragments/features/phrases; POST Delete per row | 145 |
| `/creator/terrain/edit?kind=&id=` | `TerrainEdit` | CreatorOrAdmin | Get/Create/Update for one library entity; `TempData["Notice"]` | 194 (+56 `TerrainLibrary.cs`) |

Plus `Pages/Shared/_Layout.cshtml` (a bare `<nav>`; **no CSS, no stylesheet, no viewport-specific layout exists today**).
Total 14 pages, 1 layout, 5 antiforgery-protected POST forms on the room page alone.

### 1.2 Services (`MUD.Web/Services`)

| File | Role | Fate (section 6) |
|---|---|---|
| `AdminClient.cs` (371) | `IAdminClient`, `GrpcAdminClient`, `CoreRefusedException`, `CoreUnavailableException`, `CoreSocket` | stays in host, unchanged |
| `SessionAuthentication.cs` (111) | cookie `mud.session`, `SessionAuthenticationHandler` (asks core `GetSession` per request), `SessionAccountService` | stays in host; challenge/forbidden behaviour changes for `/api` |
| `LoginThrottle.cs` (35) | per-IP failure window | stays in host (password endpoint) |
| `RoomCreation.cs` (174) | `IRoomCreationService` + room form validation via `MUD.Data.RoomRules` | rules to Shared, service to host |
| `RoomMapLayout.cs` (154), `RoomRecord.cs` (22) | pure map layout | to Shared |
| `PlayerEditing.cs` (32) | `PlayerRules` (which controls an editor may use) | to Shared |
| `Account.cs` (20) | `Account`, `AccountRole`, `IAccountService` | `AccountRole` to Shared |

### 1.3 Tests (`MUD.Web.Tests`, 253 `[Fact]`/`[Theory]` methods, xunit 2.4.1, no Mongo)

| File | Methods | Classes | What moves where |
|---|---|---|---|
| `AccountSessionTests.cs` | 41 | `FakeAdminClient`, `LoginPageTests`, `LogoutPageTests`, `SessionAuthenticationHandlerTests`, `SessionAccountServiceTests`, `PasswordPageTests`, `LoginThrottleTests`, `CoreSocketTests`, `NoMongoForAccountsTests`, `GrpcAdminClientSocketTests` (+`StandInAdmin`) | Page tests become `AuthApiTests`/`PasswordApiTests`; the rest stay |
| `CreatorTests.cs` | 12 | `CreatorPolicyTests`, `RoomFormValidationTests` | policy test stays (host); validation to `MUD.Web.Shared` tests |
| `LibraryClientTests.cs` | 9 | `LibraryGrpcClientTests`, `FakeLibraryClientTests` | stay (host) |
| `ModulesPageTests.cs` | 19 | `ModulesPageTests` | to `ModulesApiTests` + client component tests |
| `NoDatabaseAccessTests.cs` | 4 | `NoDatabaseAccessTests` | stay and extend (section 9.4) |
| `PlayerEditTests.cs` | 35 | `PlayerRulesTests`, `PlayerPagesTests` | rules to Shared tests; pages to `PlayersApiTests` + client tests |
| `RoomMapTests.cs` | 34 | `RoomMapLayoutTests`, `CreatorMapPageTests`, `CreatorMapColourTests` | layout to Shared tests; page/colour to `MapApiTests` + client tests |
| `RoomPagesTests.cs` | 75 | `RoomCreationServiceTests`, `RoomValidatorParityTests`, `RoomEditPageTests`, `CreatorPageTests` | service/parity stay or move to Shared; page tests to `RoomsApiTests` + client tests |
| `TerrainPagesTests.cs` | 24 | `TerrainPagesTests` | to `LibraryApiTests` + client tests |

`MUD.Web.Tests` references `MUD.Web` and (for `PasswordRuleParityTests`-style checks) `MUD.Core`. CI (`.github/workflows/ci.yml`)
runs `dotnet restore/build/test MUD.sln` on `ubuntu-latest` with `setup-dotnet` `8.0.x`; it installs **no workload**.

### 1.4 Environment facts

- `MUD.Web` is `Microsoft.NET.Sdk.Web`, net8.0, references `MUD.Data` and `MUD.Contracts`, package `Grpc.Net.Client`.
- **`MUD.Data` references `MongoDB.Driver` and `MongoDB.Bson`.** `MUD.Web` uses it only for `RoomRules`. The WASM client
  must not reference `MUD.Data` (it would ship the Mongo driver to the browser and break the "no database" rule in spirit).
  See section 3.2.
- Local SDK: `dotnet` resolves to `/usr/local/bin/dotnet -> /usr/share/dotnet/dotnet`, SDK `8.0.128`. `dotnet workload list`
  there shows **`wasm-tools 8.0.28/8.0.100` installed (FileBased)**; the packs (Mono browser-wasm runtime, AOT cross
  compiler, Emscripten 3.1.34, `MonoAOTCompiler.Task`, `WebAssembly.Sdk`) are present under `/usr/share/dotnet/packs`.
  `/usr/bin/dotnet` is a broken symlink to `/usr/lib/dotnet` (running it gives `libhostfxr.so not found`); use the
  `/usr/local/bin` one. NuGet cache already holds `Microsoft.AspNetCore.Components.WebAssembly` and `.DevServer`.
  No compile of a WASM project was done in this card **[verify]**.
- CI: `setup-dotnet 8.0.x` resolves the newest 8.0 SDK on the runner (feature band 8.0.4xx today). Workload manifests are
  per feature band, so the local `8.0.100`-band install does not tell us what CI needs. CI has no `wasm-tools`.
- Deploy: `scripts/deploy.sh` does `dotnet publish MUD.Web/MUD.Web.csproj -c Release -o <release>/web` and checks for
  `web/MUD.Web.dll`. Unit `planar-realms-web.service` runs `current/web/MUD.Web` on `127.0.0.1:5080`.

## 2. Hosting model

Hosted WebAssembly, three web projects plus the existing contract:

```
MUD.Web.Shared   (net8.0 class library, no ASP.NET, no gRPC, no Mongo)   DTOs, enums, fixed messages, validation rules, pure layout
MUD.Web.Client   (net8.0, Microsoft.NET.Sdk.BlazorWebAssembly)           Razor components, client services, Fluent UI
MUD.Web          (net8.0, Microsoft.NET.Sdk.Web)                         host: JSON API, session auth, IAdminClient, serves the client
MUD.Contracts    (unchanged)                                             gRPC contract; referenced by MUD.Web only
```

Reference graph: `MUD.Web -> MUD.Web.Client` (so publish carries the client), `MUD.Web -> MUD.Web.Shared`,
`MUD.Web.Client -> MUD.Web.Shared`, `MUD.Web -> MUD.Contracts`. **Forbidden**: `MUD.Web.Client` or `MUD.Web.Shared` referencing
`MUD.Contracts`, `Grpc.*`, `Google.Protobuf`, `MUD.Data`, `MUD.Core`, or any MongoDB package. Enforced by a test (9.4).

Decision: standalone WASM client served by the existing host (`Microsoft.AspNetCore.Components.WebAssembly.Server`,
`UseBlazorFrameworkFiles`, `MapFallbackToFile("index.html")`), **not** the .NET 8 "Blazor Web App" with per-page
interactivity. Reasons: the owner asked for WASM; there is no SEO or prerender need (every page except `/login` is behind
sign-in); prerender would run component code on the server with the user's claims and add a second auth path; the WASM-only
shape keeps the host a plain API. Cost: first load shows a loading splash (section 8.3). The alternative is in Open questions.

Host responsibilities after the change:

- `app.MapGroup("/api")` endpoints (section 4), all `RequireAuthorization` with the existing policies unless stated.
- Static files + Blazor framework files; fallback to `index.html` for non-`/api` GETs so deep links
  (`/creator/rooms/a.b`) and refreshes work.
- Session authentication and antiforgery (section 5).
- Still binds `MUD_WEB_URL` (default `http://127.0.0.1:5080`); no new setting is required. Core socket settings unchanged.

No prerendering, no SignalR, no Blazor Server. The host opens no new listener.

### 2.1 Where code lives (summary of the move)

| Today | After |
|---|---|
| `*.cshtml` + `*.cshtml.cs` | `MUD.Web.Client/Pages/*.razor` (UI + client state) and `MUD.Web/Api/*Endpoints.cs` (handler logic) |
| `IAdminClient`, `GrpcAdminClient`, `CoreSocket`, session handler, `LoginThrottle`, `RoomCreationService` | unchanged, in `MUD.Web` |
| `RoomMapLayout`, `RoomRecord`, `PlayerRules`, `AccountRole`, room/feature/exit validation | `MUD.Web.Shared` |
| Page error/notice sentences (`LoginModel.GenericError`, `RoomEditModel.RegenWarning`, ...) | `MUD.Web.Shared/Messages.cs` as constants, unchanged text |
| `TempData["Notice"]` | client `NoticeService` (section 8.2) |

## 3. Shared project and DTOs

### 3.1 DTO rules

- Hand-written C# `record`s in `MUD.Web.Shared`, serialised with `System.Text.Json` (web defaults, camelCase). The protobuf
  types never cross the API (keeps `Google.Protobuf` and gRPC out of the download, and decouples the browser from core's contract).
- The host owns one mapping layer `MUD.Web/Api/Mappers.cs` (proto <-> DTO). It replaces the proto-touching parts of every
  PageModel. Unit tested field by field.
- Optional coordinates stay `int?`: `null` is unplaced, never 0 (WEB.md). Mappers must preserve this (a test per DTO with X/Y/Z).
- No DTO carries a password except the three request DTOs that must (`LoginRequest`, `ChangePasswordRequest`); no response DTO
  has any password field; `PlayerDetail` never had one.
- Domain results travel as an envelope (section 4.1), not as HTTP status codes, because core's reply enums (`Ok`, `NoChange`,
  `Invalid`, `NotFound`, `AlreadyExists`, `Refused`, `AuditFailed`, `Forbidden`, `LastAdmin`, `Failed`) map 1:1 onto the page messages
  that already exist.

### 3.2 Validation rules in the client

Room, feature and exit validation (`MUD.Data.RoomRules`: key/area patterns, 100/4000 length caps, control characters,
direction pattern, max 32 exits / 64 features) is needed in the browser for immediate feedback and on the host as the first
server check (core still re-validates and remains the authority). The client cannot reference `MUD.Data`.

Decision (needs owner/implementer confirmation, Open question 3): extract `RoomRules` into a dependency-free project
**`MUD.Rules`** referenced by `MUD.Data` (compat shim or type forward), `MUD.Core`, and `MUD.Web.Shared`. Checked: `MUD.Data/RoomRules.cs` uses only `System.*` namespaces (collections, Linq, Regex), so it has no Mongo-typed members and extraction is mechanical. This is a prerequisite card, independent of Blazor. Fallback if rejected:
link the source file into `MUD.Web.Shared` (`<Compile Include="..\MUD.Data\RoomRules.cs" Link=...>`), which keeps one source of
truth but needs the file to stay dependency-free.

## 4. JSON API surface

All under `/api`, JSON, host-side only. One endpoint set per `IAdminClient` capability the pages use. Names below are the
decision; request/response DTOs mirror the fields the page models bind today.

### 4.1 Conventions

- **Auth**: every endpoint but the three marked "anon" requires the existing session authentication; policy shown in the
  table. Authorisation is enforced on the host per endpoint (`RequireAuthorization(WebPolicies.CreatorOrAdmin)` etc.);
  hiding things in the client is cosmetic only.
- **Antiforgery**: every non-GET endpoint (and `login`) validates the antiforgery header (section 5.3).
- **Status codes**: `200` with envelope for any answer core gave (including refusals of a domain nature); `401` session absent
  or ended (`CoreRefusedException.SessionEnded` = true; the host also deletes the cookie); `403` role too low
  (`CoreRefusedException` otherwise, or failed policy; replaces the old redirect to `/access-denied`); `404` for reads whose
  entity does not exist (`GetRoom`, `GetPlayer`, library `Get`); `400` for malformed JSON/route values; `503` with
  `{ "result": "Unavailable" }` when `CoreUnavailableException`. **No endpoint ever returns a redirect, HTML or an exception
  message**; unhandled errors give `500` with a fixed body (ProblemDetails with no detail text).
- **Envelope** for writes: `{ "result": "Ok|NoChange|Invalid|NotFound|AlreadyExists|Refused|AuditFailed|Forbidden|LastAdmin|Failed",
  "message": string?, "errors": [ { "field": string, "message": string } ], "data": <typed payload or null> }`.
  `message` is only filled with core text where WEB.md already shows core's text (module refusal wording, delete-room sentence,
  generation counts); all other sentences are chosen client-side from `result` using `Messages.cs`. Core's text and exception text are never otherwise echoed
  (existing rule).
- Route values are URL-encoded; room keys contain `.` and are passed as a single segment (`RoomRules.PrefixPattern` is `^[a-z0-9_]+(\.[a-z0-9_]+)*$`, so keys never contain `/`).
- `Cache-Control: no-store` on all `/api` responses.

### 4.2 Endpoint table

| Endpoint | `IAdminClient` call | Policy | Replaces page handler |
|---|---|---|---|
| `GET /api/antiforgery` (anon) | none (framework) | anon | the hidden `__RequestVerificationToken` fields |
| `GET /api/auth/session` (anon) | none: reads claims the handler built (`GetSession` already ran in authentication) | anon, returns `{name, role}` or `401` | `_Layout` nav checks, `Index` redirect, `Login` GET redirect |
| `POST /api/auth/login` (anon) | `WebAuthenticateAsync(name, password, remoteKey)` | anon | `Login.OnPost` |
| `POST /api/auth/logout` | `LogoutAsync(token)` | any (idempotent: succeeds also when anonymous or core down) | `Logout.OnPost` |
| `POST /api/auth/password` | `ChangePasswordAsync` | signed in | `Password.OnPost` |
| `GET /api/rooms` | `ListRoomsAsync` | CreatorOrAdmin | `Creator.OnGet` (area suggestions), `CreatorMap` |
| `GET /api/rooms/{key}` | `GetRoomAsync` | CreatorOrAdmin | `RoomEdit.OnGet` |
| `POST /api/rooms` | `CreateRoomAsync` (via `RoomCreationService`) | CreatorOrAdmin | `Creator.OnPost` |
| `PUT /api/rooms/{key}` | `UpdateRoomAsync` (whole form incl. `features_present`) | CreatorOrAdmin | `RoomEdit.OnPostSave` |
| `DELETE /api/rooms/{key}` | `DeleteRoomAsync` | CreatorOrAdmin | `RoomEdit.OnPostDelete` |
| `POST /api/rooms/{key}/move` | `MoveRoomAsync(key, newArea, newLocalKey)` | CreatorOrAdmin | `RoomEdit.OnPostMove` |
| `POST /api/rooms/{key}/regenerate` | `RegenerateRoomsAsync(key)` | CreatorOrAdmin | `RoomEdit.OnPostRegen` |
| `POST /api/rooms/{key}/update-generated` | `UpdateRoomsAsync(key)` | CreatorOrAdmin | `RoomEdit.OnPostUpdate` |
| `GET /api/terrain/colors` | `ListTerrainColorsAsync` | CreatorOrAdmin | `CreatorMap` colour lookup |
| `GET /api/players?q=&sort=&page=` | `ListPlayersAsync(filter, descending, page, 50)` | CreatorOrAdmin | `PlayerList.OnGet` |
| `GET /api/players/{nameKey}` | `GetPlayerAsync` | CreatorOrAdmin | `PlayerEdit.OnGet` |
| `PUT /api/players/{nameKey}` | `UpdatePlayerAsync` | CreatorOrAdmin | `PlayerEdit.OnPost` |
| `GET /api/modules` | `ListModulesAsync` | CreatorOrAdmin | `Modules.OnGet` |
| `POST /api/modules/{name}/start` \| `stop` \| `restart` | `Start/Stop/RestartModuleAsync` | **Admin** (core re-checks; host also requires `Role=Admin`, see 4.4) | `Modules.OnPostStart/Stop/Restart` |
| `GET /api/library/{kind}` | `List{Fragments,FeatureTemplates,NeighborPhrases}Async` | CreatorOrAdmin | `TerrainList.OnGet` |
| `GET /api/library/{kind}/{id}` | `Get...Async` | CreatorOrAdmin | `TerrainEdit.OnGet` |
| `POST /api/library/{kind}` | `Create...Async` | CreatorOrAdmin | `TerrainEdit.OnPostSave` (no id) |
| `PUT /api/library/{kind}/{id}` | `Update...Async` | CreatorOrAdmin | `TerrainEdit.OnPostSave` (id) |
| `DELETE /api/library/{kind}/{id}` | `Delete...Async` | CreatorOrAdmin | `TerrainList.OnPostDelete` |

`{kind}` is `fragments`, `features` or `phrases`; the host's kind switch is the only place that picks one of the 15 library
calls. The kind and id come only from the route, never from the body (this keeps the existing guarantee that a form cannot
redirect a write to another entity; the body DTO has no `kind` and no stored `id`).

Not exposed because no page uses them: `ListAudit`, `RegenerateMap`, `GetSession` as its own endpoint. The audit viewer
and map regeneration remain out of scope.

### 4.3 Per-page behaviour that must survive in the handler

- **Login**: `remoteKey` is `HttpContext.Connection.RemoteIpAddress` as today (host only; the client cannot choose it).
  `AuthOk` sets the cookie (`Set-Cookie` on the response, same `CookieOptionsFor`); `AuthRetired` returns the retired message;
  `AuthTryAgainLater` and core-down return the unavailable message; every other result returns the generic message after the
  500 ms delay. Bad credentials and throttled address stay indistinguishable. Response body never contains the token.
- **Password**: the `LoginThrottle` check (locked-out IP sees the generic error and core is not asked), failure counting and the
  500 ms delay stay in the handler. Success keeps the session and returns the confirmation. Client clears all three fields after
  any response.
- **Rooms save**: the handler sends the whole form and the cookie token; `NO_CHANGE`, `INVALID` (field errors, including
  `Features[i].*` prefixed "Feature N:" 1-based), `NOT_FOUND`, `AUDIT_FAILED`, `FORBIDDEN` map as WEB.md lists. The reply carries the
  room as it is now (`data`). No revision, last write wins (D30).
- **Dangling exits**: stays a host check in `RoomCreationService`/save handler plus the client checkbox, same text.
- **Map**: `GET /api/rooms` returns definition fields only (short, area, X/Y/Z, exits, `gen`). Colours are fetched only when
  the visible level contains a generated room; the host validates `#RRGGBB` before returning them (the check moves from
  `CreatorMapModel.FillFor` to the endpoint, with the client re-checking before putting it into an SVG attribute).
- **Players**: list filter cut to 64 characters server-side; sort `name` / `-name`; 50 per page; "Online" stays `unknown`.
  Role change and retire controls are hidden by `PlayerRules` in the client; core stays the authority and its result
  (`Forbidden`, `LastAdmin`, ...) is shown.
- **Modules**: `Restart` default notice wording unchanged; results `Ok`/`NoChange`/`NotFound`/`Refused` show the notice, `Forbidden` and
  others the fixed message.
- **Library**: `Ok` returns the entity (new id on create) so the client can navigate to the edit view with the "Saved." notice;
  `Invalid` carries field errors; feature version is core's (client shows the version text from the loaded entity).

### 4.4 Authorisation on the host

`WebPolicies.CreatorOrAdmin` is reused unchanged. Admin-only module actions today rely on core's check plus UI hiding; the host
adds `RequireAuthorization(p => p.RequireClaim(ClaimTypes.Role, "Admin"))` as defence in depth. This is a deliberate small
tightening; the end behaviour (403 for a creator) is identical to the current "forged POST from a creator is refused".

## 5. Auth, cookie and antiforgery flow

### 5.1 Session

Unchanged mechanism: opaque core token in cookie `mud.session` (HttpOnly, SameSite=Lax, `Secure` when HTTPS, session cookie,
Path `/`). The browser JS and the WASM code never see the token (HttpOnly). The client's `HttpClient` calls same-origin `/api/...`
with default credentials (the browser attaches the cookie); the client is served from the same origin, so no CORS is configured
and **none may be added** (a CORS policy would be a new attack surface). `SessionAuthenticationHandler` still calls core
`GetSession` on every API request, so a demotion or retirement applies on the next call.

Handler changes: for requests whose path starts with `/api`, `HandleChallengeAsync` returns `401` and `HandleForbiddenAsync`
returns `403` instead of redirecting to `/login?ReturnUrl=` and `/access-denied`. Non-API paths do not need the redirect any
more (the fallback serves `index.html`; the client routes).

### 5.2 Client auth state

`SessionAuthStateProvider : AuthenticationStateProvider` in the client calls `GET /api/auth/session` at startup and after login,
logout and any `401`. It builds a `ClaimsPrincipal` with `Name` and `Role` for `<AuthorizeView>` and `[Authorize(Policy="CreatorOrAdmin")]`
(the same policy name and rule registered client-side via `AddAuthorizationCore`). This drives navigation and redirects only.

- Anonymous on a protected route: client redirects to `/login?ReturnUrl=<path+query>` (same shape as today); after login it goes
  to `ReturnUrl` if it is a local path, else `/character`. `ReturnUrl` must be validated as a local relative path
  (new code; the Razor version never honoured it after login, so honouring it is **new behaviour**, see Open question 6).
- Signed in without role: client shows the access-denied view at `/access-denied` (same text as `AccessDenied.cshtml`) and the
  URL is kept so there is no redirect loop.
- Any API `401` anywhere: clear the cached state and go to `/login` with the `ReturnUrl`; any `403`: show access-denied.
- `/` redirects to `/character` or `/login` (client route).

### 5.3 Antiforgery

Cookie-authenticated JSON endpoints need CSRF protection; SameSite=Lax is a mitigation, not the control. Design:

1. Host: `builder.Services.AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN")`, and `app.UseAntiforgery()` with the validation
   applied through an endpoint filter on every non-GET `/api` endpoint (`login` included).
2. `GET /api/antiforgery` (anon) calls `IAntiforgery.GetAndStoreTokens(ctx)`; the antiforgery cookie is set by the framework
   (HttpOnly), the response body is `{ "headerName": "...", "requestToken": "..." }`.
3. Client: a `DelegatingHandler` on the typed `HttpClient` fetches the token once (lazily), adds `X-XSRF-TOKEN` to every non-GET
   request, and on a `400` with antiforgery failure refetches once and retries the idempotent request, otherwise surfaces the
   "reload the page and try again" message (new fixed sentence). The token is refreshed after login and logout (the identity
   changes).
4. Same-origin only: no `Access-Control-Allow-*` headers; JSON `Content-Type` is required (a plain HTML form POST cannot send it).

Logout stays POST-only. `GET /logout` as a URL is served by the client route and does nothing but redirect to `/`, as today.

### 5.4 Not changed

Plaintext password caveat (WEB.md): passwords are sent in the body of `POST /api/auth/login` and `/password` only, never in a
URL, query string or response, never logged, never stored in client state past the request; inputs use
`autocomplete="current-password"` / `"new-password"`. Over plain HTTP they still cross the network unencrypted, so the localhost
bind stays and HTTPS/reverse proxy is still the precondition for anything wider. No-token/handshake on `frontends.sock` (P7-1)
remains an open point in ADMIN_API.md and is unaffected.

## 6. Mapping existing logic and tests

### 6.1 PageModel logic

| PageModel responsibility | New home |
|---|---|
| Auth attributes (`[Authorize]`, policy) | endpoint `RequireAuthorization` (host) + `<AuthorizeView>`/`@attribute [Authorize]` (client, cosmetic) |
| Calling `IAdminClient`, try/catch `CoreUnavailable`/`CoreRefused`, result switch | endpoint handler `MUD.Web/Api/<Area>Endpoints.cs` (one static class per area; thin, calls mapper, returns envelope) |
| Proto <-> view model conversion | `Mappers.cs` |
| Message sentences | `Messages.cs` (Shared); selected by result code in the client |
| Bound form properties, field-level display, add/remove/move exit and feature rows | component state in the page `.razor` (+ `@code`); the no-write round trips become local list edits, no network call |
| `TempData["Notice"]` | `NoticeService` |
| `RoomMapLayout`, `PlayerRules`, validation | unchanged code in `MUD.Web.Shared` |
| `RoomEdit.RegenWarning` + browser `confirm(...)` | a Fluent confirmation dialog with the same text |
| Client-state invariants (name key from route only, kind/id from route only, provenance read-only) | request DTOs simply do not have those fields |

### 6.2 Client services (`MUD.Web.Client/Services`)

- `ApiClient` (typed `HttpClient`): one method per endpoint, returns `ApiResult<T>` (`Ok`, status, envelope) and maps `401/403/503/network error`
  to `ApiOutcome.SignedOut | Denied | Unavailable | NetworkFailed`; never throws for these.
- `SessionAuthStateProvider`, `AntiforgeryHandler`, `NoticeService`, `ViewportService` (breakpoint, via `matchMedia` JS interop).
- No client service holds a token or talks gRPC.

## 7. Layout, navigation and responsive design (Fluent UI)

Package: `Microsoft.FluentUI.AspNetCore.Components` (**[verify]** the 4.x line as the one that supports net8.0 and pin an exact version),
registered with `AddFluentUIComponents()`; its static web assets are served from the host (`_content/...`), **no CDN** (matches the
current "no external requests" rule). Components used: `FluentLayout`, `FluentHeader`, `FluentBodyContent`, `FluentNavMenu`/`FluentNavLink`,
`FluentDialog`, `FluentMessageBar`, `FluentProgressRing`, `FluentSkeleton`, `FluentCard`, `FluentDataGrid`, `FluentTextField`/`FluentTextArea`/
`FluentSelect`/`FluentCheckbox`/`FluentAutocomplete`, `FluentTabs`, `FluentButton`, `FluentToast`/`FluentToastProvider`. Names are the design intent; confirm each
exists in the pinned version.

### 7.1 Breakpoints

| Class | Width | Layout |
|---|---|---|
| Phone | < 600px | single column; **bottom tab bar** (max 4 slots) + **drawer** for the rest; top bar shows page title and, when applicable, a back button |
| Tablet | 600-1023px | top bar + collapsible left rail (icons) that expands as an overlay drawer |
| Desktop | >= 1024px | permanent left nav, content max-width 1100px (map and tables use the full width) |

Design baseline is 360x640 (the owner's width). Anything that works at 360px works wider; wide layouts are enhancements.

### 7.2 Navigation

Items and visibility (same as today's `_Layout`): Character, Change password (signed in); Creator, Map, Players, Modules, Terrain
(CreatorOrAdmin); Log out (a button that POSTs, never a link). Anonymous users see no nav, just the login page.

- Phone, creator/admin: bottom bar = Map, Creator, Players, **More**. More opens a bottom-sheet drawer with Modules, Terrain, Character,
  Change password, Log out. Phone, plain player: bottom bar = Character, Password, Log out (3 slots).
- Tablet/desktop: left nav lists all items; Log out sits at the bottom of the nav.
- Active item is marked by text weight and an icon state, not colour alone. Nav has a "skip to content" link and proper `nav`/`aria-current`.
- Drawer closes on navigation and on Escape/backdrop tap; focus returns to the opener. The bottom bar respects `env(safe-area-inset-bottom)`.
- Page title is set per page (`<PageTitle>`), format `<Title> - Planar Realms` as today.

### 7.3 Global rules (acceptance for phone)

1. `<meta name="viewport" content="width=device-width, initial-scale=1">` (as in the current layout, kept in `index.html`).
2. Every interactive element (buttons, links in nav and tables, checkboxes, selects, row actions) has a hit area of at least
   **44 x 44 CSS px** (padding or `min-height/min-width`); Fluent's default control height is smaller **[verify]**, so a global stylesheet
   `mobile.css` raises it via control height tokens or `::part` rules. At least 8px between adjacent targets.
3. **No horizontal page scroll at 360px**: `html,body {max-width:100%; overflow-x:hidden}` is NOT the fix (it hides bugs); every
   component must fit. Long unbroken tokens (room keys, handles) use `overflow-wrap:anywhere`. Only the map canvas may scroll/pan internally.
4. Body text 16px minimum (prevents iOS zoom on input focus); inputs use `font-size:16px`; line-height 1.4+.
5. One primary action per screen, reachable at the bottom: forms end in a **sticky action bar** (Save / Cancel) above the bottom nav,
   full width on phone. Destructive actions (Delete, Regen, Move) live in a separate "Danger zone" section, never next to Save.
6. Long pages use collapsible sections (`FluentAccordion`) so the user can jump to Exits or Features without scrolling the room text.
7. Respect `prefers-reduced-motion` and `prefers-color-scheme`? Colour scheme is an open question (8); contrast must meet WCAG AA either way.

### 7.4 Patterns per page type

**Form (login, password, create room, player edit, terrain edit, room edit sections)**: single column labelled fields on phone
(label above input, never placeholder-only), two columns only from 600px up for short fields (X/Y/Z, race/class/gender). Field errors
appear directly under the field and in a `FluentMessageBar` summary at the top (same messages; the summary is focused and announced
`role="alert"` after a failed save). Numeric inputs use `inputmode="numeric"`; text with autocomplete keeps suggestions via `FluentAutocomplete`
(area prefixes). Password fields have `autocomplete` hints and no pre-filled value ever.

**Repeating rows (exits, features)**: each row is a card with labelled fields; Move up / Move down / Remove are 44px buttons in the card
footer. Add exit/feature buttons sit after the list; the Save bar is sticky. Counts shown ("3 of 32 exits"). Handles/adjectives/plurals keep the "separated by commas or spaces" help text.

**List / table (players, modules, terrain library, unplaced rooms, map mismatches)**: `FluentDataGrid` at >= 600px. Below 600px the
same data renders as a **card list** (one card per row: title line, key facts as label/value pairs, actions in a footer), driven by
`ViewportService`; both render from the same view model so the tests cover one source. Sort and filter controls collapse into a
single "Filter" row (text field + sort select) above the list. Paging is Previous/Next buttons with "Page N" text (50 per page,
unchanged) at the bottom. Terrain tabs become a horizontally scrollable segmented control only if it cannot fit; the preferred phone form
is a `FluentSelect` for the tab plus the count. Status that used colour (online, running, required) keeps words with the existing
`[+]`/`[-]`/`[!]` text prefixes.

**Map (`/creator/map`)**: SVG rendered from `RoomMapLayout` (unchanged geometry). Container `touch-action: none` with pointer-event handling
(pan with one pointer, pinch zoom with two, wheel/trackpad zoom, double-tap to zoom in) implemented as a **small JS module**
`map-interop.js` that updates the SVG `viewBox`/transform; C# keeps layout and click handling. This replaces the "no JavaScript" property
of the Razor map, which WASM makes moot. Because WASM-to-JS gesture round trips are too chatty for smooth pinch, the transform is applied in JS and C# is told only on gesture end.
Controls (all 44px): zoom in, zoom out, reset/fit, and the Area and Level selectors in a collapsible toolbar above the map (collapsed to an
icon on phone so the map gets the screen). A room tap goes to `/creator/rooms/{key}`; a tap target smaller than 44px at the current zoom
falls back to selecting the nearest room within a 22px radius and showing a bottom sheet with "Open room" (so dense maps stay usable).
The Unplaced list and Mismatches list render below the map as card lists. Keyboard: arrow keys pan, +/- zoom (desktop).
Legend/terrain text stays in the room title and the bottom sheet (colour is never the only signal).

### 7.5 Per-page layout notes

| Page | Phone | Tablet/desktop |
|---|---|---|
| Login | centred card, name/password stacked, full-width button | same, card max 420px |
| Character | greeting card | same |
| Password | stacked fields + sticky Save | same |
| Creator | stacked fields; exits as cards; Save sticky | X/Y/Z in one row; exits 2-up |
| Map | full-bleed map, collapsible toolbar | toolbar on top, unplaced/mismatch panels to the side |
| Room edit | accordion: Basics, Exits, Features, Generated (if any), Move, Delete; sticky Save | two-column: form left, summary right |
| Players | filter row + card list | grid |
| Player edit | stacked; role and retire controls only when allowed | two columns |
| Modules | module cards, action buttons full width (admin) | grid with Actions column |
| Terrain list | tab select + filters + card list | tabs + grid |
| Terrain edit | stacked; terrain checkboxes in a scrollable fieldset max 40vh | two columns |

## 8. Error and loading states

### 8.1 Loading

- First app load: a CSS-only splash in `index.html` (logo text "Planar Realms", a progress bar fed by `--blazor-load-percentage`
  Blazor sets) so the user sees something before WASM starts. No dependence on Fluent or WASM for the splash.
- Page data: a `LoadState<T>` (`Loading | Loaded | NotFound | Denied | Unavailable | Failed`) used by every page; `Loading` shows
  `FluentSkeleton` shaped like the content (after 150 ms, to avoid flicker), writes `aria-busy`. Buttons that submit show a spinner and are
  disabled until the response (prevents double submit); the sticky bar stays visible.
- Map: skeleton rectangle while rooms load; colours load afterwards and the map re-fills without moving.

### 8.2 Errors and notices (all existing sentences preserved)

| Situation | Behaviour |
|---|---|
| Core down (`503`/`Unavailable`) | the existing "game is not reachable" sentence; page-level message bar with a Retry button (reads), or an inline error that keeps form input (writes); was a 503 page |
| Network failure to the host | new fixed sentence "Could not reach the website. Check your connection and try again." (no existing equivalent; WASM can lose the host) |
| `401` | session expired: go to `/login` with `ReturnUrl`; unsaved form input is lost except that room/player/terrain editors keep the draft in memory for the in-page re-login prompt (**optional**, Open question 9) |
| `403` | access-denied view (was 403 page / redirect) |
| `404` (`NotFound`) | not-found view with a link back to the list (was 404 page) |
| `Invalid` | field errors in the form + summary; user input kept (as today) |
| `NoChange`, `AlreadyExists`, `Refused`, `AuditFailed`, `Forbidden`, `LastAdmin`, `Failed` | the existing fixed sentences from `Messages.cs`, shown as a message bar (error/warning/info per intensity) |
| Success | `Ok` shows the existing notice ("Saved.", module notice, generation counts) in a toast/message bar that persists across the navigation that follows (`NoticeService` holds it until shown once; replaces `TempData`, so unlike TempData it is lost on a hard reload, which is acceptable) |
| Unhandled exception in a component | `ErrorBoundary` per page shows "Something went wrong. Reload the page." and logs to the browser console only; no exception text is rendered |
| WASM fails to start (old browser, blocked) | `index.html` `<noscript>` and the Blazor `#blazor-error-ui` strip say the site needs a current browser with WebAssembly and JavaScript enabled |

User-visible messages from the existing page models are moved verbatim to `Messages.cs`; a test (9.3) pins the strings so a
reword is a conscious change.

### 8.3 Unsaved changes

Leaving a dirty editor (room, player, terrain, creator) prompts with a Fluent dialog (new behaviour; the Razor site had none, but
client-side navigation makes accidental loss much easier, especially on phones with a back gesture). Out-of-scope if the owner declines (Open question 9).

## 9. Test strategy

### 9.1 Projects

| Project | Contents | Needs |
|---|---|---|
| `MUD.Web.Tests` (existing) | host: API handler tests, mapper tests, session handler, policy, throttle, `GrpcAdminClient` over UDS, no-database tests, `FakeAdminClient` | `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory) |
| `MUD.Web.Shared.Tests` (new, or a folder in `MUD.Web.Tests`) | `RoomMapLayout`, `PlayerRules`, validation, `Messages`, DTO (de)serialisation | none |
| `MUD.Web.Client.Tests` (new) | bUnit component tests | `bunit` (net8-compatible **[verify]**) |

### 9.2 API handler tests replace PageModel tests

`WebApplicationFactory<Program>` over the real pipeline (auth handler, antiforgery, policies, JSON) with `IAdminClient` replaced by the
existing `FakeAdminClient`. This needs `public partial class Program` (or an `InternalsVisibleTo`) and moving `WebPolicies` out of the top-level-statement file.
Each existing PageModel test becomes an HTTP-level test of the corresponding endpoint with the same arrangement and expectations
(result -> envelope + status instead of result -> page model state). Mapping of the 253 existing tests by area is in 1.3; every old test
is either ported, moved to Shared, kept, or explicitly deleted with a reason in the card's PR description. Required additional tests:

- every endpoint: anonymous -> 401, creator-only endpoint as plain player -> 403, admin-only module action as creator -> 403;
- every non-GET endpoint: missing/invalid antiforgery -> 400 and `IAdminClient` never called;
- `401` for `CoreRefusedException.SessionEnded` (cookie deleted), `403` otherwise, `503` for `CoreUnavailableException`, with no exception text in the body;
- login: cookie attributes (HttpOnly, SameSite=Lax, Secure under HTTPS), generic message for bad credentials and throttled address, 500 ms delay path, token absent from body;
- password: throttle behaviour and "core not asked when locked out" (existing assertions);
- a route-table test asserting the exact set of `/api` endpoints and the policy on each (so an endpoint cannot be added unprotected);
- room save: null vs 0 coordinates round trip, `features_present` true with an empty list, whole-form payload, field-error prefix;
- the "request DTOs have no `kind`/`id`/`nameKey`/`gen*` field" guarantees (reflection test, replacing `TerrainEditModel` bound-property list).

### 9.3 Client tests (bUnit)

bUnit fits for: message selection by result code, form validation display, exit/feature row add/remove/move (pure state), breakpoint card/grid switch
(fake `ViewportService`), conditional controls by role (`PlayerRules`, admin-only module buttons), nav items per role, `LoadState` rendering, `ApiResult`
mapping (fake `HttpMessageHandler`). Fluent components need `Services.AddFluentUIComponents()` and loose JS interop in bUnit **[verify]**.
bUnit does **not** cover pixel layout, touch-target size, horizontal scroll or pinch zoom; those are the manual/E2E checks below. A test pins every string in `Messages.cs`.

### 9.4 Guard tests

`NoDatabaseAccessTests` keeps its four checks and gains: `MUD.Web.Client.csproj` and `MUD.Web.Shared.csproj` have no `MongoDB*`,
`Grpc*`, `Google.Protobuf` package and no `ProjectReference` to `MUD.Contracts`, `MUD.Data`, `MUD.Core`; their assemblies reference none; sources contain no `MUD_MONGO_URI`/`MongoDB`.
The unit-file check on `planar-realms-web.service` stays.

### 9.5 Not automated (stated, not hidden)

No browser is available to CI, so phone behaviour (360px, 44px targets, no horizontal scroll, drawer, pinch) is verified by a manual checklist
(Appendix A) on a real phone or Chrome device emulation, performed by the migration card that builds the layout and again at cutover. Adding Playwright
(headless Chromium + `dotnet test`) is the way to automate it; it is an Open question because it adds a browser download to CI. Nothing here has been
run against core/Mongo (no Mongo in the sandbox), same as today.

## 10. CI and deploy impact

### 10.1 wasm-tools

- **When it is required**: a plain Blazor WASM `dotnet build`/`publish` (no AOT, default trimming) uses the prebuilt runtime from the
  `Microsoft.NETCore.App.Runtime.Mono.browser-wasm` / `Microsoft.AspNetCore.Components.WebAssembly` packs, downloaded from NuGet; the `wasm-tools` workload is needed for
  AOT, native relinking (`WasmBuildNative`) and some trimming-size options **[verify]**. Decision: **first release is JIT/interpreter, no AOT**, so the workload
  is not a hard requirement. AOT is a later optimisation if first-load or CPU use disappoints.
- **Local machine** (`/usr/local/bin/dotnet`, root install at `/usr/share/dotnet`, SDK 8.0.128): `wasm-tools 8.0.28/8.0.100` is already installed with packs, so
  AOT could build here. Use `/usr/local/bin/dotnet`, not `/usr/bin/dotnet` (broken symlink). Not exercised in this card **[verify]**.
- **CI**: add to the workflow, after `setup-dotnet` and before `Restore`:
  `dotnet workload install wasm-tools` (idempotent; install as the job's user, no sudo needed for the runner's tool cache install **[verify]**; if the runner SDK path is root-owned the step needs `sudo`).
  Optionally cache `~/.dotnet`/`~/.nuget/packages`. If we stay non-AOT this step is belt-and-braces; the owner's instruction is to make CI build it, so it is included, and
  removable if it costs more than it earns (it downloads several hundred MB; **[verify]** the time cost).
- Pin the SDK with a `global.json` (`rollForward: latestFeature` within 8.0.x) so the local 8.0.1xx band and CI's 8.0.4xx band do not
  disagree about workload manifests **[verify]** — if CI and local bands cannot be reconciled, report the blocker rather than shipping AOT.
- `MUD.sln` gains the two new web projects and the two new test projects; the existing `Restore/Build/Test` steps stay; the "Core does not reference the telnet project" guard stays.

### 10.2 Publish and deploy

- `scripts/deploy.sh` keeps one `dotnet publish MUD.Web ... -o <release>/web`; because the host references the client project, the publish output contains `web/wwwroot/_framework/*`
  and `web/wwwroot/_content/*`. The only script change: add a sanity check `[ -f "$RELEASE_DIR.partial/web/wwwroot/_framework/blazor.webassembly.js" ] || die ...`
  (name **[verify]**; in .NET 8 the file is `blazor.webassembly.js`, possibly fingerprinted). No change to the units, ports or env files.
- The WASM publish step is slower than the Razor one (trimming/relink; design expectation 1-3 minutes without AOT, several more with AOT **[verify]**); `deploy.sh` runs on the maintainer's machine, so only time is affected.
- Release directory size grows (client assets add roughly 10-20 MB uncompressed plus precompressed `.br`/`.gz` copies **[verify]**); `MUD_KEEP_RELEASES` (default 5) bounds the disk use.
- The host must serve pre-compressed `.br` files (`UseBlazorFrameworkFiles` + static file content negotiation does this for the published assets **[verify]**), otherwise first load is several times larger.

### 10.3 Size and first-load budget (targets, to be measured, not promises)

| Metric | Target | Measured by |
|---|---|---|
| Transferred on first load (Brotli, empty cache) | <= 4 MB (runtime ~1.5-2 MB, app + Fluent ~1-2 MB estimated) | browser network panel against the published site |
| Repeat load (cached framework files) | <= 300 KB and interactive in <= 1.5 s on a mid-range phone | same |
| Time to interactive, first load, mid-range phone on 4G over localhost/LAN | <= 6 s; splash visible in <= 1 s | Chrome device emulation with throttling |
| Publish output (uncompressed) | <= 25 MB | `du` on the `web/` directory in the migration card |

Levers if the budget is missed, in order: trim unused Fluent icon/emoji packages (import only used icons), enable `InvariantGlobalization` and
`BlazorEnableTimeZoneSupport=false`, `PublishTrimmed` with a trim-safe `System.Text.Json` source generator (DTOs get a `JsonSerializerContext`), lazy-load the
rarely used pages (terrain, modules) as separate assemblies, then AOT (needs wasm-tools; larger download, faster run). The budget is an Open question for the owner to ratify.

Because the site is localhost-bound the network is not the bottleneck on the same machine; the budget matters for the phone use case, which means the host must be reachable from the phone
(LAN, VPN, tunnel). That access path is out of scope and conflicts with "bind localhost only" (Open question 1).

## 11. Migration order

Strangler approach so `hermes` always has a working site. A host setting `MUD_WEB_UI=razor|blazor` (env, read at start, default `razor` until cutover)
chooses which UI the host maps; both compile in one host during migration; the `/api` is always mapped. Each step is its own card/PR, with tests.

0. **Spike + prerequisites** (one card): create the three projects skeleton; prove `dotnet build` and `dotnet publish` of the hosted shape with Fluent on this machine and in CI; measure first-load size
   for a hello page; extract `MUD.Rules` (3.2); add `global.json` and the CI workload step; confirm the **[verify]** items. Gate: spike results recorded here before step 1.
1. **API foundation**: `MUD.Web.Shared` DTOs and `Messages`; host `/api` group, antiforgery, `/api/auth/*`, auth handler behaviour for `/api`; `Mappers`; `WebApplicationFactory` test harness; route-table test. Razor UI untouched and still default.
2. **Shell**: client shell (layout, nav, auth state, antiforgery handler, `ApiClient`, notice/loading/error components, `mobile.css`), pages Login, Character, Change password, Access denied. Behind `MUD_WEB_UI=blazor`.
3. **Read-only pages**: Modules (list; admin actions), Players list, Map (with touch gestures) and the room/colour endpoints.
4. **Forms**: Creator (create room), Player edit, Terrain list + edit (3 library kinds).
5. **Room edit** (largest, 463-line model): basics, exits, features, move, delete, regen/update.
6. **Cutover**: default `MUD_WEB_UI=blazor`; run the manual phone checklist; update DEPLOYMENT/WEB docs; delete Razor pages, their tests, `Microsoft.AspNetCore.Mvc.RazorPages` usage and the flag after a soak period (the owner decides length).
7. **Follow-ups** (separate, not in this spec's scope): AOT, Playwright E2E, offline/PWA, HTTPS.

Steps 2-5 can partly parallelise after step 1 (distinct page files), except the shared shell in step 2 must land first. Steps 1 and 0 touch `MUD.Web.csproj`, `Program.cs` and `MUD.sln`, which are collision hotspots; do those serially.

## 12. Rollback plan

- **During migration (flag still present)**: set `MUD_WEB_UI=razor` in `~/.config/mud/web.env` and restart `planar-realms-web` — one setting, no redeploy. (Restarting services is for the operator at that time, not this card.)
- **After deploy**: `scripts/deploy.sh --rollback` switches `current` to the previous release (which contains the Razor-only `web/`) and restarts units; it already tolerates releases without the other parts. Keep at least the Razor-era release among the retained ones (`MUD_KEEP_RELEASES` >= 3 around cutover).
- **After the Razor code is deleted (step 6 end)**: rollback is the previous release via `--rollback`, or `git revert` of the deletion commit on `hermes` and a redeploy. For this reason the Razor deletion is its own PR, merged only after the soak.
- **API is additive**: `/api` and the new projects do not alter core, the contract, units, ports or env files, so rolling back the web never affects the game or telnet.
- Cookie compatibility: the cookie name and format do not change, so a rollback in either direction keeps users signed in (core owns the session).

## 13. Out of scope (separate work)

Password hashing; HTTPS/reverse proxy; the `frontends.sock` token (P7-1); a game-output/terminal page; the audit log viewer; player self-retire; offline/PWA; AOT; sample-room preview in the terrain editor (no admin call exists);
reverse-exit creation; any change to `contracts/mud/admin.proto` or to core. No change to `MUD.Main`, `MUD.Core` (except consuming `MUD.Rules` if 3.2 is accepted), or the telnet host.

## 14. Open questions

Unresolved; none was silently decided beyond the defaults stated in the sections referenced.

1. **Phone reachability vs localhost-only.** The owner wants phone use and also localhost-only binding with plain HTTP and plaintext passwords. A phone cannot reach `127.0.0.1` on the server. What is the access path (VPN, SSH tunnel, LAN bind, reverse proxy with HTTPS)? Sending plaintext passwords over a LAN needs HTTPS first. This spec keeps the bind unchanged and does not solve it.
2. **Blazor flavour.** Standalone WASM served by the existing host (this spec) vs the .NET 8 Blazor Web App with `InteractiveWebAssembly` (prerendering, server-side auth for the first paint). Confirm the standalone choice.
3. **`RoomRules` extraction.** Create `MUD.Rules` (touches `MUD.Data`, `MUD.Core`) or link the source file? (`RoomRules.cs` is confirmed to use only `System.*`.) Is a prerequisite card acceptable?
4. **Fluent UI version.** Which package line/version (must support net8.0), and are the named components present? To confirm in the spike. Also Fluent's license/telemetry terms and CSS-variable approach for 44px targets.
5. **First-load budget** (10.3): are the targets acceptable, and is AOT allowed if they are missed (which makes `wasm-tools` mandatory in CI and on the deploy machine)?
6. **`ReturnUrl` after login.** The Razor login ignores `ReturnUrl` after sign-in (always `/character`). Honour it (new behaviour, small risk of open-redirect if not validated) or keep ignoring it?
7. **`GET /api/auth/session` anonymous 401 vs 200 with null.** Cosmetic; 401 chosen (browsers log it as an error in the console). Confirm or switch to `200 {"signedIn":false}`.
8. **Colour scheme.** Light only, system preference or a toggle; the current site has no styling at all.
9. **Unsaved-changes prompt and in-page re-login after a 401.** New behaviours (8.2, 8.3); accept, defer or drop.
10. **Host also serving a Razor fallback.** Is the `MUD_WEB_UI` flag and soak period acceptable, or is a hard cut preferred? And how long a soak?
11. **Automated browser tests.** Add Playwright to CI (browser download) or accept the manual phone checklist only?
12. **Admin-only host check for module actions** (4.4) is a small tightening beyond today's behaviour; accept.
13. **CI workload step** (10.1): install `wasm-tools` even without AOT? Cost vs the owner's instruction; also whether a root-owned SDK path needs `sudo` on `ubuntu-latest`.
14. **Broken `/usr/bin/dotnet` symlink** on the maintainer machine (`/usr/lib/dotnet` missing). Housekeeping; relevant because systemd or scripts that use `/usr/bin/dotnet` would fail. Not part of this work.
15. **Tests count at cutover.** Which of the 253 existing tests may be dropped as obsolete (page binding mechanics) vs must be ported; this spec requires each decision to be listed in the PR.

## Appendix A. Manual phone checklist (acceptance for the layout cards)

At 360x640 (Chrome device emulation and one real phone), for every page: no horizontal scroll (also with long keys/names); all
controls >= 44px and not touching; text readable without zoom; inputs do not trigger zoom on focus; bottom bar and drawer
operate with one thumb; Save reachable without scrolling to the top; keyboard (on-screen) does not hide the focused field or the
sticky bar; rotate to landscape without breakage; map: one-finger pan, two-finger pinch, double-tap zoom, zoom buttons,
tap a room opens it, page itself does not scroll while panning the map; error states (core stopped) show the fixed sentences.
Repeat at 768px and 1280px.
