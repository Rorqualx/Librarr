# Librarr Docker image — self-contained multi-stage build (Phase 9b).
#
# Build context: repo root.
#   docker build -f distribution/docker/Dockerfile -t librarr/librarr:1.0.0-beta.1 .
#
# Compiles backend + frontend INSIDE the image — no local .NET / Node
# toolchain required. If you already build locally and just want to
# package _output/, use distribution/docker/Dockerfile.prebuilt.
#
# Stages:
#   1. backend-build  → dotnet publish for the target arch's musl RID
#   2. frontend-build → yarn install + yarn build → _output/UI
#   3. runtime        → aspnet:10.0-alpine, copies from 1 + 2
#
# Multi-arch: supports linux/amd64, linux/arm64 and linux/arm/v7. Both
# build stages are pinned to $BUILDPLATFORM and CROSS-compile — .NET
# framework-dependent publish just needs the target's apphost from the
# runtime pack, and webpack output is arch-independent. Never let the
# SDK stage run under QEMU: Roslyn segfaults under x86_64-on-arm64
# emulation, and even where it survives it is ~10x slower.
#
#   docker buildx build -f distribution/docker/Dockerfile \
#     --platform linux/amd64,linux/arm64,linux/arm/v7 -t librarr/librarr:dev .
#
# Status: skeleton — the layer graph and stage flow are wired, but a
# full smoke (build → `docker run` → web UI loads) hasn't been run on
# this fork yet. If you build this and hit problems, file an issue
# rather than working around them silently.
#
# The runtime image expects:
#   /config         persistent app data (config.xml, Readarr.db, Logs/)
#   /books          your library root
#   /downloads      where download clients drop completed files

# ────────────────────────────────────────────────────────────────────
# Stage 1 — backend build
# ────────────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend-build

ARG LIBRARR_VERSION=1.0.0-beta.0

# READARRVERSION is what build.sh's UpdateVersionNumber() reads — kept for
# parity, but this Dockerfile calls `dotnet publish` directly and never runs
# build.sh, so the env var alone stamps nothing. The version is applied as
# MSBuild properties on the publish commands below instead; without that
# every image reported the 10.0.0.* placeholder from Directory.Build.props.
#
# This is not cosmetic. RuntimeInfo.InternalIsOfficialBuild()
# (src/NzbDrone.Common/EnvironmentInfo/RuntimeInfo.cs:206-215) treats
# Major >= 10 as an unofficial build, so a placeholder-versioned image runs
# with IsProduction == false — which changes Sentry DSN selection, analytics,
# HTTP redirect handling and initialize.json caching.
#
# Only AssemblyVersion and AssemblyConfiguration are worth passing:
# Directory.Build.props:86-88 sets GenerateAssemblyFileVersionAttribute and
# GenerateAssemblyInformationalVersionAttribute to false, so -p:FileVersion /
# -p:InformationalVersion are silently discarded. build.sh stamps the same
# two, via sed.
#
# Branch name recorded as AssemblyConfiguration; surfaces as BuildInfo.Branch
# and in the "release" field of /initialize.json.
ARG BUILD_BRANCH=main
ENV READARRVERSION=${LIBRARR_VERSION}

# TARGETARCH / TARGETVARIANT are injected by BuildKit from --platform. They
# are empty under the legacy (non-BuildKit) builder, which cannot do
# multi-platform anyway — that case falls back to linux-musl-x64.
ARG TARGETARCH
ARG TARGETVARIANT

# Escape hatch: pass --build-arg DOTNET_RID=... to pin the RID explicitly
# and skip the TARGETARCH mapping below.
ARG DOTNET_RID=""

# Resolve the .NET RID once and stash it at /rid for the later stages of
# this build. The runtime base is alpine, so every target is musl.
# Note the ${VAR:-} guards: BuildKit omits an ARG from the RUN environment
# entirely when it has no value, so bare ${TARGETVARIANT} would trip `set -u`
# on amd64/arm64 (which carry no variant).
RUN set -eu; \
    if [ -n "${DOTNET_RID:-}" ]; then \
        rid="${DOTNET_RID}"; \
    else \
        case "${TARGETARCH:-}${TARGETVARIANT:-}" in \
            amd64)           rid="linux-musl-x64" ;; \
            arm64|arm64v8)   rid="linux-musl-arm64" ;; \
            arm|armv7|armv6) rid="linux-musl-arm" ;; \
            "")              rid="linux-musl-x64" ;; \
            *)               echo "unsupported TARGETARCH '${TARGETARCH:-}${TARGETVARIANT:-}' — add it to the case in distribution/docker/Dockerfile" >&2; exit 1 ;; \
        esac; \
    fi; \
    echo "building for RID: ${rid}"; \
    printf '%s' "${rid}" > /rid; \
    printf '%s' "$(echo "${LIBRARR_VERSION}" | sed 's/-[A-Za-z][A-Za-z0-9]*//')" > /assemblyversion; \
    echo "assembly version: $(cat /assemblyversion) (informational: ${LIBRARR_VERSION})"

WORKDIR /src

# The Phase 9b skeleton tried to flatten csprojs into a separate
# restore layer for cache locality. That optimization assumed the
# project-folder name matched the csproj basename, which isn't true
# in this repo (folders are `NzbDrone.X`, csprojs are `Readarr.X`).
# Until someone wires up an explicit name-mapping, do a single
# COPY src/ + restore + publish. Slower on iterative builds; correct.
COPY .editorconfig ./
# NzbDrone.Core.csproj embeds ../../Logo/64.png — i.e., expects the
# Logo/ tree at the WORKDIR root, sibling to src/. Without this the
# Release publish dies with CS1566 on the embedded resource.
COPY Logo/ ./Logo/
COPY src/ ./src/
RUN dotnet restore src/Readarr.sln -p:Configuration=Release -p:Platform=Posix -r "$(cat /rid)"
RUN dotnet publish src/NzbDrone.Console/Readarr.Console.csproj \
        -c Release \
        -f net10.0 \
        -r "$(cat /rid)" \
        --no-restore \
        --self-contained false \
        -p:Platform=Posix \
        -p:AssemblyVersion="$(cat /assemblyversion)" \
        -p:AssemblyConfiguration="${BUILD_BRANCH}" \
        -o /publish

# The platform shim (Readarr.Mono on Linux/macOS; Readarr.Windows on
# Windows) is loaded by name at runtime via AssemblyLoader, not as a
# static ProjectReference of Console. dotnet publish on Console alone
# therefore doesn't drop it into /publish — we have to publish it
# separately and let its output overlay. Mirrors the per-project
# publish loop in build.sh's PublishAllRids target.
RUN dotnet publish src/NzbDrone.Mono/Readarr.Mono.csproj \
        -c Release \
        -f net10.0 \
        -r "$(cat /rid)" \
        --no-restore \
        --self-contained false \
        -p:Platform=Posix \
        -p:AssemblyVersion="$(cat /assemblyversion)" \
        -p:AssemblyConfiguration="${BUILD_BRANCH}" \
        -o /publish

# ────────────────────────────────────────────────────────────────────
# Stage 2 — frontend build
# ────────────────────────────────────────────────────────────────────
# Pinned to $BUILDPLATFORM: the webpack output under _output/UI is plain
# JS/CSS/HTML with no per-arch content, so building it once natively and
# copying it into every arch's runtime image is both correct and much
# faster than re-running yarn under emulation for each platform.
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend-build

WORKDIR /src

# Yarn install layer.
COPY package.json yarn.lock ./
RUN yarn install --frozen-lockfile --network-timeout 120000

# Webpack build. tsconfig.json at repo root is read by the
# fork-ts-checker plugin; the rest of the frontend config (babel,
# postcss, webpack) lives under frontend/.
COPY frontend/ ./frontend/
COPY tsconfig.json ./
RUN yarn run build --env production

# Output lands at /src/_output/UI (per project webpack config).

# ────────────────────────────────────────────────────────────────────
# Stage 3 — runtime image
# ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime

LABEL org.opencontainers.image.title="Librarr" \
      org.opencontainers.image.description="Librarr — OpenLibrary continuation of the archived Readarr ebook manager" \
      org.opencontainers.image.source="https://github.com/Rorqualx/Librarr" \
      org.opencontainers.image.licenses="GPL-3.0-only"

# su-exec: the entrypoint drops from root to PUID:PGID with it.
# tzdata: the base image ships no zoneinfo, so without it the documented
# TZ variable silently resolves to UTC.
#
# The three volume roots are created owned by 1000:1000 (the default
# PUID:PGID) so that the anonymous volumes Docker makes for any of them
# left unmounted are writable — both in the default start and under
# `--user 1000:1000`. Everything under /app stays root-owned and
# read-only to the app.
RUN apk add --no-cache \
        sqlite-libs \
        icu-libs \
        ca-certificates \
        bash \
        curl \
        su-exec \
        tzdata \
    && rm -rf /var/cache/apk/* \
    && mkdir -p /config /books /downloads \
    && chown 1000:1000 /config /books /downloads

ENV LIBRARR_HOME=/app \
    LIBRARR_DATA=/config \
    LIBRARR_PORT=8787 \
    LIBRARR_SSL_PORT=6868 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR ${LIBRARR_HOME}

COPY --from=backend-build /publish/ ${LIBRARR_HOME}/
COPY --from=frontend-build /src/_output/UI/ ${LIBRARR_HOME}/UI/

VOLUME ["/config", "/books", "/downloads"]
EXPOSE 8787 6868

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl -fsS http://localhost:${LIBRARR_PORT}/ping || exit 1

# The container starts as root on purpose — there is deliberately no USER
# instruction. The entrypoint needs root for one thing, giving /config to
# PUID:PGID (default 1000:1000), and then drops to that user with su-exec
# before exec'ing ./Readarr. Started with an explicit non-root `--user`,
# it skips all of that and just execs the app. See entrypoint.sh.
#
# chmod in a RUN rather than COPY --chmod so the legacy (non-BuildKit)
# builder still works, and so the mode does not depend on the checkout.
COPY distribution/docker/entrypoint.sh /usr/local/bin/librarr-entrypoint
RUN chmod 0755 /usr/local/bin/librarr-entrypoint

ENTRYPOINT ["/usr/local/bin/librarr-entrypoint"]
CMD ["-nobrowser", "-data=/config"]
