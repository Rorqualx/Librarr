# Librarr

[![Build](https://github.com/Rorqualx/Librarr/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/Rorqualx/Librarr/actions/workflows/build.yml)
[![Nightly integration](https://github.com/Rorqualx/Librarr/actions/workflows/nightly-integration.yml/badge.svg)](https://github.com/Rorqualx/Librarr/actions/workflows/nightly-integration.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.md)

> **1.2.2-beta — 2026-08-22.** Forked from the archived
> [Readarr/Readarr](https://github.com/Readarr/Readarr) project (last
> upstream commit `0b79d300`, 2025-06-27). Rebuilds Readarr on top of
> Open Library as the primary metadata source.
>
> See [`CHANGELOG.md`](CHANGELOG.md) for full release notes,
> [`MASTER-PLAN.md`](MASTER-PLAN.md) for the strategic roadmap, and
> [`ARCHITECTURE.md`](ARCHITECTURE.md) § "Librarr fork additions" for
> a map of what changed in the fork.

Librarr is an ebook and audiobook collection manager for Usenet and
BitTorrent users. It monitors RSS feeds for new books from your favorite
authors and will grab, sort, and rename them. Like its predecessor, only
one type of a given book is supported per instance — run two instances
if you want both an audiobook and ebook of the same title.

## Heritage

Librarr inherits its codebase from Readarr, which was itself forked from
Sonarr in the Servarr family (Sonarr / Radarr / Lidarr / Readarr).
Internally many namespaces and assemblies still carry the `Readarr` and
`NzbDrone` names — see [`CLAUDE.md`](CLAUDE.md) and
[`ARCHITECTURE.md`](ARCHITECTURE.md) for the full identity map.

## Migrating from Readarr

Librarr ships a hands-off first-boot migration for existing Readarr
libraries. Point the container at your old Readarr `config/` directory
(it already contains your authors, books, and download history) and
start it — `LegacyMigrationService`
(`src/NzbDrone.Core/Books/Services/LegacyMigrationService.cs`) takes
over from there:

1. On `ApplicationStartedEvent` it scans the imported DB for legacy
   GoodReads-shaped IDs.
2. If it finds any, it flips `MonitorNewItems` to `None` per-author so
   the OpenLibrary refresh path doesn't grab unwanted new editions
   mid-migration.
3. It enqueues `ReidentifyLibraryCommand` at high priority. The
   reidentify pipeline walks every book, matches it against
   OpenLibrary using ISBN / ASIN / title-author confidence scoring,
   and writes results into the `BookIdMapping` bridge table
   (migration `041_book_id_mapping.cs`).
4. A frontend banner
   (`frontend/src/App/LegacyMigrationBanner.js`) reports progress
   while it runs, then auto-hides when done. The companion health
   check (`LegacyMigrationCheck`) surfaces problems if the marker
   never sets.
5. A persisted marker (`LegacyMigrationCompleted` in `config.xml`)
   prevents re-runs on subsequent restarts.

If you already manually reidentified your library before upgrading, the
migration detects the pre-populated `BookIdMapping` table and skips
straight to setting the marker.

## Importing an existing collection

This is the *other* migration, and it's easy to confuse with the one
above. The section above is for people with a Readarr **`config/`
directory** — a database that already knows about their authors. This
one is for people with a **folder of books and no database at all**,
whether they're coming from Calibre, a manual shelf, or nothing.

Two ways in, and they're complementary:

**Library Import** (Library → Library Import, or `/add/import`) — pick
the root folder holding your books. Every folder inside it that isn't
already an author becomes a row, pre-matched against Open Library, with
an editable search box if the guess is wrong. Select the ones you want
and import. Each author is attached to the folder that's already on
disk, so nothing gets moved or re-created.

Use this when you want to see and correct the matches — which, with
Open Library, you generally do. A search for a well-known author often
returns several records that are identical in every visible field, so
the wizard shows the OL id when names collide.

**Rescan** (Settings → Media Management → Root Folders → the rescan
button on a folder) — unattended. Reads tags and filenames and takes
the best Open Library match per file. Good for a tidy, well-tagged
library; anything it can't place lands in Unmapped Files.

Importing the authors first makes the subsequent file matching much more
reliable, because Librarr then searches that author's bibliography
instead of all of Open Library.

## Major Features

* Watches for better quality of the ebooks and audiobooks you have and
  does automatic upgrades (e.g., from PDF to AZW3). One instance handles
  both formats, but not for the same author at once — see
  [Ebooks and audiobooks in one instance](docs/ebooks-and-audiobooks.md).
* Cross-platform: Windows, Linux, macOS, Raspberry Pi.
* Automatically detects new books.
* Scans your existing library and downloads missing books.
* **Library Import** — point Librarr at a folder of books you already
  have and match each author folder against Open Library in one pass.
  See [Importing an existing collection](#importing-an-existing-collection).
* Failed-download handling: will try another release if one fails.
* Manual search to pick any release or see why one was skipped.
* Profiles for fine-grained quality / format preferences.
* Configurable book renaming.
* Supports SABnzbd, NZBGet, qBittorrent, Deluge, rTorrent, Transmission,
  uTorrent, and other download clients.
* Calibre integration (add to library, conversion) — requires Calibre
  Content Server.

## What changed vs Readarr

| Area | Readarr | Librarr |
|---|---|---|
| Primary metadata source | BookInfo (Goodreads-derived, unusable) | Open Library (native) |
| Series metadata | Goodreads | Wikidata SPARQL |
| Audiobook supplement | none | audnex.us (opt-in) |
| CI | Azure Pipelines | GitHub Actions |
| Sentry / telemetry | servarr.com, on by default | none — off unless you set `LIBRARR_SENTRY_DSN` |
| CLA | Required (assigns rights to Servarr) | None — GPL v3 inbound = outbound |
| Status | Archived 2025-06-27 | Active fork |

## Installing with Docker

Images are published to both GHCR and Docker Hub on every `v*` tag, as a
single multi-arch manifest covering **`linux/amd64`, `linux/arm64` and
`linux/arm/v7`**. Docker picks the right architecture for you — a
Raspberry Pi and an x86 server run the same `docker pull`.

```bash
docker run -d \
  --name librarr \
  -p 8787:8787 \
  -v /path/to/config:/config \
  -v /path/to/books:/books \
  -e PUID=1000 -e PGID=1000 -e TZ=Etc/UTC \
  ghcr.io/rorqualx/librarr:latest
```

Docker Hub is the same image: `rorqualx/librarr:latest`. Tags are
`:<version>` for every release, `:beta` on beta tags, and `:latest` only
on non-prerelease tags — so on a beta line pin `:beta` or the explicit
version rather than `:latest`.

`docker compose` and a local-build path are in
[`distribution/docker/README.md`](distribution/docker/README.md).

**ARM notes.** Both ARM images are cross-compiled natively rather than
under QEMU emulation, so they build as fast and as correctly as the
amd64 image.

`linux/arm64` is **runtime-verified**. Exercised on real aarch64
hardware, not emulated: boot to a passing container healthcheck, UI and
all routes serving, live Open Library search, root-folder scan with
unmapped-folder detection, a Library Import run, and a full discography
refresh with cover downloads. No errors in the log.

`linux/arm/v7` is verified only under emulation, and with a caveat worth
stating plainly. It completed that same workload, then QEMU itself
aborted — the assertion is inside the emulator's ARM Thumb instruction
translator (`target/arm/tcg/translate.c`), not in Librarr. Emulated
32-bit ARM and a JIT are a known-awkward combination. The database
survived intact and the container restarted cleanly, but **that is not
evidence the image is sound on real armv7 hardware, and not evidence it
is broken either.** If you run a 32-bit Pi, a report is the one thing
that would settle it.

## Status

**1.2.2-beta.** The OpenLibrary metadata proxy, BookIdMapping bridge,
reidentify pipeline, first-boot migration, Library Import wizard, and
multi-arch images are all shipped, joined in the 1.2.x-beta line by per-format
quality profiles, a root-folder audiobook profile default, and the
first Windows installers the fork has produced. See
[`CHANGELOG.md`](CHANGELOG.md) for the per-release breakdown.

Caveats:

- Field-validated on a single x86_64 deployment so far. Field reports
  welcome, especially from ARM.
- Several known follow-ups remain (duplicate-book-record dedupe,
  broader indexer coverage). Track progress in
  [`MASTER-PLAN.md`](MASTER-PLAN.md).

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). No CLA — contributions are
accepted under GPL v3 (inbound = outbound).

## License

* [GNU GPL v3](http://www.gnu.org/licenses/gpl.html)
* Copyright 2017-2025 readarr.com
* Copyright 2026-present Librarr Project
