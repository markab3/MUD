# Website: Blazor Server with Fluent UI (design spec)

Status: **built; cutover done** (card t_2375c78f). The Razor Pages are removed and the components own the real URLs (`WebRoutes.Prefix` is empty).
Sections 1-13 are the original design text and still describe intent; Appendices B-D describe what was built; Appendix E records the cutover verification.
Items marked **[verify]** in the design were exercised only where Appendix E says so.

Replaces the Razor Pages UI of `MUD.Web` described in [WEB.md](WEB.md). The behaviour in WEB.md and
[WEB_EDITING.md](WEB_EDITING.md) stays the requirement; this file changes the delivery mechanism only.

Owner decisions (fixed; they supersede earlier WASM / JSON API / DTO-project text):

1. Blazor **Server** (.NET 8 Blazor Web App, interactive server rendering) with `Microsoft.FluentUI.AspNetCore.Components`.
   Not WebAssembly. No JSON API, no client project, no shared DTO project, no wasm-tools.
2. Components run in the `MUD.Web` host and inject `IAdminClient` (gRPC to core) directly. The browser never touches core.
   `NoDatabaseAccessTests` still holds.
3. Login and logout stay plain HTTP (cookies cannot be set from a circuit). `SessionAuthenticationHandler` stays; components read
   the user through `AuthenticationState`; the session token is re-checked on circuit start and periodically (D31).
4. Phone is first class: mobile-first, touch targets >= 44px, no horizontal page scroll at 360px, one top bar with a pull-out menu from the left at every width (no bottom bar, no left rail),
   forms and tables reflow to stacked cards, one-handed use, map with touch pan and pinch zoom.
5. Dropped connections must not silently lose input: reconnect UI, state kept in components, clear lost-circuit message,
   optional local draft (section 8).
6. Preserve behaviour, policies (creator/admin), antiforgery on plain-HTTP posts, audit-first admin calls, every user-visible
   message, same URLs. Localhost bind and `scripts/deploy.sh` unchanged; one project, one release. Razor Pages and Blazor run side
   by side until the cutover card. No deploy, no restarts.

## 1. Facts about the current site (inventory)

From the repo at `d5ed91a` (`hermes`).

### 1.1 Pages (`MUD.Web/Pages`)

| Route | Page model (lines) | Auth | Handlers / purpose |
|---|---|---|---|
| `/` | `Index` (10) | none | redirect to `/character` or `/login` |
| `/login` | `Login` (62) | anonymous | POST `WebAuthenticate`, sets cookie; generic / retired / unavailable errors; 500 ms delay on failure |
| `/logout` | `Logout` (26) | none | GET redirects `/`; POST (antiforgery) `Logout` + delete cookie |
| `/access-denied` | `AccessDenied` (15) | anonymous | 403 page |
| `/character` | `Character` (16) | signed in | `Hello (<PlayerName>)!` |
| `/account/password` | `Password` (85) | signed in | `ChangePassword`; per-IP `LoginThrottle` (5 / 5 min) + 500 ms delay |
| `/creator` | `Creator` (98) | CreatorOrAdmin | create-room form (Key, Short, Long, Area, X/Y/Z, 4 exits, dangling checkbox); `ListRooms` for area suggestions; `CreateRoom` via `RoomCreationService` |
| `/creator/map` | `CreatorMap` (79) | CreatorOrAdmin | `ListRooms` (+ `ListTerrainColors` when a generated room is on the level); `RoomMapLayout` SVG; `?area=&z=` |
| `/creator/rooms/{key}` | `RoomEdit` (463) | CreatorOrAdmin | `GetRoom`; Save `UpdateRoom`, Move `MoveRoom`, Delete `DeleteRoom`, Regen `RegenerateRooms`, Update `UpdateRooms`; Add/Remove exit, Add/Remove/MoveUp/MoveDown feature are no-write round trips |
| `/creator/players` | `PlayerList` (71) | CreatorOrAdmin | `ListPlayers` (`q`, `sort`, `page`, 50 per page) |
| `/creator/players/{nameKey}` | `PlayerEdit` (184) | CreatorOrAdmin | `GetPlayer`, `UpdatePlayer`; `PlayerRules` hides controls |
| `/creator/modules` | `Modules` (108) | CreatorOrAdmin (buttons admin only) | `ListModules`; `Start/Stop/RestartModule`; `TempData["Notice"]` |
| `/creator/terrain` | `TerrainList` (145) | CreatorOrAdmin | tabs + filters over fragments / features / phrases; Delete per row |
| `/creator/terrain/edit?kind=&id=` | `TerrainEdit` (194) + `TerrainLibrary.cs` (56) | CreatorOrAdmin | Get / Create / Update one library entity; `TempData["Notice"]` |

Plus `Pages/Shared/_Layout.cshtml`: a bare `<nav>`; **no CSS, no stylesheet, no responsive layout exists today.** 14 pages.

### 1.2 Services (`MUD.Web/Services`)

`AdminClient.cs` (`IAdminClient`, `GrpcAdminClient`, `CoreRefusedException`, `CoreUnavailableException`, `CoreSocket`), `SessionAuthentication.cs`
(cookie `mud.session`, `SessionAuthenticationHandler`, `SessionAccountService`), `LoginThrottle.cs`, `RoomCreation.cs`
(`RoomCreationService`, uses `MUD.Data.RoomRules`), `RoomMapLayout.cs`, `RoomRecord.cs`, `PlayerEditing.cs` (`PlayerRules`), `Account.cs`.
All of these stay as they are (they are not page-technology specific). `MUD.Web` keeps its `MUD.Data` reference (for `RoomRules`)
and `MUD.Contracts`; both are fine because everything runs on the server.

### 1.3 Tests (`MUD.Web.Tests`, 253 `[Fact]`/`[Theory]` methods, xunit 2.4.1, no Mongo)

| File | Methods | Classes | Fate |
|---|---|---|---|
| `AccountSessionTests.cs` | 41 | `FakeAdminClient`, `LoginPageTests`, `LogoutPageTests`, `SessionAuthenticationHandlerTests`, `SessionAccountServiceTests`, `PasswordPageTests`, `LoginThrottleTests`, `CoreSocketTests`, `NoMongoForAccountsTests`, `GrpcAdminClientSocketTests` | page tests port to logic classes (6.2); the rest stay |
| `CreatorTests.cs` | 12 | `CreatorPolicyTests`, `RoomFormValidationTests` | stay |
| `LibraryClientTests.cs` | 9 | `LibraryGrpcClientTests`, `FakeLibraryClientTests` | stay |
| `ModulesPageTests.cs` | 19 | `ModulesPageTests` | port |
| `NoDatabaseAccessTests.cs` | 4 | `NoDatabaseAccessTests` | stay, extend (9.4) |
| `PlayerEditTests.cs` | 35 | `PlayerRulesTests`, `PlayerPagesTests` | rules stay; pages port |
| `RoomMapTests.cs` | 34 | `RoomMapLayoutTests`, `CreatorMapPageTests`, `CreatorMapColourTests` | layout stays; page/colour port |
| `RoomPagesTests.cs` | 75 | `RoomCreationServiceTests`, `RoomValidatorParityTests`, `RoomEditPageTests`, `CreatorPageTests` | service/parity stay; page tests port |
| `TerrainPagesTests.cs` | 24 | `TerrainPagesTests` | port |

CI (`.github/workflows/ci.yml`): `ubuntu-latest`, `setup-dotnet 8.0.x`, `dotnet restore/build/test MUD.sln`, no workload.
Deploy: `scripts/deploy.sh` runs `dotnet publish MUD.Web/MUD.Web.csproj -c Release -o <release>/web` and checks `web/MUD.Web.dll`;
unit `planar-realms-web.service` runs `current/web/MUD.Web` on `127.0.0.1:5080`.

### 1.4 Environment

Local `dotnet` is `/usr/local/bin/dotnet` (SDK 8.0.128 under `/usr/share/dotnet`); `/usr/bin/dotnet` is a broken symlink to a missing
`/usr/lib/dotnet` (housekeeping, Open question 14). Blazor Server needs **no workload**: the local `wasm-tools` install is not used
and CI needs no extra step. The NuGet cache has no Fluent UI or bUnit package, so the first restore needs network; not exercised here.

## 2. Hosting model

One project. `MUD.Web` (`Microsoft.NET.Sdk.Web`, net8.0) becomes a Blazor Web App with interactive **server** rendering only:

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddRazorPages();                       // until the cutover card removes it
app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapRazorPages();                                    // until cutover
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
```

Project changes: add `Microsoft.FluentUI.AspNetCore.Components` (4.x line, pinned exact version **[verify net8.0 support]**). Nothing else is
added to `MUD.Web.csproj`; no new project in `MUD.sln` except the optional component-test project (9.1).

### 2.1 Render modes

- Everything is **InteractiveServer with prerendering off** (`new InteractiveServerRenderMode(prerender: false)`). Reasons: prerender would run
  every component's load twice (once static, once on circuit start) and so call core twice; the first paint is a loading skeleton either way; no SEO need.
- **Login is static SSR** (not interactive): it must set the cookie, which only a normal HTTP response can do. It carries `[ExcludeFromInteractiveRouting]`
  and `App.razor` picks the render mode per page (`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null`), so the rest of the app
  navigates inside one circuit without full page loads. **[verify]** this .NET 8 pattern in the spike.
- Logout is a **minimal endpoint** `POST /logout` with antiforgery (`app.MapPost("/logout", ...)`) that calls `IAdminClient.LogoutAsync`, deletes the cookie
  and redirects to `/login`. `GET /logout` redirects to `/` (as today). The nav's Log out is an HTML `<form method="post" action="/logout">`
  with `<AntiforgeryToken />`, a real browser POST rather than a circuit event. **[verify]** that the token component works on an interactive page in .NET 8; fallback:
  a button that first calls `LogoutAsync` in the circuit and then navigates with `forceLoad` to a `/logout/done` endpoint that only deletes the cookie (POST-only property then lost; Open question 8).
- `/` is a static endpoint redirect (as today `Index`). `/access-denied` is a routable component (no status-code change, see 8.3).
- Static assets of Fluent UI are served from the host (`_content/Microsoft.FluentUI.AspNetCore.Components/...`); **no CDN** (keeps the "no external requests" rule).
  The Blazor script `_framework/blazor.web.js` is served by the host too.

### 2.2 Side by side until cutover

During migration the Razor Pages owned the final URLs, so the Blazor components lived under a temporary prefix **`/next`** (`@page "/next/character"`, ...) and all
internal links were built from one constants class (now `WebRoutes`), whose prefix was `"/next"` during migration. After the cutover `WebRoutes.Prefix` is `""` and no `/next` route exists.
Both UIs share the same cookie, the same `IAdminClient` and the same policies, so a user can use either. The cutover card changes the prefix constant and every `@page`
(they use `@page Routes.X` constants where Razor allows, else a find/replace guarded by a route-table test), removes Razor Pages and deletes `_Layout`.
Per-page cutover (delete a Razor page and take its URL in the same PR) is allowed too (Open question 10).

## 3. Authentication and authorisation

### 3.1 Request vs circuit

The initial HTTP request (and the SignalR negotiate request) is authenticated by the existing `SessionAuthenticationHandler` (cookie -> `GetSession`).
A circuit lives longer than a request and has no `HttpContext` after start, so:

- `CircuitSession` (scoped; one per circuit) holds `Token`, `RemoteKey` (caller IP for `LoginThrottle`) and is filled once, at the start of the circuit, from
  `HttpContext` (cascading parameter of `App.razor`, or an `IHttpContextAccessor` read in a circuit handler during connection) **[verify]**. The token never reaches the browser's JS, and is not
  stored in component state that is rendered; components get it only through `CircuitSession`.
- `SessionAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider` (replaces the default). Initial state is the principal the handler built.
  `RevalidationInterval = 30 s` (**[verify]** choice). `ValidateAuthenticationStateAsync` calls `IAdminClient.GetSessionAsync(token)`:
  - null (logout, retire, demote, core restart): state becomes anonymous; components redirect to `/login` with `forceLoad` (D31, same as a stale cookie today).
  - role differs: state is rebuilt with the CURRENT role, so a lowered role removes creator/admin UI and policies within one interval;
  - `CoreUnavailableException`: keep the last state (do not sign the user out because core is restarting), mark `CoreStatus.Unavailable` for the shell banner.
  The existing request behaviour (core down means anonymous request, cookie kept) is unchanged for plain HTTP requests.
- Every core call that throws `CoreRefusedException` has the same effect as a failed revalidation (`SessionEnded`) or shows the "not allowed" view (role too low), and triggers an immediate revalidation.
- Core still re-checks the caller's current role on every call; the UI checks are for navigation only.

### 3.2 Authorisation per component

`AddCascadingAuthenticationState()`; `Routes.razor` uses `AuthorizeRouteView` with `NotAuthorized` showing the access-denied view (signed in) or redirecting to
`/login?ReturnUrl=...` (anonymous). Policies are the existing ones; `WebPolicies.CreatorOrAdmin` moves out of `Program.cs`'s top-level file so tests can reference it.

| Component | Attribute |
|---|---|
| Character, Password | `[Authorize]` |
| Creator, CreatorMap, RoomEdit, PlayerList, PlayerEdit, Modules, TerrainList, TerrainEdit | `[Authorize(Policy = WebPolicies.CreatorOrAdmin)]` |
| Module Start/Stop/Restart buttons | rendered in `<AuthorizeView Roles="Admin">`, and the handler re-checks the role (defence in depth; core is authoritative) |
| Login, access-denied | anonymous |

### 3.3 Login, logout, cookie

Unchanged mechanism and cookie (`mud.session`, HttpOnly, SameSite=Lax, Secure on HTTPS, browser-session). `Login.razor` is a static page with `<EditForm Model=... FormName="login" method="post">`
(`[SupplyParameterFromForm]`, antiforgery enforced by `UseAntiforgery`); its handler is `LoginModel.OnPostAsync` logic moved verbatim: remote IP from the connection, `WebAuthenticate`,
`AuthOk` appends the cookie and redirects (`NavigationManager.NavigateTo` in static SSR issues a redirect), `AuthRetired`, `AuthTryAgainLater` / core down, generic error after the 500 ms delay. The first interactive
request after login starts the circuit and reads the new cookie. Because the circuit captured the previous identity, a login or logout always ends in a **full page load** (redirect, not enhanced navigation) so no circuit
ever carries the previous user. Enhanced form handling is disabled on these forms (`data-enhance="false"`, default off) **[verify]**.

Plaintext password caveat (WEB.md) is unchanged: passwords exist only in the login and change-password forms, are never put in URLs, logs or component state beyond the submit, inputs carry `autocomplete` hints and never a value. Over plain HTTP they still cross the network
unencrypted, so the localhost bind stays; the password page runs over the SignalR circuit, so its content is subject to the same caveat. Antiforgery covers the plain-HTTP posts (login, logout); circuit events need no antiforgery token (they are not browser form posts and only the page's own scripts can reach the circuit; **no CORS and no cross-origin hub access are added**).

## 4. Component structure and mapping of existing logic

### 4.1 Pattern: logic classes plus thin components

To keep the existing tests useful, each PageModel's logic moves into a plain scoped class with no `HttpContext`, `ModelState`, `TempData` or `IActionResult`, and the component only binds and renders.
Naming: `<Page>Presenter` in `MUD.Web/Pages/`. Each takes `IAdminClient` and `CircuitSession`, exposes the same state the page model had (form fields, `Error`, `Notice`, `Rows`, flags), and returns an `Outcome` enum
(`Ok`, `NoChange`, `Invalid`, `NotFound`, `Denied`, `Unavailable`, `SignedOut`, ...) instead of an `IActionResult`. Message sentences move **verbatim** into `Messages.cs` constants (`LoginModel.GenericError`, `RetiredError`, `UnavailableError`, `RoomEditModel.RegenWarning`, ...).

| PageModel | New components | Notes |
|---|---|---|
| `Login` | `Login.razor` (static SSR) | uses `LoginPresenter`; only page that is not interactive |
| `Character` | `Character.razor` | greeting from `AuthenticationState`, HTML-encoded by Razor as today |
| `Password` | `Password.razor` + `PasswordPresenter` | throttle check, delay, confirmation; fields cleared after each submit |
| `Creator` | `Creator.razor` + `CreatorPresenter` + `ExitRows.razor` | area suggestions via `FluentAutocomplete`; on success `NavigateTo(Routes.RoomEdit(key))` |
| `CreatorMap` | `CreatorMap.razor` + `RoomMap.razor` (SVG + JS interop) + `MapToolbar.razor` | `?area=&z=` as `[SupplyParameterFromQuery]`; layout by unchanged `RoomMapLayout` |
| `RoomEdit` | `RoomEdit.razor` split into `RoomBasics`, `ExitRows` (shared with Creator), `FeatureRows`, `RoomMoveSection`, `RoomGeneratedSection`, `RoomDeleteSection` + `RoomEditPresenter` | add/remove/move rows are local state changes (no write, never leave the circuit) |
| `PlayerList` | `PlayerList.razor` + `ResponsiveList<T>` | filter / sort / page via query parameters |
| `PlayerEdit` | `PlayerEdit.razor` + `PlayerEditPresenter` | `PlayerRules` unchanged |
| `Modules` | `Modules.razor` + `ModulesPresenter` | `TempData["Notice"]` becomes `NoticeService` |
| `TerrainList` | `TerrainList.razor` + `TerrainListPresenter` | kind + id from route/query only |
| `TerrainEdit` | `TerrainEdit.razor` + `TerrainEditPresenter` | the presenter has no settable `Kind` or stored id (replaces the "exact bound-property list" test) |
| `AccessDenied` | `AccessDenied.razor` | same text |
| `_Layout` | `MainLayout.razor`, `NavShell.razor`, `ReconnectBanner.razor`, `NoticeHost.razor` | section 6 |

Shared UI: `ResponsiveList<T>` (grid >= 600px, card list below), `StickyActionBar`, `PageState` (loading / error / not found wrapper), `ConfirmDialog` (replaces the browser `confirm(...)` of Regen with the same text), `NoticeService` (scoped, lives with the circuit, shows once; lost on a full reload, unlike `TempData`, which is acceptable).

### 4.2 Rules that must not regress

- Route/query values are the only source of the room key, name key, kind and id (presenters have no setters for them; a reflection test pins this).
- Blank X/Y/Z is `null`, never 0. `UpdateRoom` still sends the whole form with `features_present`. Delete / Move / Regen / Update still take the route's key only.
- Last write wins (D30); no revision field. Dangling-exit confirmation, 32 exit / 64 feature caps, feature error prefix "Feature N:" unchanged.
- Admin calls still go to core which writes audit rows first; the web adds none.
- No exception text or submitted values are rendered; unhandled exceptions are caught by an `ErrorBoundary` per page (8.2).

## 5. Layout, navigation and responsive design (Fluent UI)

Package components used (confirm each in the pinned version **[verify]**): `FluentLayout`, `FluentHeader`, `FluentBodyContent`, `FluentNavMenu`/`FluentNavLink`, `FluentDialog`, `FluentMessageBar`,
`FluentProgressRing`, `FluentSkeleton`, `FluentCard`, `FluentDataGrid`, `FluentTextField`/`FluentTextArea`/`FluentSelect`/`FluentCheckbox`/`FluentAutocomplete`, `FluentAccordion`, `FluentButton`, `FluentToast`, and the `Fluent*Provider` components in `MainLayout`.

### 5.1 Breakpoints

| Class | Width | Layout |
|---|---|---|
| Every width | all | **one chrome**: a top bar (menu button, title, user name) and a pull-out menu from the left (5.2). No bottom tab bar, no "More" sheet, no left rail. Content max-width 1100px |

Only the page content (tables vs cards, grids) still changes at 600px.

Design baseline 360x640. Breakpoints are decided in CSS (`@media`) wherever possible, so layout never depends on a circuit round trip; where the markup itself must differ (grid vs card list) a `ViewportService`
reads `matchMedia` once through JS interop and subscribes to changes. Alternative that needs no interop: render both and hide one with CSS; chosen only for small lists (Open question 12).

### 5.2 Navigation

Same items and visibility as before the top bar: Character, Change password (signed in); Map, Creator, Players, Modules, Terrain (CreatorOrAdmin, listed first); Log out (a POST form, never a link). The list is `NavMenu.ItemsFor(creatorOrAdmin)` in `MUD.Web/Pages/NavMenu.cs`; `NavShell.razor` renders it.

- **Top bar** (`<header class="mud-topbar">`, sticky, at every width), left to right: a three-line menu button (44x44, `aria-label="Menu"`, `aria-haspopup="dialog"`, `aria-expanded`, `aria-controls="mud-menu"`), the title "Planar Realms", and on the far right the signed-in user's name as a link to `/character`. The name is one line and truncates with an ellipsis (`min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap`), so there is no horizontal scroll at 360px however long the name. **Anonymous visitors** (login page, access denied before sign-in) get the bar with the title only: no button, no name, no menu.
- **Pull-out menu**: `#mud-menu`, `role="dialog" aria-modal="true"` around a `<nav aria-label="Main">`, slides in from the left (`transform` transition, 0.25s; no animation under `prefers-reduced-motion`) over a dimmed backdrop. Closed, it is `visibility:hidden` so it is neither focusable nor read. Width `min(280px, 85vw)`.
- **Closes** on: backdrop tap, Escape, the "Close menu" button, a link click (`CloseForNavigation`) and any navigation (`LocationChanged`; the handler is detached in `Dispose`).
- **Focus** (`window.mudMenu` in `wwwroot/js/app.js`, called from `NavShell.OnAfterRenderAsync`): on open, focus moves to the first control in the menu and Tab / Shift+Tab wrap inside it; on close from the menu (Escape, backdrop, Close button) focus returns to the menu button. After a link click or navigation the page's `FocusOnNavigate` puts focus on the new heading instead, so focus is not pulled back to the button.
- Active link: weight, underline and a turquoise left edge (not colour alone); skip-to-content link; `aria-current` from `NavLink`. `<PageTitle>` per page, `<Title> - Planar Realms`.
- The reconnect banner now sits at the bottom edge (there is no bottom bar); the sticky action bar of forms sticks at `bottom: 0`.

### 5.3 Global phone rules (acceptance)

1. `<meta name="viewport" content="width=device-width, initial-scale=1">` in `App.razor`.
2. Every interactive element has a hit area >= **44 x 44 CSS px** and >= 8px spacing. Fluent's default control height is smaller **[verify]**; a global `mobile.css` raises it through control height tokens / `::part`.
3. **No horizontal page scroll at 360px.** `overflow-x:hidden` on body is not the fix; components must fit. Long tokens (keys, handles) use `overflow-wrap:anywhere`. Only the map canvas pans internally.
4. Body text >= 16px; inputs 16px (no iOS zoom on focus).
5. Forms end in a **sticky action bar** (Save / Cancel) above the bottom nav, full width on phone; destructive actions (Delete, Regen, Move) are in a separate "Danger zone" section.
6. Long pages use collapsible sections (`FluentAccordion`).
7. **Latency discipline** (server rendering means each event is a round trip): text inputs bind on `change` (blur), not `input`; only the player filter uses `input` with a 300 ms debounce; no per-frame or per-keystroke events; map gestures run in JS (below).
8. WCAG AA contrast; `prefers-reduced-motion` respected; colour scheme is Open question 9.

### 5.4 Patterns per page type

**Form** (login, password, create room, player edit, terrain edit, room sections): labels above inputs, single column on phone, two columns from 600px only for short fields (X/Y/Z, race/class/gender). Field errors under the field and a `FluentMessageBar` summary at the top, focused and `role="alert"` after a failed save. `inputmode="numeric"` for numbers. Password inputs carry `autocomplete` hints and no value.

**Repeating rows** (exits, features): each row is a card with labelled fields; Move up / Move down / Remove are 44px buttons in the card footer; Add buttons after the list; "3 of 32 exits" counters; the "separated by commas or spaces" help text stays.

**List / table** (players, modules, terrain library, unplaced rooms, map mismatches): `FluentDataGrid` from 600px; below that `ResponsiveList<T>` renders one card per row (title, label/value pairs, actions in a footer) from the same view model. Sort and filter collapse into one filter row; paging is Previous / Next with "Page N" (50 per page, unchanged). Terrain tabs become a `FluentSelect` plus count on phone. Status keeps words with the `[+]`/`[-]`/`[!]` prefixes (never colour alone).

**Map**: SVG from unchanged `RoomMapLayout`, rendered by the server. Room squares stay real `<a href>` links so a tap navigates even if JS fails. A small JS module `roommap.js` (imported through `IJSRuntime`, `touch-action:none` container) does pan (one pointer), pinch zoom (two), wheel/trackpad zoom and double-tap zoom by editing the SVG `viewBox`
entirely in the browser; **no event goes to the server during a gesture**. Toolbar (44px): zoom in/out, fit, Area and Level selectors (collapsible to an icon on phone). If a room square is under 44px at the current zoom, a tap selects the nearest room within 22px and shows a bottom sheet with "Open room". Unplaced and mismatch lists below as card lists. Keyboard: arrows pan, +/- zoom. Terrain colour stays in the hover title and the sheet text. Generated-room fill rules (exact `#RRGGBB` only, neutral grey fallback, colours fetched only when the level has a generated room) move unchanged from `CreatorMapModel`.

### 5.5 Per-page layout notes

| Page | Phone | Tablet/desktop |
|---|---|---|
| Login | centred card, stacked, full-width button | card max 420px |
| Character | greeting card | same |
| Password | stacked + sticky Save | same |
| Creator | stacked; exits as cards; sticky Save | X/Y/Z one row; exits 2-up |
| Map | full-bleed, collapsible toolbar | toolbar on top, unplaced/mismatch side panels |
| Room edit | accordion: Basics, Exits, Features, Generated (if any), Move, Delete; sticky Save | two columns: form + summary |
| Players | filter row + cards | grid |
| Player edit | stacked; role/retire only if allowed | two columns |
| Modules | cards, full-width action buttons (admin) | grid with Actions column |
| Terrain list | tab select + filters + cards | tabs + grid |
| Terrain edit | stacked; terrain checkboxes in scrollable fieldset max 40vh | two columns |

## 6. Dropped connections and lost circuits

Mobile connections drop (screen lock, network change, tab backgrounded). A Blazor Server circuit holds the component state in server memory: if the same circuit reconnects, state is intact; if it was evicted, everything unsaved is gone.

Decisions:

1. **Retention**: `CircuitOptions.DisconnectedCircuitRetentionPeriod = 20 minutes` (default 3), `DisconnectedCircuitMaxRetained = 50` (default 100); a handful of users makes memory a non-issue. **[verify]** exact option names in .NET 8.
   SignalR: `ClientTimeoutInterval` 60 s (default 30) and `KeepAliveInterval` 15 s so a brief radio stall does not drop the circuit; `HandshakeTimeout` unchanged. WebSockets with long-polling fallback stay enabled (a future reverse proxy must pass WebSockets).
2. **Reconnect UI**: replace the default modal with `ReconnectBanner` using the `components-reconnect-*` states: *retrying* shows a non-blocking bar "Connection lost, reconnecting..." and **disables Save, Delete and the other mutating buttons** (so a click cannot queue; built as one helper, `window.mudReconnectGuard` in `app.js`, which watches the banner's `components-reconnect-*` class and disables every `.mud-save` or `data-mutating` button, then re-enables only those it disabled; any new mutating button must carry one of the two markers); *failed* shows "Could not reconnect." with a 44px Retry button; *rejected* (circuit gone) shows "This page's session was lost. Reload to continue." with a Reload button, plus "Your unsaved edits were kept as a draft on this device" when a draft exists. The banner is styled in `mobile.css`, never hides the sticky bar, and respects safe-area insets. Auto reload on *rejected* is **not** done (it would discard the draft prompt); the user taps Reload.
3. **State in components**: all form state is component/presenter state (never static, never singleton), so reconnect to a retained circuit loses nothing. Row edits (exits, features) are local and cost no round trip, so they are not lost by a drop except when the circuit is gone.
4. **Local draft (for editors only)**: room edit, create room, player edit and terrain edit write their form state as JSON to browser `localStorage` (key `draft:<user>:<route>`, JS interop, debounced 2 s after the last change, plus on `visibilitychange` hidden, which is what fires when the phone is locked). Never stored: any password or login field. On load, if a draft exists and differs from what core returned, the page offers "Restore unsaved changes? / Discard". The draft is removed on successful save, on Discard and on logout; it expires after 24 h; it never overrides core's data without the user's choice. Last write wins (D30) still applies when the restored form is saved; the restore banner says the data may have changed in the meantime. Drafts are device-local plain text of data the user can already see; no secrets are in them.
5. **Session ended while disconnected**: revalidation (3.1) runs on reconnect; an ended session sends the user to `/login` with a `ReturnUrl`.
6. **Server restart (deploy)**: every circuit is lost; clients see *rejected*, and the draft mechanism covers the edit forms. The deploy script's restart is therefore visible to signed-in users; it is acceptable and already true of the Razor site's session loss on core restart.
7. **Concurrency**: one circuit per tab; two tabs are two circuits (each re-validates independently).

## 7. Error and loading states

### 7.1 Loading

- First load: a CSS-only splash in `App.razor` until the circuit connects (no WASM download; size is small, section 10.3).
- Data pages: `PageState` shows `FluentSkeleton` shaped like the content after 150 ms (avoid flicker), with `aria-busy`. Submit buttons show a spinner and are disabled while a call is in flight (prevents double submit; also protects against the round-trip delay). The sticky bar stays visible.
- Map: skeleton first, colours fill in afterwards without moving the map.

### 7.2 Errors and notices (every existing sentence preserved verbatim in `Messages.cs`)

| Situation | Behaviour |
|---|---|
| Core down (`CoreUnavailableException`) | the existing "game is not reachable" sentence; page-level message bar with Retry for reads, inline error with input kept for writes |
| `CoreRefusedException` session ended | revalidate and go to `/login` (forced full load) |
| `CoreRefusedException` role too low / policy fails | the existing "not allowed" view / sentence |
| Entity not found | the existing not-found view with a link back to the list |
| `Invalid` | core's field errors in the form and summary; input kept (as today) |
| `NoChange`, `AlreadyExists`, `Refused`, `AuditFailed`, `Forbidden`, `LastAdmin`, `Failed` | existing fixed sentences |
| Success | existing notices ("Saved.", module notice, generation counts) via `NoticeService`, shown once after the navigation that follows |
| Unhandled exception in a component | `ErrorBoundary` per page: "Something went wrong. Reload the page." with Reload; details only in the server log (detailed circuit errors stay off; no exception text rendered) |
| Browser without JavaScript | the Blazor Server UI cannot work; `<noscript>` says the site needs JavaScript. Login still works (static) |

### 7.3 Behaviour changes to accept (stated, not hidden)

- **HTTP status codes**: today a missing room, a refused role and an unreachable core produce 404 / 403 / 503 pages. In an interactive circuit the page is already streamed or rendered over SignalR, so these become in-page views with HTTP 200 (the access-denied route still shows its page). Behaviour users see is the same; scripted callers would notice. Only static endpoints (login, logout, `/`) keep real redirects/status.
- `TempData` notices do not survive a hard reload.
- Two actions that used to be page round trips are now in-circuit events: a user can see a different "Online"/data state without a navigation; data is still loaded fresh by each page.
- The Regen browser `confirm()` becomes a Fluent dialog with the same text.

## 8. Test strategy

### 8.1 Projects

Existing `MUD.Web.Tests` keeps xunit and the `FakeAdminClient`. Add **bUnit** (`bunit`, net8-compatible **[verify]**; it supports xunit 2.x) there rather than a new project if package versions allow;
otherwise a new `MUD.Web.Components.Tests`. Fluent components need `Services.AddFluentUIComponents()` and `JSInterop.Mode = Loose` **[verify]**.

### 8.2 PageModel tests port to presenter tests

Because logic moves into `*Presenter` classes (4.1) that take `IAdminClient` and `CircuitSession`, the existing PageModel tests port by replacing the page-model construction, `ModelState`, `TempData` and `IActionResult`
assertions with presenter state and `Outcome` assertions; the arrangement over `FakeAdminClient` and the expectations (messages, calls made, "core not asked when locked out") stay. Each of the 253 tests is ported, kept, moved to bUnit, or deleted with a reason in the PR description. Required additions:

- `SessionAuthenticationStateProvider`: revalidation returns anonymous for a null session, rebuilds the principal on a role change, keeps state when core is unavailable, and calls `GetSession` with the circuit's token;
- `CircuitSession` is filled once and never shared across circuits;
- login (static) handler: cookie attributes, generic message for bad credentials and throttled address, delay path, retired and unavailable messages; logout endpoint POST-only, antiforgery required, GET redirects;
- a route table test: every component route and its authorisation attribute (so a page cannot be added unprotected);
- a `Messages` pin test (every user-visible sentence), replacing the strings that used to sit in page models.

### 8.3 bUnit component tests (fits)

Rendering with a fake auth state: nav items per role (phone and desktop variants), role-conditional controls (admin-only module buttons, `PlayerRules`), grid vs card switch with a fake viewport, add/remove/move exit and feature rows, `PageState` transitions, reconnect banner state texts, confirm dialog on Regen, draft restore prompt (fake JS interop), notice shown once.
bUnit does **not** cover pixel layout, 44px targets, horizontal scroll or touch gestures: those are the manual checklist (Appendix A). Playwright would automate them but adds a browser download to CI (Open question 11).

### 8.4 Guard tests

`NoDatabaseAccessTests` keeps its four checks and **must be extended to scan `*.razor` files** (its current pattern `*.cs*` does not match them) for `MUD_MONGO_URI`, `MongoDB`, `IMongoClient`. The service-unit check stays.

### 8.5 Not run

No Mongo and no browser here: nothing in this design was exercised against a live core or a real phone. The map gesture module and the reconnect behaviour can only be verified on a device.

## 9. CI and deploy impact

- **wasm-tools / workload**: not needed. The earlier WASM idea would have; this design removes the question. CI stays `setup-dotnet 8.0.x` + restore/build/test; no workflow change except bUnit/Fluent packages restoring from NuGet (network at restore, as for existing packages).
- **Publish**: `scripts/deploy.sh` is unchanged: `dotnet publish MUD.Web -o <release>/web`; Fluent's static web assets land in `web/wwwroot/_content/...` via the publish manifest. Add one sanity line to the script
  (check `web/wwwroot/_content/Microsoft.FluentUI.AspNetCore.Components` exists, **[verify]** path) in the cutover or shell card. Unit, port, env files unchanged.
- **Runtime**: the host now holds a SignalR hub (`/_blazor`) and per-circuit memory (a few hundred KB each); with the user count here that is negligible. A future reverse proxy must forward WebSockets (and `Upgrade` headers).
- **Size / first load**: no framework download; first load is the Fluent CSS/JS bundle plus `blazor.web.js` (estimate well under 1 MB compressed **[verify]**). Targets, to be measured in the spike: first load <= 1.5 MB transferred, interactive in <= 3 s on a mid-range phone on 4G, repeat load <= 200 KB. Each interaction costs a round trip; on a phone the budget is therefore latency-bound (aim for <= 150 ms per event at the server, network excluded).
- `MUD.sln` gets no new web project (and one test project only if 8.1 requires).

## 10. Migration order

Each step is its own card/PR with tests; Razor Pages stay default and working until step 7.

0. **Spike**: add the Fluent package and bUnit on a branch; confirm every **[verify]** (net8 versions, per-page render mode with a static login, `AntiforgeryToken` in an interactive layout, circuit token capture, `RevalidatingServerAuthenticationStateProvider`, circuit options names, 44px token overrides, `matchMedia` interop); measure first-load size; record results in this file. Gate for step 1.
1. **Shell**: `App.razor`, `Routes.razor`, `MainLayout`, nav shell (phone/tablet/desktop), `CircuitSession`, auth state provider with revalidation, `ReconnectBanner`, `NoticeService`, `PageState`, `ConfirmDialog`, `Messages.cs`, `Routes` prefix `/next` (migration only; now empty), `mobile.css`, route-table test. Touches `Program.cs` and `MUD.Web.csproj` (collision hotspots: do not run in parallel with other web cards).
2. **Account pages**: Login (static), logout endpoint, Character, Password, AccessDenied.
3. **Read pages**: Modules, PlayerList, CreatorMap with `roommap.js`.
4. **Forms**: Creator, PlayerEdit, TerrainList, TerrainEdit; draft save component (6.4) lands with the first editor.
5. **RoomEdit** (largest: 463-line model, five forms).
6. **Review gate**: manual phone checklist (Appendix A) on a device, soak period decided by the owner.
7. **Cutover**: change the prefix constant / `@page` routes to the real URLs; remove `AddRazorPages`, `MapRazorPages`, `Pages/*.cshtml*`, `_Layout`, and the Razor page tests; update WEB.md and DEPLOYMENT.md; run the checklist again. Razor deletion is its own PR, merged only after the soak.
8. Follow-ups (not in scope): Playwright, HTTPS, offline/PWA.

Steps 2-5 can partly parallelise after step 1 (distinct files); presenter/service extraction happens inside each page's card.

## 11. Rollback plan

- **Before cutover**: Razor Pages are still the default URLs; rolling back meant not using `/next` (historical; the prefix is now removed). A bad shell merge is reverted via `git revert` on `hermes`.
- **At and after cutover**: `scripts/deploy.sh --rollback` switches `current` to the previous release (which has the Razor UI) and restarts the units. Keep at least the last Razor-era release (`MUD_KEEP_RELEASES` >= 3 around cutover). The restart is the operator's action at that time, not this card's.
- **After Razor deletion**: `git revert` of the deletion PR plus a redeploy, or `--rollback`.
- No change to core, the gRPC contract, units, ports or env files, so a web rollback never affects the game or telnet. The cookie name and format do not change, so sessions survive a rollback in either direction (core owns the session).

## 12. Out of scope (separate work)

Password hashing; HTTPS / reverse proxy; the `frontends.sock` token (P7-1); a game-output page; audit log viewer; player self-retire; offline/PWA; sample-room preview in the terrain editor; reverse-exit creation; any change to `admin.proto` or core.

## 13. Open questions

None of these was silently decided beyond the defaults named in the referenced sections.

1. **Phone reachability vs localhost-only.** A phone cannot reach `127.0.0.1` on the server, and plaintext passwords must not cross a LAN over HTTP. What is the access path (VPN, SSH tunnel, LAN bind plus HTTPS reverse proxy that forwards WebSockets)? This spec keeps the bind unchanged and does not solve it.
2. **Fluent UI version and components** (5.): which 4.x version supports net8.0 and has every named component; how to reach 44px targets (tokens vs CSS parts); license/telemetry terms. Resolved by the spike.
3. **Prerender off** (2.1): confirm (avoids double core calls, shows a skeleton on first paint).
4. **Revalidation interval and circuit retention** (3.1, 6.1): 30 s and 20 min / 50 circuits proposed; confirm or adjust.
5. **Core down while a circuit is open**: this spec keeps the last identity and shows a banner rather than signing the user out (differs from the per-request "anonymous but cookie kept" only in that the page does not change). Confirm.
6. **Draft storage** (6.4): `localStorage` for 24 h on the device (survives tab discard, but persists on a shared phone until logout/expiry) vs `sessionStorage`. Which drafts at all: the four editors only. Confirm.
7. **`ReturnUrl` after login**: today ignored (always `/character`). Honour it (new; must be validated as a local path) or keep ignoring?
8. **Logout mechanics** (2.1): POST form with antiforgery token (preferred) vs the fallback; confirm after the spike.
9. **Colour scheme**: light only, system preference, or toggle (the current site has no styling).
10. **Cutover style**: decided and done as the `/next` prefix followed by one cutover card (Appendix E); per-page cutover was not used.
11. **Automated browser tests**: add Playwright to CI or accept the manual phone checklist only?
12. **Grid/card switch**: JS-interop `ViewportService` vs rendering both and hiding one in CSS.
13. **HTTP status codes** (7.3): accept that in-circuit 403/404/503 views are HTTP 200.
14. **Broken `/usr/bin/dotnet` symlink** on the maintainer machine (housekeeping, not part of this work).
15. **Which of the 253 tests may be dropped** as obsolete (page binding mechanics) vs must be ported: each decision to be listed in the porting PRs.
16. **Deploy restarts** drop every circuit (6.6): acceptable for a localhost, few-user site?

## Appendix A. Manual phone checklist (acceptance for layout cards)

At 360x640 (Chrome device emulation and one real phone), every page: no horizontal scroll (also with long keys and names); all controls >= 44px and not touching; text readable without zoom; inputs do not zoom on focus; the menu button and pull-out menu work with one thumb; Save reachable without scrolling to the top; the on-screen keyboard does not hide the focused field or the sticky bar; landscape works; map: one-finger pan, two-finger pinch, double-tap zoom, zoom buttons, tap a room opens it, the page does not scroll while panning;
**connection tests**: lock the phone for 30 s and unlock (the edit form keeps its input), switch Wi-Fi to mobile data mid-edit, stop and start the web unit and confirm the "session was lost" banner and the draft restore prompt; error states (core stopped) show the fixed sentences. Repeat at 768px and 1280px.

## Appendix B. Room pages as built (card t_fd0c7a00)

Components: `/creator` (`Creator.razor`), `/creator/map` (`CreatorMap.razor`, `?area=&z=`), `/creator/rooms/{Key}` (`RoomEdit.razor`). All use `CreatorOrAdmin`.
Logic is in `CreatorPresenter`, `CreatorMapPresenter` and `RoomEditPresenter` (moved from the page models; same calls, same refusal mapping). Sentences are in `Messages.cs`.
Shared: `ExitRows.razor` (exit cards), `RoomMap.razor` + `wwwroot/js/roommap.js` (viewBox pan, pinch, wheel, double-tap, keyboard; a tap on a room smaller than 44px selects the nearest room and shows "Open room"; room squares stay real links), `NoticeService` (replaces TempData), `DraftStore` + `window.mudDraft` in `app.js` (localStorage, 24 h, key `draft:<user>:<route>`, cleared on logout).
Decisions: status codes become in-page views (7.3); Regen's confirm is an in-page confirmation showing `Messages.RegenWarning` (not a browser dialog, so no script string quoting); row edits (add/remove/move exit and feature) are local and call no core method; inputs bind on change, not per keystroke; a draft is offered only when it differs from what core returned. Not verified here: touch gestures, reconnect behaviour and the 360px layout need a real device (Appendix A); there is no bUnit, so components are covered by presenter tests plus source/reflection guards.

## Appendix C. Player and module pages as built (card t_459edd31)

Components: `/creator/players` (`PlayerList.razor`, `?q=&sort=&page=`), `/creator/players/{NameKey}` (`PlayerEdit.razor`), `/creator/modules` (`Modules.razor`). All use `CreatorOrAdmin`; the shell nav now links to them.
Logic is in `PlayerListPresenter`, `PlayerEditPresenter` (editor role passed in; `PlayerRules` unchanged) and `ModulesPresenter`. Sentences are in `Messages.cs`.
Lists render a table and a stacked-card list from the same rows; CSS shows the table from 600px and the cards below (no round trip, Open question 12 option "render both, hide one").
Decisions: the module notice stays beside the refreshed list (no `NoticeService` hop, there is no navigation); Start goes straight through, Stop and Restart ask first in `ConfirmDialog`; newly retiring a character asks first (new, `Messages.RetirePlayerConfirm`); player edit keeps a local draft like the room editor (restore never takes a role or retired value the editor may not set); HTTP 403/404/503 become in-page views (7.3).
New text (not moved from Razor): `RetirePlayerConfirm`, `StopModuleConfirm`, `RestartModuleConfirm`, `PlayerDraftFound`. Not verified here: layout at 360px, touch targets, reconnect behaviour and a signed-in circuit (no core or browser in the sandbox).

## Appendix D. Terrain library pages as built (card t_9011d43a)

Components: `/creator/terrain` (`TerrainList.razor`, `?tab=&terrain=&tag=`) and `/creator/terrain/edit?kind=&id=` (`TerrainEdit.razor`). Both use `CreatorOrAdmin`; the shell Terrain link now points at them.
Logic is in `TerrainListPresenter` and `TerrainEditPresenter`; `TerrainLibrary.cs` (helpers and the error sentences) is shared with the Razor pages until cutover. Kind and id come from the query string only; the presenter has no setter for either, and an edit writes the original id, never the form's.
Lists render a table and a card list from the same rows (CSS, 600px). The four tabs are links from 600px and one `<select>` below. The terrain tick list is a scrollable box (max 40vh) on a phone and a three-column grid from 600px. The empty-terrains warning (`[!] Not used by any generator`) shows on the editor and in list rows exactly as before.
Decisions: Delete asks first in `ConfirmDialog` (new text `TerrainDeleteConfirm`); the editor keeps a local draft like the room and player editors (`TerrainDraftFound`); a saved new entry navigates to its edit address with the "Saved." notice via `NoticeService`; a saved edit reloads from core. HTTP 403/404/503 become in-page views (7.3).
Not verified here: layout at 360px, touch targets, reconnect behaviour and a signed-in circuit (no core, Mongo or browser in the sandbox).

## Appendix E. Cutover and verification (card t_2375c78f)

Cutover: `Pages/*.cshtml`, their `.cshtml.cs` page models and `Pages/Shared/_Layout.cshtml` are deleted; `AddRazorPages`/`MapRazorPages` are gone; `@page`
routes lost the `/next` prefix and `WebRoutes.Prefix` is `""` (`WebRoutes.Home` is `/`). The presenters stay in `MUD.Web/Pages/` (namespace `MUD.Web.Pages`);
`FeatureInput` moved from the deleted `RoomEdit.cshtml.cs` into `RoomEditPresenter.cs` unchanged. Same URLs as before: /login, /character, /creator, /creator/map,
/creator/rooms/{key}, /creator/players[/{nameKey}], /creator/modules, /creator/terrain[/edit], /account/password, /access-denied; `POST /logout` is the minimal endpoint
(GET redirects). `scripts/deploy.sh` and the unit are unchanged (publish output contains the Fluent static assets under `wwwroot/_content`). CI needed no change (no workload).

Tests: the Razor page-model test classes (`LoginPageTests`, `LogoutPageTests`, `PasswordPageTests`, `ModulesPageTests`, `PlayerPagesTests`, `RoomEditPageTests`,
`CreatorPageTests`, `CreatorMapPageTests`, `CreatorMapColourTests`, `TerrainPagesTests`) were deleted because each has a presenter/endpoint counterpart ported earlier
(`LoginPresenterTests`, `PasswordPresenterTests`, `LogoutEndpointTests`, `ModulesPresenterTests`, `PlayerEditPresenterTests`, `RoomEditPresenterTests`,
`CreatorPresenterTests`, `CreatorMapPresenterTests`, `TerrainPresenterTests`). Parity assertions against the deleted models now pin the literal sentences. Services, rules,
layout, policy, gRPC and guard tests are untouched. A new test asserts no `.cshtml` remains and the real routes.

Measured with headless Chromium (Playwright) against the published Release build and a stand-in core on a Unix socket (no real core or Mongo; room, player and
module data were canned). Results and screenshots are attached to the PR. Not verified: a real phone, real touch gestures on the map (pinch, one-finger pan), the
core-down banner, a role lowered mid-circuit, throttled networks.

## Appendix F. Site chrome, player page and palette (card t_2e8bcd61)

**Chrome**: see 5.1 and 5.2. Files: `Components/Layout/NavShell.razor` (bar + menu), `MainLayout.razor` (no `FluentLayout`/`FluentHeader` any more), `Pages/NavMenu.cs`, `wwwroot/css/mobile.css`, `wwwroot/js/app.js` (`mudMenu`).

**Player page** (`/character`, `Character.razor` + `CharacterPresenter`): the signed-in player's OWN record. It calls a new read-only Admin rpc, `GetOwnPlayer(TokenRequest) returns (OwnPlayerReply)` (`GetPlayer` is creator-only and takes a name; see ADMIN_API.md). Core picks the player from the token and the request has no name field, so no other player can be asked for. Shown, in a `FluentCard` around a definition list (one column on a phone; label/value columns from 600px): name, role, race, gender, class, current location (room key), short description, long description. Chosen from the real model: these are the fields the player edit page shows plus role and location, and all are safe for the owner. Never in the reply: password, hash, session token, inventory, retired flag (a retired player cannot be signed in), other players. Core down shows `Messages.Unavailable` (the existing core-down sentence); a refused token shows `Messages.CharacterRefused`.

**Palette** (all in `mobile.css`, defined once as custom properties; no hex outside the `:root` block): `--mud-red #931621`, `--mud-teal-dark #28464B`, `--mud-teal #326771`, `--mud-cyan #2C8C99`, `--mud-turquoise #42D9C8`. Fluent design tokens are mapped to them (`--accent-base-color`, `--accent-fill-*`, `--accent-foreground-*`, `--focus-stroke-*`, `--error`). Roles: top bar and menu `--mud-teal-dark` with white text, menu hover `--mud-teal`, focus ring turquoise on the dark bar and menu (dark ring with turquoise halo on light surfaces), destructive buttons, errors and the regen warning `--mud-red`, links and primary buttons the darkened cyan below, page background `#F4F7F8`, body text `#1B2B2E`. Map terrain colours (from the legends) are untouched. Dark mode is out of scope.

**Contrast** (WCAG 2.x, checked by script and pinned in `SiteChromeMarkupTests`). Two palette colours fail on their own, so shades are used:
- Raw cyan `#2C8C99` is 3.95:1 on white, below 4.5:1 for text and for white-on-cyan buttons. Links, primary buttons and the accent use `--mud-cyan-ink #216E79` (5.88:1 on white, 5.46:1 on the page) and `--mud-cyan-deep #1B5C66` for hover (7.04:1). Raw cyan stays defined but is not used for text or fills.
- Raw turquoise `#42D9C8` is 1.75:1 on white, so it is not a focus ring on light surfaces; there the ring is `--mud-teal-dark` (10.16:1) with a turquoise halo. On the dark bar and menu turquoise is 5.80:1 and is used directly.
- The border colour is the shade `#6F878C` (3.53:1 on the page, 3.80:1 on white; UI components need 3:1).

| Pair | Ratio | Needs |
|---|---|---|
| white on `#28464B` (bar, menu) | 10.16 | 4.5 |
| white on `#326771` (menu hover) | 6.34 | 4.5 |
| `#42D9C8` on `#28464B` (name hover, focus) | 5.80 | 4.5 |
| `#216E79` on white / on `#F4F7F8` (links, primary fill) | 5.88 / 5.46 | 4.5 |
| `#1B5C66` on `#F4F7F8` (link hover) | 7.04 | 4.5 |
| white on `#1B5C66` (primary hover) | 7.58 | 4.5 |
| white on `#931621` (danger button) | 8.83 | 4.5 |
| `#931621` on `#F4F7F8` / white (errors) | 8.21 / 8.83 | 4.5 |
| `#1B2B2E` on `#F4F7F8` / white (body) | 13.63 / 14.67 | 4.5 |
| `#4A5A5E` on `#F4F7F8` / white (hints, card labels) | 6.69 / 7.20 | 4.5 |
| `#1B2B2E` on `#FFF4CE` (warning band) | 13.33 | 4.5 |
| `#0E700E` on `#F4F7F8` (ok text) | 5.84 | 4.5 |
| `#28464B` on `#42D9C8` | 5.80 | 4.5 |
| `#28464B` on white (focus ring) | 10.16 | 3 |
| `#6F878C` on `#F4F7F8` / white (control border) | 3.53 / 3.80 | 3 |

**Verified** with headless Chromium (Playwright) at 360x640, 768x1024 and 1280x800 against a stand-in core (canned data, no Mongo): anonymous bar (title only), player and creator menus (links as above), 44x44 button, aria-expanded, focus into the menu and back to the button on Escape and backdrop, Tab and Shift+Tab trapped, link click closes and navigates, log-out form is a POST with a token, no horizontal scroll, a 48-character name truncates with an ellipsis. **Not verified**: a real phone and touch, a screen reader, a real core and Mongo, the visual result of every other page under the new palette beyond the character page and the menu, and the Fluent components' own colours (the pages still use the plain `mud-*` controls; only `FluentCard` is new).
