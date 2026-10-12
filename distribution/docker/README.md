# distribution/docker — Librarr docker images

Docker build definitions for Librarr. Images are published automatically
by [`.github/workflows/release.yml`](../../.github/workflows/release.yml)
on every `v*` tag push, as a multi-arch manifest covering
`linux/amd64`, `linux/arm64` and `linux/arm/v7`:

```bash
docker pull ghcr.io/rorqualx/librarr:beta    # GitHub Container Registry
docker pull rorqualx/librarr:beta            # Docker Hub
```

Tags: `:<version>` exact (e.g. `:1.2.3-beta`), `:<major>.<minor>`,
`:beta` on beta tags, and `:latest` only on non-prerelease tags. Every
release so far is a beta, so **`:latest` has never been published** and
pulling it fails — it will exist once there is a stable release. Docker
will select the right architecture for your host automatically.

You can also build locally from this directory — see **Build** below.

## Files

| File | Purpose |
|---|---|
| `Dockerfile` | Self-contained multi-stage build: compiles backend (`dotnet publish`) + frontend (`yarn build`) inside the image, then assembles a small runtime layer on `aspnet:10.0-alpine`. No local toolchain required. |
| `Dockerfile.prebuilt` | Thin packaging layer for when you already ran `./build.sh` and `yarn build` locally — copies `_output/` straight into the runtime image. Fast iteration, but requires the host toolchain. |
| `entrypoint.sh` | Entrypoint of both images: resolves `PUID`/`PGID`/`UMASK_SET`, takes ownership of `/config`, drops root and execs the app. See **User, permissions and umask** below. |

## Build

From the repo root — builds for the host's architecture:

```bash
docker build \
  -f distribution/docker/Dockerfile \
  -t librarr/librarr:1.0.0-beta \
  .
```

### Multi-arch

Supported platforms: **`linux/amd64`**, **`linux/arm64`**,
**`linux/arm/v7`**. The Dockerfile maps BuildKit's `TARGETARCH` to the
matching musl RID itself, so nothing needs to be passed per-arch:

| Platform | .NET RID |
|---|---|
| `linux/amd64` | `linux-musl-x64` |
| `linux/arm64` | `linux-musl-arm64` |
| `linux/arm/v7` | `linux-musl-arm` |

```bash
docker buildx create --use --name librarr   # once
docker buildx build \
  -f distribution/docker/Dockerfile \
  --platform linux/amd64,linux/arm64,linux/arm/v7 \
  -t librarr/librarr:1.0.0-beta \
  --push \
  .
```

A multi-platform build cannot load into the local daemon — use `--push`
to a registry, or build one platform at a time with `--load`.

Both build stages are pinned to `$BUILDPLATFORM` and **cross-compile**:
`dotnet publish -r <rid> --self-contained false` only needs the target's
apphost from the runtime pack, and the webpack bundle is
arch-independent. This is deliberate — the .NET SDK under QEMU is both
drastically slower and prone to Roslyn segfaults on x86_64-on-arm64.
QEMU is still required (the runtime stage's `apk add` runs on the
target), hence `docker/setup-qemu-action` in `release.yml`.

`--build-arg DOTNET_RID=...` still overrides the mapping for a
single-platform build — e.g. to target a glibc base (`linux-x64`,
`linux-arm64`) after swapping the runtime image. `win-arm64` is **not**
in the RID list and is unsupported (`src/Directory.Build.props:11`).

`Dockerfile.prebuilt` accepts the same `--platform` set, but only for
RIDs you have already built into `_output/net10.0/` — a missing RID fails
that platform's build with an explicit error rather than silently
shipping the wrong binaries. Populate it with:

```bash
READARRVERSION=1.0.0-beta.4 ./build.sh --backend --frontend
```

`READARRVERSION` matters: the prebuilt variant compiles nothing, so it
cannot stamp a version the way `Dockerfile` does. Without it the binaries
keep the `10.0.0.*` placeholder, and since
`RuntimeInfo.InternalIsOfficialBuild()` rejects `Major >= 10` the image
runs with `IsProduction == false` — changing Sentry DSN selection,
analytics and `initialize.json` caching.

Note `build.sh` begins with `rm -rf _output`, and passing `-r` a
semicolon-separated RID list does not work (MSBuild reads `;` as a
property separator, and `RuntimeIdentifiers` is consumed as singular
downstream). Build the full RID set, or one RID per run.

## Run

Minimum invocation:

```bash
docker run -d \
  --name librarr \
  --restart unless-stopped \
  -e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC -e UMASK_SET=002 \
  -p 8787:8787 \
  -v /path/to/librarr/config:/config \
  -v /path/to/your/library:/books \
  -v /path/to/downloads:/downloads \
  ghcr.io/rorqualx/librarr:beta
```

(Substitute the tag you built — e.g. `librarr/librarr:1.0.0-beta` — to
run a local build.) The repo-root [`docker-compose.yml`](../../docker-compose.yml)
is the same thing as a Compose service, with every value overridable
from a `.env` file.

### User, permissions and umask

| Variable | Default | Meaning |
|---|---|---|
| `PUID` | `1000` | uid Librarr runs as. |
| `PGID` | `1000` | gid Librarr runs as. |
| `UMASK_SET` | `022` | umask for files Librarr creates. `002` makes them group-writable. `UMASK` is accepted as an alias; `UMASK_SET` wins if both are set. |
| `TZ` | UTC | Time zone, e.g. `America/Denver`. |

The image has no `USER` instruction: by default the container **starts**
as root, and [`entrypoint.sh`](entrypoint.sh) then

1. applies the umask;
2. changes the owner of anything under `/config` that is not already
   `PUID:PGID` (so a `/config` left root-owned by an older image, which
   ran as root, is fixed on the first start);
3. drops to `PUID:PGID` with `su-exec` and `exec`s the app, which
   therefore runs as PID 1 and receives `docker stop`'s SIGTERM directly.

`/books` and `/downloads` are **never** chowned — they can be huge and
their ownership is yours to decide. If `PUID:PGID` cannot write to one of
them, the entrypoint logs a warning at start; fix the ownership on the
host or pick the `PUID`/`PGID` that owns them. Use the same ids as your
download client so imports can move files.

Alternatively run the container non-root from the outset:

```bash
docker run -d --user 1000:1000 -e UMASK_SET=002 ... ghcr.io/rorqualx/librarr:beta
```

With an explicit non-root `--user` (or Compose `user:`, or Kubernetes
`runAsUser`) the entrypoint does not chown or switch user — it applies
the umask and execs the app. `PUID`/`PGID` are ignored in this mode, and
`/config` must already be writable by that uid.

`PUID=0` keeps the app running as root. It is not recommended.

Notes:

- `docker exec librarr <cmd>` still runs as root; add `--user 1000:1000`
  when touching files under `/config` by hand.
- Dropping privileges needs the `CHOWN`, `SETUID` and `SETGID`
  capabilities. If you run with `--cap-drop ALL`, use `--user` instead
  of `PUID`/`PGID`.

Volume layout the container expects:

| Mount | Purpose |
|---|---|
| `/config` | Persistent app data — `config.xml`, `readarr.db` (the file name is kept from Readarr on purpose), `logs/`. |
| `/books` (or whatever you choose) | Your library root. Configure the root folder inside Librarr's UI to match. |
| `/downloads` (or whatever you choose) | Where your download client(s) drop completed grabs. Configure Remote Path Mappings if the path-as-seen-by-the-download-client differs from the path-as-seen-by-Librarr. |

Default ports: **8787** HTTP, **6868** HTTPS (only listening once you
enable SSL in Librarr; add `-p 6868:6868` then — the Compose file
publishes both)
(`src/NzbDrone.Host/Bootstrap.cs:135-136`). Override via `config.xml`
(`Port`, `SslPort`, `BindAddress`); `config.xml` reload-on-change is
disabled (`Bootstrap.cs:237`), so changes require a restart.

## Migrating from Readarr

Point `/config` at a copy of your old Readarr `config/` directory and
start the container. The first-boot `LegacyMigrationService` handles
the rest — see ["Migrating from Readarr"](../../README.md#migrating-from-readarr).
A persisted marker in `config.xml` (`LegacyMigrationCompleted`)
prevents re-runs on subsequent restarts.
