# Planar Realms — Local Deployment

Runs `MUD.Main` as a systemd **user** service against the local MongoDB
(localhost, authentication on). Configuration is described in
[CONFIGURATION.md](CONFIGURATION.md).

## Layout

```
~/.config/mud/mongo.env            MUD_MONGO_URI=...   (required, mode 600, not in git)
~/.config/mud/server.env           MUD_BIND_ADDRESS / MUD_PORT overrides (optional)
~/.config/systemd/user/planar-realms.service
~/.local/share/mud/releases/<UTC timestamp>-<sha>/   one directory per deploy
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

The unit sets `MUD_BIND_ADDRESS=0.0.0.0` and `MUD_PORT=7680` as defaults;
put different values in `server.env` to change them (then
`systemctl --user restart planar-realms`). It restarts on failure after
5 s and gives up after 5 failed starts in 2 minutes (for example when
`MUD_MONGO_URI` is missing), so a bad config does not loop forever.

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
   to `MUD.Main` for the content scan.
3. Atomically repoints the `current` symlink and runs
   `systemctl --user restart planar-realms`. If the restart fails, it points
   `current` back at the previous release and restarts again.
4. Keeps the newest 5 releases (`MUD_KEEP_RELEASES`, minimum 2), never
   removing the current or previous one.
5. Prints how to roll back.

Other overrides: `MUD_DEPLOY_ROOT` (default `~/.local/share/mud`) and
`MUD_SERVICE` (default `planar-realms`). Requires `git` and the .NET 8 SDK
on the host. The script never touches `mongo.env`.

Note: a deploy restarts the game, which disconnects connected players.
Runtime state is written through to MongoDB (see RUNTIME_STATE.md).

## Rolling back

```bash
scripts/deploy.sh --rollback
```

This repoints `current` to the newest release directory that is not the
current one and restarts the service. The release that was rolled back
from is kept, so running `--rollback` again switches forward again. To pick
a specific release:

```bash
ln -sfn ~/.local/share/mud/releases/<id> ~/.local/share/mud/current
systemctl --user restart planar-realms
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
