# V2 spike results: gRPC over Unix domain sockets

Throwaway spike (owner decision D38) run on 2026-10-25, branch `v2-spike`, base
`hermes` at `599721a`. All experiment code lived outside the repo
(`~/.hermes/cache/scratch/v2-spike-code`) and is NOT committed. This file is the
only artifact. Numbers below are what actually ran; anything not run is listed
under "Not run" for each question.

## Environment

- Machine: Intel Core i7 960 @ 3.20 GHz (4 cores / 8 threads), 30 GB RAM, Linux
  5.15.0-191-generic.
- IMPORTANT noise caveat: this is a shared desktop. Load average was 6.6-7.0 on
  8 threads during the benchmark (browser, Discord, other processes at 80-150% CPU).
  Latency tails below are inflated by this and by the very old CPU. Treat them as
  an upper-ish bound for this box, not as a clean-room figure. No re-run on a
  quiet machine was done.
- .NET: `/usr/bin/dotnet` is a host stub. A real SDK exists at `~/.dotnet8`
  (SDK 8.0.425, runtime Microsoft.NETCore.App 8.0.31, Microsoft.AspNetCore.App
  8.0.31). Used via
  `export DOTNET_ROOT=$HOME/.dotnet8 PATH=$HOME/.dotnet8:$PATH`.
  `~/.dotnet31` also exists; not used. MUD.Main on `hermes` targets `net8.0`.
- Packages (all from nuget.org, restore worked): Grpc.AspNetCore 2.84.0,
  Grpc.Net.Client 2.84.0, Grpc.Tools 2.84.0, Google.Protobuf 3.36.2,
  Grpc.Core.Api 2.84.0.
- Spike solution: `Contracts` (classlib, one `.proto`, `<Protobuf ... GrpcServices="Both"/>`),
  `Core` (`Microsoft.NET.Sdk.Web`, Kestrel), `Module` (console, Grpc.Net.Client).
  The proto has two services: `Modules` (Hello, Subscribe server-stream, SubmitVerdict,
  Ping, Report) and `Frontends` (Who).

## Summary

| # | Question | Verdict |
|---|---|---|
| 1 | Kestrel on two UDS endpoints, different services, 0600, stale sockets | VERIFIED, with two gotchas (permissions need explicit handling; stale/leftover socket files need explicit deletion) |
| 2 | Plain console host (MUD.Main) hosting Kestrel/gRPC | VERIFIED on a repo copy: needs a FrameworkReference, a package, and a DI version bump |
| 3 | Grpc.Tools codegen with `dotnet build`; CI needs | PARTIAL: works locally; CI not run, only inspected |
| 4 | Grpc.Net.Client over UDS, server-stream + unary | VERIFIED |
| 5 | Latency | VERIFIED as measured, noisy machine; see numbers |
| 6 | Process supervision basics | VERIFIED, with one important finding about graceful core shutdown |

## Q1. Two UDS endpoints in one process

Method: `Core` calls `ListenUnixSocket` twice (`modules.sock`, `frontends.sock`),
both `HttpProtocols.Http2`, one `AddGrpc()`, both services mapped. A connection
middleware tags each connection with its endpoint name; an HTTP middleware returns
404 when a `/mud.Modules/` call arrives on the frontends socket or `/mud.Frontends/`
on the modules socket. A separate `Module probe` client calls each service on each
socket.

Commands (spike code, scripts `q1.sh`):

    dotnet Core.dll perms <dir> x default|umask|chmod
    dotnet Core.dll listen <dir> x            # then Module probe <sock> modules|frontends

Results:

- Both sockets listen in one process. Observed probe results:
  - modules.sock + Modules service: OK
  - modules.sock + Frontends service: `Unimplemented`, HTTP 404
  - frontends.sock + Frontends service: OK
  - frontends.sock + Modules service: `Unimplemented`, HTTP 404
  So separation per D22 works, but it is done by my middleware. I did not run the
  host without the middleware; I expect `MapGrpcService` alone to expose both
  services on both endpoints (not verified). Alternatives not tried: separate hosts
  or endpoint-metadata based routing.
- File permissions: Kestrel creates the socket honoring the process umask.
  Shell umask 0002 gave mode `775` (default, NOT acceptable).
  Setting `umask(077)` around startup gave `700`. `File.SetUnixFileMode(...,
  UserRead|UserWrite)` after `StartAsync` gave `600`. So 0600 is achievable
  but is not the default; the chmod approach has a short window after bind
  where the file has the umask-derived mode. umask(077) before bind removes the
  window (mode 700, which is owner-only but includes execute; the socket's x bit
  is irrelevant for connect, chmod gives exact 0600). A restrictive parent
  directory (0700) is the other layer; not tested separately.
- Stale socket handling: after `kill -9` of the core, both `.sock` files remain.
  Restarting without cleanup FAILED: `AddressInUseException: Address already in
  use` for `modules.sock`. Deleting the `.sock` files before starting worked and a
  fresh client connected. So core must unlink stale socket files itself before
  binding (D20 already has "core checks for stale module processes at start").
  Not tested: distinguishing a stale file from a socket still held by a live
  second core instance (a real implementation should probe-connect before
  deleting).
- Clean shutdown: in `q1.sh` I sent SIGINT and listed the directory 2 s later; both
  socket files were still present. Inconclusive, because the core may still have been
  inside its shutdown timeout (see Q6) at that moment. Not re-checked. Core should
  unlink on exit anyway and still tolerate leftovers.

Not run: non-Linux platforms; abstract-namespace sockets; behaviour with
long socket paths (the 108-byte `sun_path` limit; paths here were short enough
but the deployed path length matters and was not checked).

## Q2. Plain console host and Kestrel (copy of repo, not the real tree)

Method: `git archive HEAD | tar -x` into `repocopy/` (scratch). Baseline first,
then modified `MUD.Main.csproj` in the copy only, added a `MUD.Contracts` project
to the copy's `MUD.sln`, and a probe class in MUD.Main that builds a
`WebApplication` with `ListenUnixSocket` and references the generated
`Modules.ModulesBase`.

Baseline (unmodified copy): `dotnet build MUD.sln` 0 errors (8 warnings, ~2m18 cold),
tests: MUD.Tests 194 passed, MUD.Web.Tests 34 passed.

Changes needed in MUD.Main.csproj (Sdk stays `Microsoft.NET.Sdk`):

    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="..\MUD.Contracts\MUD.Contracts.csproj" />
    <PackageReference Include="Grpc.AspNetCore" Version="2.84.0" />
    <!-- and bump the existing reference: -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />

- The first attempt without the DI bump FAILED at restore with NU1605 (package
  downgrade): Grpc.AspNetCore pulls Microsoft.Extensions.DependencyInjection >= 8.0.1
  while MUD.Main pins 8.0.0 (warnings are errors for NU1605). Bumping to 8.0.1 fixed it.
- Switching to `Microsoft.NET.Sdk.Web` was NOT tried; the FrameworkReference route
  works and keeps the console project style. Not tested whether Sdk.Web would
  change `appsettings.json` or content handling.
- After the changes: `dotnet build MUD.sln` 0 errors (4 warnings); tests unchanged:
  194 + 34 passed. `Main` was not restructured. The probe compiled, so Kestrel types
  resolve from the console project. The probe was NOT executed inside MUD.Main (the
  real Main needs Mongo, which is unavailable here); hosting Kestrel was executed in
  the separate `Core` spike project only.
- Output `runtimeconfig.json` now lists both Microsoft.NETCore.App and
  Microsoft.AspNetCore.App; a shared-framework deployment needs the ASP.NET runtime
  on the host. `build.ps1`'s `dotnet publish ./MUD.sln --self-contained -r linux-x64`
  was run on the modified copy and succeeded (~3m47); MUD.Main publish folder is 115 MB
  and contains the Kestrel and Grpc.AspNetCore.Server assemblies. I did not
  compare against the pre-change publish size.
- Note MUD.Web in the same solution already uses `Microsoft.NET.Sdk.Web`, so CI
  and publish already handle ASP.NET Core.
- Real tree: untouched (`git status` clean apart from this docs change).

Not run: MUD.Main starting up with Kestrel (needs Mongo); systemd unit changes
(`planar-realms.service`) for the ASP.NET runtime; the Mongo-dependent paths.

## Q3. Grpc.Tools codegen and CI

Method and result: `Contracts.csproj` references Grpc.Tools 2.84.0
(`PrivateAssets=all`) and `<Protobuf Include="proto/mud.proto" GrpcServices="Both" />`.
`dotnet build -c Release` generated `obj/Release/net8.0/proto/Mud.cs` and
`MudGrpc.cs` with no extra steps. The same project, copied into the repo copy and
added to `MUD.sln`, built through `dotnet build MUD.sln` and
`dotnet publish MUD.sln`. Grpc.Tools ships its own `protoc` and
`grpc_csharp_plugin` for linux_x64 in the NuGet package (confirmed in
`~/.nuget/packages/grpc.tools/*/tools/linux_x64`), so no system protoc is needed.

CI inspection (read, not executed):

- `.github/workflows/ci.yml` (hermes): `ubuntu-latest`, `actions/setup-dotnet@v4`
  with `8.0.x`, then `dotnet restore MUD.sln`, `dotnet build MUD.sln --no-restore`,
  conditional `dotnet test MUD.sln --no-build`. Codegen is part of build, so this
  flow should work provided restore can reach nuget.org (it did here).
- `build.ps1` is a single line: `dotnet publish ./MUD.sln --self-contained -r linux-x64`.
  `appveyor.yml` (image `Ubuntu`, master branch only) restores then runs it.
- Verdict PARTIAL: I cannot verify the GitHub runner image or the AppVeyor Ubuntu
  image actually run it, because no CI run was made (spike is local; the diff
  rules forbid adding projects to the solution). Risk points to check when the
  contracts project lands: runner architecture must be x64 or arm64 supported by
  Grpc.Tools (it is for both), NuGet access, and that restore uses a runtime
  identifier `linux-x64` with `--self-contained` (worked in the copy).
- Local gotcha: the first `dotnet restore MUD.sln` after adding Grpc.AspNetCore to
  MUD.Main failed on NU1605 (see Q2) so CI would also fail until the DI
  version is aligned.

## Q4. Client over UDS (Grpc.Net.Client + ConnectCallback)

Method: `GrpcChannel.ForAddress("http://localhost", { HttpHandler = new
SocketsHttpHandler { ConnectCallback = ... UnixDomainSocketEndPoint ... } })`.
Module flow: `Hello` (token from env `MUD_TOKEN`) -> `Subscribe` (server stream) ->
for each event received, unary `SubmitVerdict`. Core: writes events into a
channel feeding the stream, awaits the verdict.

Result: works end to end, 10 000 + 1 000 warmup event/verdict round trips per scenario
completed with no errors or lost events; plain `http://` with HTTP/2 prior
knowledge worked (no TLS). Wrong token was rejected by `Hello`
(`REJECTED bad token`). Verdict: VERIFIED.

Not run: TLS; multiple modules at once; many concurrent streams; large payloads.

## Q5. Latency

Method: commands `dotnet Core.dll bench <dir> <Module exe>` (core spawns the module
per scenario). Release builds. Each scenario: 1000 warmup then 10 000 measured
iterations, timed with `Stopwatch` in microseconds.

- Unary: module times `Ping` calls (module -> core), 10 000 sequentially.
- Event+reply: core writes an event to the server stream, the module receives it,
  optionally spins 1 ms of busy work, calls unary `SubmitVerdict`, core completes.
  Timed on the core side from write to verdict received.
- "load": 4 extra tasks in the module issue `Ping` every 2 ms (light concurrent load).
- Idle = 0 ms work, "1 ms" = busy-spin 1 ms per event.

Results in microseconds (us), n = 10 000 each:

| Scenario | Call | p50 | p95 | p99 | p99.9 | max |
|---|---|---|---|---|---|---|
| idle module, no load | unary | 1285 | 5642 | 7941 | 15106 | 37037 |
| idle module, no load | event+verdict | 1251 | 5379 | 6024 | 10470 | 120840 |
| idle module, light load | unary | 1377 | 6640 | 11074 | 19372 | 118122 |
| idle module, light load | event+verdict | 1328 | 6296 | 9705 | 14280 | 80726 |
| 1 ms work, no load | unary | 1358 | 6032 | 8771 | 14999 | 159513 |
| 1 ms work, no load | event+verdict | 2608 | 6841 | 8871 | 14585 | 23675 |
| 1 ms work, light load | unary | 1416 | 6706 | 10331 | 17795 | 97692 |
| 1 ms work, light load | event+verdict | 2719 | 7626 | 10746 | 15536 | 67878 |

(For the "1 ms work" rows the unary figure is measured before the work starts, so
it is the same kind of measurement as idle; the difference is run to run noise.)

Cold start of a .NET module process, spawn to first successful call, 10 runs
(`Process.Start` in core to the core receiving the call; module is the framework
dependent `Module` apphost, Release):

- spawn -> `Hello` accepted: median 792 ms (min 556, max 1425)
- spawn -> first `Ping` done: median 941 ms (min 633, max 1516)
- module `Main` start -> `Hello` reply: median 771 ms (min 504, max 1223)
  (i.e. almost all the time is JIT/channel setup inside the module, not runtime
  startup; includes first-call gRPC/HTTP2 and JIT costs).

Caveats and honest reading:

- The ~1.3 ms median for a trivial unary over a local socket is much higher than
  expected for UDS on modern hardware. I believe, but did not prove, that this is mostly the busy
  shared i7-960 (load ~7 on 8 threads) and sequential `await` scheduling across two
  runtimes; I did not profile it or run on a quiet machine. The p95-p99 values of
  5-11 ms are dominated by that contention, and some max values (to 160 ms) look
  like scheduling stalls or GC; not investigated.
- The load generator was light and in the same process as the measured caller; no heavy
  or multi-module load was tested.
- Comparison with the spec proposal (50 ms per handler, 150 ms chain, unverified):
  the transport round trip here was ~1.3 ms p50 and ~6-11 ms p99 on a
  loaded old machine, with worst observed single samples of 160 ms (unary) and 121
  ms (event). A 3-handler chain at p99 of ~10 ms each would be ~30 ms of transport
  alone, leaving margin against 150 ms, but the max outliers show a single sample can
  exceed 50 ms on this machine. So: 50 ms per handler looks comfortable for typical
  calls (p99 about 5x under it) but NOT guaranteed on this hardware under
  contention, and the deadline policy must tolerate the rare slow call (it already
  follows the hook's fail policy per the owner decisions). A quiet-machine re-run is
  recommended before fixing the numbers. Handler work itself (Mongo, game thread
  queueing) is not included.
- Module cold start is about 0.8-1.5 s per .NET module here. With "required" modules
  holding the front-end gate, start-up timeout needs to be well above that; restart
  backoff for a crash loop costs about a second per attempt just in start-up.
  Not run: ReadyToRun/AOT/trimmed module, which could reduce this.

## Q6. Process supervision basics

Commands: `q6.sh`, `q6d.sh` (core `serve` mode spawns one module via `Process.Start`
with `MUD_TOKEN`, `MUD_SOCK`, `MUD_SCENARIO` in the child environment).

- Spawn with environment token: VERIFIED. Core generated a GUID token per spawn,
  passed it via `ProcessStartInfo.Environment`; module presented it in `Hello` and
  was accepted. A module started by hand with the wrong token got
  `REJECTED bad token`. Not tested: token visibility to other users via
  `/proc/<pid>/environ` (same-user readable by design, matches D17 stance).
- Child exit detection: VERIFIED. `Process.Exited` fired in core with exit code 0
  when the module finished (`CHILD_EXIT code=0`). Exit code of a crash was not
  separately exercised, only code 0.
- Child detecting core loss: VERIFIED for kill -9, with a caveat for graceful stop.
  Module holds the `Subscribe` stream.
  - Core killed with SIGKILL: the module's `MoveNext` threw `RpcException
    Unavailable ... ResponseEnded` about 109 ms after the kill (kill at wall
    ...269340, stream error at ...269449). Module then retried `Ping` for a 2 s
    grace period, got nothing, printed `EXITING back=False` (about 2.1 s after the kill)
    and exited with code 4. The child was gone.
  - Core given SIGTERM while a client stream was open: the core did NOT exit
    quickly. It stayed alive for about 30 s (polled each second: core gone at t+31 s,
    child gone at t+33 s). The module stayed connected the whole time and only saw
    the stream end when the core finally went away. The ~30 s matches the default
    generic host shutdown timeout, but I did not confirm the cause (for example by
    changing the timeout or completing the stream handlers). Consequence to
    design for: a graceful core stop must end the server streams itself, otherwise
    modules may not notice for ~30 s.
  - Orphan behaviour: after SIGKILL of the core, the child was NOT killed by the OS; it
    only exited because of its own connection watch. So the grace-period exit in
    D20 must be implemented in the module; nothing else cleans it up.
- Not run: restart with backoff, crash-loop handling, stale module process check at
  core start, PR_SET_PDEATHSIG, process groups, Windows.

## Other findings (not fixed; for separate work)

- `Microsoft.Extensions.DependencyInjection` 8.0.0 in MUD.Main must go to >= 8.0.1
  when Grpc.AspNetCore is added (NU1605 is an error).
- `CA1416` platform-analyzer warnings appear for `File.SetUnixFileMode`; wrap in
  an OS check or suppress when this is implemented for real.
- The `sun_path` length limit (108 bytes) applies to the socket paths chosen for
  deployment.

## Reproducing

Scratch code is not in the repo. Essential steps to rebuild: a classlib with
Grpc.Tools + `<Protobuf Include="...">`; a Web SDK project with Grpc.AspNetCore
and two `ListenUnixSocket` calls; a console with Grpc.Net.Client and the
`ConnectCallback` shown above. Environment: `DOTNET_ROOT=$HOME/.dotnet8`.
