# Planar Realms — Local Deployment

Runs `MUD.Main` as a systemd **user** service against the local MongoDB
(localhost, authentication on). Configuration is described in
[CONFIGURATION.md](CONFIGURATION.md).

## Layout

```
~/.config/mud/mongo.env            MUD_MONGO_URI=...   (required, mode 600, not in git)
~/.config/mud/telnet.env           MUD_TELNET_BIND_ADDRESS / MUD_TELNET_PORT overrides for the telnet unit (optional)
~/.config/mud/web.env              MUD_WEB_URL override for the website (optional)
~/.config/systemd/user/planar-realms.service
~/.config/systemd/user/planar-realms-web.service
~/.config/systemd/user/planar-realms-telnet.service
~/.local/share/mud/releases/<UTC timestamp>-<sha>/   one directory per deploy
~/.local/share/mud/releases/<id>/web/                the MUD.Web publish output
~/.local/share/mud/current  ->  releases/<id>        what the service runs
```

## One-time setup (maintainer)

1. Create `~/.config/mud/mongo.env` with the install script. It must
   contain `MUD_MONGO_URI=...` and be readable only by you (`chmod 600`).
2. Install the unit and enable lingering so the service survives logout
   and starts at boot:

   ```bash
   mkdir -p ~/.config/systemd/user
   cp planar-realms.service ~/.config/systemd/user/
   systemctl --user daemon-reload
   loginctl enable-linger "$USER"
   ```
3. Run the first deploy (below), then `systemctl --user enable planar-realms`.

Core has no telnet port since card P7-3: players connect through the telnet unit below, which is therefore required for
playing over telnet. The unit restarts on failure after
5 s and gives up after 5 failed starts in 2 minutes (for example when
`MUD_MONGO_URI` is missing), so a bad config does not loop forever.

**Modules and the start limit (P4-3, D70).** Core now scans a modules directory and starts every module it finds as a child
process (see "Modules" below). The game unit allows at most 3 restarts within 5 minutes
(`StartLimitBurst=3`, `StartLimitIntervalSec=300`) and then stays failed. This is what ends the loop when a module marked
required never becomes Ready: core exits with a clear error after the start-up timeout, systemd restarts it, and after the third
try systemd stops. Reset it with `systemctl --user reset-failed planar-realms`, fix the module, then start the unit.
The unit also sets `KillMode=control-group` and `TimeoutStopSec=60` so stopping core also stops its module children, and
`Environment=DOTNET_ROOT=/usr/share/dotnet` (change it if dotnet lives elsewhere on the host; the supervisor passes it to modules).
**This changes the earlier "5 starts in 2 minutes" limit of the game unit; copy the unit again and run `systemctl --user daemon-reload`.
The web unit keeps its own limit.**

## Modules (phase 4)

Modules are separate programs that core starts and supervises. They need no unit of their own.

- **Directory.** `ModuleHost:ModulesDirectory` in `appsettings.json` (default `modules` next to `MUD.Main`; a relative path is
  relative to the release). One folder per module, the executable inside named after the folder: `modules/desert/desert`.
  The deploy script does not publish modules yet (a later card does); until then the directory is empty or missing and the game starts as before.
- **`--describe`.** Every module executable prints one JSON document for `--describe` and exits without connecting:
  `{"name","version","contract_version","arguments","behavior_types","required"}`. Core refuses a contract version that differs
  from its own at scan time. The `required` flag comes from this output.
- **Start-up.** Core starts the modules after the world is loaded and holds `frontends.sock` shut to front ends (a Connect is refused; the telnet host retries by itself) until every required module
  reports Ready. If one is not Ready within `ModuleHost:StartupTimeoutSeconds` (30), core stops the modules it started, prints
  `STARTUP FAILED: Required module ...` to standard error (the journal) and exits with status 1.
- **Restarts.** A module that ends unexpectedly is restarted after `RestartBackoffSeconds` (1, 2, 5, 10, 30 seconds, the last repeats).
  `CrashLoopMaxCrashes` (5) crashes within `CrashLoopWindowSeconds` (300) mark it failed; an admin `modules start <name>` clears that.
  A module that stops answering Ping for `PingTimeoutSeconds` (30) is stopped and restarted like a crash.
- **Stale processes.** Before every start, core ends other processes of the same user whose executable is that module's full path
  (terminate signal, `TerminateGraceSeconds` (5) of grace, then kill). A module started by hand from the same path is therefore
  killed the next time core starts that module. Two cores sharing one modules directory are not supported.
- **Logs.** Each module's standard output and error appear in core's log with a `[module:name]` prefix: `journalctl --user -u planar-realms -f | grep '\[module:'`.
- **Admin command** (Admin role only): `modules list`, `modules rescan`, `modules start|stop|restart <name>`, `modules grants reload`.
  There is no development token and no `modules token` or `modules reload` (D69, D67).
- **Not verified:** none of this has run against a real module under systemd on a host; the tests use shell-script modules.

## Website unit (maintainer, optional)

`planar-realms-web.service` runs `MUD.Web` (see [WEB.md](WEB.md)) from `current/web/MUD.Web`. It
reads only the optional `~/.config/mud/web.env` (it no longer reads `mongo.env`: the web has no database access and talks to core on
`frontends.sock`).
It listens on `http://127.0.0.1:5080` by default (set in the unit); to change that, put
`MUD_WEB_URL=...` in `web.env`. Restart policy and start limits are the same as the game unit
(restart on failure after 5 s, give up after 5 failed starts in 2 minutes). It does not start the
game World or telnet server. The unit has no `Requires=` on the game unit, but its pages need core running: with core down
they show a fixed "not reachable" message and sign-in does not work.

The site speaks plain HTTP and the login sends the password in clear, so keep the default
localhost bind unless something in front of it provides HTTPS (see WEB.md).

Install after the first deploy has produced `current/web` (nothing here is done by `deploy.sh`):

```bash
cp planar-realms-web.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now planar-realms-web
```

Check with `systemctl --user status planar-realms-web` and `curl -sI http://127.0.0.1:5080/login`.

## Telnet host unit (maintainer; cards P7-2 and P7-3)

`planar-realms-telnet.service` runs `MUD.Telnet.Host`, the telnet front end as its own process, from `current/telnet/MUD.Telnet.Host`.
It has no database access: it talks to core on `frontends.sock` through the Session service (see [SESSION_API.md](SESSION_API.md)).
Since card P7-3 this is the only telnet listener: the in-process telnet server was removed from `MUD.Main`, and `MUD_BIND_ADDRESS`,
`MUD_PORT`, the `--ip`/`--port` options and `TelnetSettings` in `appsettings.json` no longer exist. Enable this unit to play over telnet.

Settings (environment variables; the unit sets the first two, `~/.config/mud/telnet.env` may override any of them):

| Variable | Default | Meaning |
|---|---|---|
| `MUD_TELNET_BIND_ADDRESS` | `0.0.0.0` | Address to listen on. |
| `MUD_TELNET_PORT` | `7680` | Telnet port. |
| `MUD_RUN_DIRECTORY` | `$XDG_RUNTIME_DIR/mud`, else `~/.local/share/mud/run` | Where core's `frontends.sock` is (same rule as core). |
| `MUD_FRONTEND_TOKEN_FILE` | `~/.config/mud/frontend-token` | The mode-600 token file core creates (D66). Read at every connect attempt. |
| the telnet limits | see `TelnetLimits` | `MUD_MAX_LINE_LENGTH`, `MUD_MAX_CONNECTIONS`, `MUD_IDLE_TIMEOUT_*` and the others of the in-process server. |

**Core restarts (D29).** When core stops or restarts, every open telnet session is told "The world is restarting. Please reconnect in a
moment." and closed. A player who connects while core is away gets the same message. The host reconnects to core by itself, waiting 1, 2,
5, 10, 30 seconds between attempts (the last repeats; starts over after a success). The host does not need a restart when core does.
These wait times are an implementer choice, not an owner decision. The unit starts after `planar-realms.service` but does not require it.

Install after the first deploy has produced `current/telnet` (`deploy.sh` does not install units):

```bash
cp planar-realms-telnet.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now planar-realms-telnet
```

`deploy.sh` publishes the host into `telnet/` of every release and restarts the unit only if it is enabled. Check with
`systemctl --user status planar-realms-telnet` and `journalctl --user -u planar-realms-telnet -f`.

Not verified: the unit has not been started under systemd, and no live telnet session was played through the host against a running core
(no MongoDB in the sandbox). The reconnect logic and the Session service round trip are covered by `MUD.Tests/TelnetHostTests.cs`.

## Deploying

```bash
scripts/deploy.sh [ref]      # ref defaults to hermes
```

The script:

1. Fetches `ref` (branch, tag or sha) from the repo's `origin` into a fresh
   temporary build directory, so no local working-tree state leaks in.
   The remote can be overridden with `MUD_REPO_URL`.
2. Runs `dotnet publish` (Release) for `MUD.Main` and `MUD.PlanarRealms`
   into one new release directory. The `MUD.PlanarRealms.dll` has to sit next
   to `MUD.Main` for the content scan. `MUD.Web` is published into the `web/`
   subdirectory of the same release so its files do not mix with the game's.
3. Atomically repoints the `current` symlink and runs
   `systemctl --user restart planar-realms`, then restarts
   `planar-realms-web` only if that unit is enabled (otherwise it prints a
   note and skips it, so deploying before the web unit is installed works).
   If either restart fails, it points `current` back at the previous release
   and restarts again.
4. Keeps the newest 5 releases (`MUD_KEEP_RELEASES`, minimum 2), never
   removing the current or previous one.
5. Prints how to roll back.

Other overrides: `MUD_DEPLOY_ROOT` (default `~/.local/share/mud`),
`MUD_SERVICE` (default `planar-realms`) and `MUD_WEB_SERVICE` (default
`planar-realms-web`). Requires `git` and the .NET 8 SDK
on the host. The script never touches `mongo.env` (only the game unit reads it).

Note: a deploy restarts the game, which disconnects connected players.
Runtime state is written through to MongoDB (see RUNTIME_STATE.md).

## Rolling back

```bash
scripts/deploy.sh --rollback
```

This repoints `current` to the newest release directory that is not the
current one and restarts the game service and, if enabled, the web service.
The release that was rolled back from is kept, so running `--rollback` again
switches forward again. A release made before the website existed has no
`web/` directory; rolling back to it still works for the game, but the web
restart will fail (a warning is printed) until you roll forward. To pick a
specific release:

```bash
ln -sfn ~/.local/share/mud/releases/<id> ~/.local/share/mud/current
systemctl --user restart planar-realms planar-realms-web
```

## Triggering deploys after merges to `hermes`

Deploys are never automatic on merge; something has to run the script.

- **Maintainer, by hand:** `scripts/deploy.sh` after a PR is merged to
  `hermes`.
- **Hermes cron job:** schedule a job on this host that runs, for example,
  every 15 minutes:

  ```bash
  cd ~/Repos/MUD && git fetch -q origin hermes && \
    [ "$(git rev-parse --short=10 origin/hermes)" != "$(cut -d' ' -f2 ~/.local/share/mud/current/RELEASE 2>/dev/null)" ] && \
    scripts/deploy.sh hermes
  ```

  `current/RELEASE` holds `<ref> <short sha>`; the comparison above makes
  a deploy happen only when `hermes` has moved. Check CI status on the
  merge commit first if deploys should be gated on a green build.
  The job needs the same environment as an interactive shell for
  `systemctl --user` (`XDG_RUNTIME_DIR=/run/user/$(id -u)`) and for
  `dotnet` in `PATH`.

## Not verified

The script was syntax checked and passed `shellcheck`; the rollback path
was exercised against a stub `systemctl` in a scratch directory. The
publish, symlink switch against a real service and the systemd unit have not been run
on a host. Verify them on the first real deploy.

The web additions (`web/` publish, `planar-realms-web.service`, conditional web restart) were
checked with `bash -n`, `systemd-analyze --user verify` (only complaint: the binary does not
exist yet because nothing is installed) and a local `dotnet publish` of `MUD.Web`. The unit has
never been started, and `deploy.sh` has not been run end to end with the web step.
