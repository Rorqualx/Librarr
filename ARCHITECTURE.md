# Librarr Architecture (formerly Readarr)

> A map of the codebase: layout, layering, conventions, contradictions, and
> open challenges. Static analysis only — no runtime/operational notes.
> File-and-line citations are accurate against the tree at the time of writing.
>
> **Snapshot:** describes Librarr at the `1.1.0-beta` release (`main`,
> 2026-07-30). Version-, count- and pin-bearing lines were re-verified
> against the tree on 2026-07-30; treat anything older as suspect. The
> shipping-version references below were bumped for the `1.2.2-beta`
> tag on 2026-08-22, but nothing else in this file was re-verified then
> — the counts and citations still date from 2026-07-30. Inherits its codebase from upstream `Readarr/Readarr` at
> `develop` HEAD `0b79d300` ("Retirement announcement", 2025-06-27); last
> upstream tagged release was `v0.4.18.2805` (commit `7cc02f95`,
> 2025-06-10). The upstream repository is archived on GitHub.

**Status of the underlying Readarr code:** **archived upstream.** This
document still describes that inherited code map. For a focused inventory
of what *Librarr* added on top, jump to
[§ Librarr fork additions](#librarr-fork-additions) immediately below.

---

## Librarr fork additions

Code paths added or materially changed in the Librarr fork on top of the
inherited Readarr tree. Everything here is new since upstream
`0b79d300`; the sections that follow this one describe the inherited
codebase. See [`CHANGELOG.md`](CHANGELOG.md) for the per-cycle history
of how this material landed.

### OpenLibrary metadata source

`src/NzbDrone.Core/MetadataSource/OpenLibrary/`. Native HTTP client +
search services + response→domain mappers — no `rreading-glasses`
shim and no `bookinfo.club` dependency.

- `OpenLibraryProxy.cs` — HTTP proxy, `AllowAutoRedirect = true`
  request shape (the explicit-opt-in pattern that Cycle 7a later
  ported to the NZB grab path).
- `OpenLibraryAuthorSearchService.cs`, `OpenLibraryBookSearchService.cs`
  — search by name / title / identifier shape.
- `OpenLibraryIdHelper.cs` — `IsAuthorId` / `IsWorkId` shape checks
  promoted out of `MetadataSourceFactory` so other callers can share
  the canonical OL ID predicates.
- `Mappers/AuthorMapper.cs`, `BookMapper.cs`, `EditionMapper.cs` —
  manual mapping from OL response shapes to `Author` / `Book` /
  `Edition` domain entities (no AutoMapper, matching the rest of
  the codebase).

### `BookIdMapping` bridge table

Migration `src/NzbDrone.Core/Datastore/Migration/041_book_id_mapping.cs`.
Confidence-scored GoodReads-ID → OpenLibrary work/edition mappings,
registered in `TableMapping.cs` (Cycle 4) and consumed by
`MetadataSourceFactory.cs` on every refresh (Cycle 5). The reverse
lookup (`GoodreadsId` → OL IDs) dominates the access pattern during
legacy data migration. Backed by `BookIdMappingRepository.cs`.

### Reidentify pipeline

- `src/NzbDrone.Core/Books/Services/ReidentifyService.cs` — walks every
  book, matches against OpenLibrary using
  ISBN / ASIN / title-author confidence, persists results into
  `BookIdMapping`.
- `src/NzbDrone.Core/Books/Commands/ReidentifyLibraryCommand.cs` — the
  command that triggers it. Manually runnable, or auto-enqueued by
  the first-boot migration below.

### First-boot legacy migration

- `src/NzbDrone.Core/Books/Services/LegacyMigrationService.cs` —
  handles `ApplicationStartedEvent` + `CommandExecutedEvent`. On
  first boot of a legacy DB, it: (1) detects GoodReads-shaped IDs,
  (2) flips `MonitorNewItems` to `None` per-author (so the OL
  refresh path doesn't grab unwanted editions mid-migration),
  (3) enqueues `ReidentifyLibraryCommand` at high priority and
  stashes the command ID, (4) on the matching `CommandExecutedEvent`
  Completed status, persists the `LegacyMigrationCompleted` marker.
- Config surface: `LegacyMigrationCompleted` + `LegacyMigrationVersion`
  in `ConfigService.cs`. `LegacyMigrationService.CurrentVersion`
  defines the schema version.
- `src/NzbDrone.Core/HealthCheck/Checks/LegacyMigrationCheck.cs` —
  state machine: marker set → Ok; no legacy authors → Ok; command
  queued/running → Notice; legacy authors + no command → Warning.

### Frontend migration banner

- `frontend/src/App/LegacyMigrationBanner.js` + `.css` + `Connector.js`
  — polls `state.system.health.items` filtered to
  `source === 'LegacyMigrationCheck'` and `state.commands.items` for
  active `ReidentifyLibrary` commands every 15 s. Renders
  `Alert kind=INFO` while running, `WARNING` while pending,
  auto-hides on Ok.
- Wired into `frontend/src/Components/Page/Page.js:94` directly
  below `<PageHeader>`.

### Narrator surface

- Migrations `042_edition_narrators.cs`, `043_normalized_narrators.cs`,
  `044_drop_editions_narrators.cs` — additive normalized narrator
  schema, then drop the legacy CSV column once the join is live.
- `NarratorService.cs` (under `Books/Services/`), wired into
  `RefreshEditionService.cs`.
- REST surface: `Readarr.Api.V1.Narrators` (`NarratorController.cs` +
  `NarratorResource.cs`), with a per-narrator detail page on the
  frontend.

### Cover-picker + canonical default

- Migration `045_book_preferred_cover_url.cs` — `Book.PreferredCoverUrl`
  column.
- Frontend modal lets the user pick from candidate OL covers; the
  canonical OL cover is the default.
- `scripts/bench_le_guin.py` — offline harness for evaluating
  cover-pick quality against a held-out fixture set.

### Quality / file recognition

- `src/NzbDrone.Core/MediaFiles/MediaFileExtensions.cs:24` — `.azw`
  (Kindle KF7) mapped to `Quality.MOBI`. Same Mobipocket container
  as `.mobi`; extraction path is identical. *(Cycle 7c.)*

### Download-client fix: NZB redirects

`src/NzbDrone.Core/Download/UsenetClientBase.cs` — sets
`request.AllowAutoRedirect = true` on the NZB grab. Required because
locally-built docker images don't run as Azure `officialBuild`, so
the dev-mode redirect-rejection guardrail in `HttpClient.cs:101` was
rejecting every NZBgeek / NzbPlanet CDN-redirect grab. *(Cycle 7a.)*

### Loud import-rejection surface

`src/NzbDrone.Core/MediaFiles/BookImport/ImportApprovedBooks.cs`'s
`Import()` method — for every rejected `ImportDecision`, materializes
an `ImportResult` entry, logs a Warn line with the rejection reasons,
and publishes `TrackImportFailedEvent`. Real Transmission /
SABnzbd-originated imports also light up a `BookImportIncomplete` row
in Activity history via the existing
`CompletedDownloadService.Process` chain. Was previously a silent
Debug-level skip. *(Cycle 7d.)*

### Library Import wizard

Restores the bulk "adopt what's already on disk" flow that every other
Servarr app ships (`ImportSeries` / `ImportMovies` / `ImportArtist`) and
that upstream Readarr dropped when it forked from Lidarr — leaving
`RootFolders/UnmappedFolder.cs` and
`MediaFiles/BookImport/ImportArtistDefaults.cs` behind as dead code.

- `src/NzbDrone.Core/RootFolders/RootFolderService.cs` —
  `GetUnmappedFolders()` lists first-level subdirectories of a root
  folder that no author's path claims, minus a special-folder blocklist
  (`$RECYCLE.BIN`, `lost+found`, `@eaDir`, Calibre's `.caltrash`, …).
  Takes `IAuthorRepository` rather than `IAuthorService`: the latter
  would close a DryIoc cycle through `AuthorPathBuilder` →
  `IRootFolderService`. Surfaced as
  `RootFolderResource.UnmappedFolders`, and `Ignore`d in
  `TableMapping.cs` since it is recomputed per read, never persisted.
- `src/Readarr.Api.V1/Author/AuthorImportController.cs` — `POST
  /api/v1/author/import`, bulk-adds via the pre-existing
  `IAddAuthorService.AddAuthors`. Returns only the authors actually
  created, since `AddAuthors` skips ones it cannot resolve.
- `frontend/src/LibraryImport/` — the two-step UI, at `/add/import`.
  See that folder's `README.md` for why lookups are queued rather than
  parallel (Open Library meters the search endpoint) and why no row is
  ever pre-selected.

Complements rather than replaces the root folder rescan button: rescan
is file-driven and unattended, this is folder-driven and asks the user
to confirm each author, which narrows the subsequent file matching from
all of Open Library to that author's bibliography.

### CI / packaging

- `azure-pipelines.yml:22` — `majorVersion: '1.2.2-beta'`.
- `distribution/docker/Dockerfile` — self-contained multi-stage build
  (Phase 9b). Compiles backend + frontend inside the image, runs on
  `aspnet:10.0-alpine`. Build command + run shape documented in
  `distribution/docker/README.md`.
- GitHub Actions replaces Azure Pipelines as the CI driver.

---

## Table of contents

0. [Librarr fork additions](#librarr-fork-additions) — what this fork added on top of inherited Readarr
1. [Overview](#1-overview)
2. [High-level architecture](#2-high-level-architecture)
3. [Repository layout](#3-repository-layout)
4. [Backend (.NET) architecture](#4-backend-net-architecture)
   - [Project inventory](#41-project-inventory)
   - [Layering & dependency graph](#42-layering--dependency-graph)
   - [Cross-cutting patterns](#43-cross-cutting-patterns)
5. [Frontend (React) architecture](#5-frontend-react-architecture)
6. [Build, CI, packaging, standards](#6-build-ci-packaging-standards)
7. [Conventions & standards](#7-conventions--standards)
8. [Contradictions, antipatterns, smells](#8-contradictions-antipatterns-smells)
9. [Open challenges](#9-open-challenges)
10. [How to find your way around](#10-how-to-find-your-way-around)

---

## 1. Overview

**Readarr** is an ebook and audiobook collection manager — the Servarr-family
sibling of Sonarr (TV), Radarr (movies), and Lidarr (music). It monitors RSS
feeds and indexers, downloads via Usenet/BitTorrent clients, applies quality
profiles, optionally integrates with Calibre, and renames/sorts files
(`README.md:12-27`).

The architecture is **forked from Sonarr** and shows it in two visible ways:

1. **C# namespaces stay `NzbDrone.*`** while csproj/assembly names are
   `Readarr.*`. This split is set explicitly in
   `src/Directory.Build.props:97-99`:

   ```xml
   <!-- For now keep the NzbDrone namespace -->
   <RootNamespace Condition="'$(ReadarrProject)'=='true'">$(MSBuildProjectName.Replace('Readarr','NzbDrone'))</RootNamespace>
   ```

   So `src/NzbDrone.Core/Readarr.Core.csproj` builds an assembly named
   `Readarr.Core` whose default namespace is `NzbDrone.Core`. Newer projects
   (`Readarr.Api.V1`, `Readarr.Http`) keep both name and namespace as
   `Readarr.*`. See [§8](#8-contradictions-antipatterns-smells).

2. **`Stylecop.ruleset` still labels itself "Rules for Radarr"** — line 1
   literally:

   ```xml
   <RuleSet Name="Stylecop.ruleset" Description="Rules for Radarr" ToolsVersion="15.0">
   ```

   The ruleset was forked from Radarr without retitling.

The product currently versions itself at `1.2.2-beta`
(`azure-pipelines.yml:22`). The `AssemblyVersion 10.0.0.*` in
`Directory.Build.props:77` is the historical Readarr placeholder the CI
overwrites at build time — not the shipping version, and not bumped
for this beta. Revisit at the 1.0.0 final cut.

---

## 2. High-level architecture

```
                          ┌────────────────────────────────────┐
   Browser (React SPA) ───┤ Kestrel / ASP.NET Core 6           │
                          │   Readarr.Http     (middleware)    │
                          │   Readarr.Api.V1   (REST)          │
                          │   NzbDrone.SignalR (real-time)     │
                          └────────────────┬───────────────────┘
                                           │ DryIoc DI
                          ┌────────────────┴───────────────────┐
                          │ NzbDrone.Core (business logic)     │
                          │  • Books / Author / Edition domain │
                          │  • Indexers + DownloadClients      │
                          │    (ThingiProvider plugin model)   │
                          │  • Messaging: Events + Commands    │
                          │  • Jobs / Housekeeping / Scheduler │
                          │  • Decision engine (specs)         │
                          │  • Parser (regex)                  │
                          │  • Datastore (custom Dapper ORM,   │
                          │    FluentMigrator, SQLite|Postgres)│
                          │  • MediaFiles / Tags / Calibre     │
                          │  • Notifications / Import Lists    │
                          └────────────────┬───────────────────┘
                                           │
                          ┌────────────────┴───────────────────┐
                          │ NzbDrone.Common (utilities)        │
                          │  Env, Disk, HTTP, Cache, Json,     │
                          │  Instrumentation (NLog targets)    │
                          └────────────────┬───────────────────┘
                                           │
              ┌─────────────────┬──────────┴──────────┬────────────────┐
              │ NzbDrone.Windows│ NzbDrone.Mono       │ NzbDrone.Console│
              │ (Win service +  │ (Linux/macOS svc,   │ (CLI entry)     │
              │  Firewall API)  │  posix signals)     │                 │
              └─────────────────┴──────────┬──────────┴────────────────┘
                                           │
                          ┌────────────────┴───────────────────┐
                          │ NzbDrone (Readarr.exe)             │
                          │ Tray/Windows-Forms shim → Bootstrap│
                          └────────────────────────────────────┘

  External: Indexers (Newznab/Torznab/Gazelle/FileList/Nyaa/IPTorrents),
            Download clients (Sabnzbd/QBittorrent/Transmission/Deluge/rTorrent
            /uTorrent/Aria2/NzbGet/NzbVortex/DownloadStation/Hadouken/Blackhole),
            Notifications (Discord/Slack/Telegram/Email/Plex/Webhook/…),
            Calibre Content Server, Goodreads/LazyLibrarian import sources.

  Process modes (Bootstrap.cs:62-107):
    • Service     — Windows service
    • Interactive — tray or console
    • Utility     — one-shot install/uninstall/register-URL
```

Default HTTP port `8787`, HTTPS port `6868` (`NzbDrone.Host/Bootstrap.cs:135-136`).
Three configuration sources merged at startup: XML file
(`config.xml`), in-memory keys (data-protection path), env vars
(`Bootstrap.cs:236-240`).

---

## 3. Repository layout

```
Readarr-develop/
├── ARCHITECTURE.md          ← this document
├── README.md                Marketing/install page, links to wiki
├── CLA.md                   No CLA — GPL v3 inbound = outbound
├── CODE_OF_CONDUCT.md       Contributor Covenant
├── CONTRIBUTING.md          Librarr contributor guide (branch, build, PR handling)
├── SECURITY.md              Vuln-report pointer → GitHub advisories
├── LICENSE.md               GPL v3 (~35 KB full text)
├── azure-pipelines.yml      ~1257-line multi-stage CI definition
├── build.sh                 Local-and-CI build orchestration (~442 lines)
├── test.sh                  dotnet test wrapper
├── docs.sh                  OpenAPI/swagger doc generation
├── package.json             Root-level package.json (no nested one in frontend/)
├── tsconfig.json            3-line stub → frontend/tsconfig.json
├── yarn.lock                Yarn classic lock
├── Logo/                    SVG/PNG project artwork
├── distribution/
│   ├── windows/             InnoSetup installer assets + service helpers
│   └── osx/                 .app bundle template + DMG scripts
├── schemas/
│   └── torznab.xsd          Torznab indexer feed schema (consumed by
│                            NzbDrone.Core/Indexers/Torznab,Newznab)
├── frontend/
│   ├── babel.config.js
│   ├── postcss.config.js
│   ├── tsconfig.json        TS config for the SPA (used by webpack ts-loader)
│   ├── jsconfig.json
│   ├── typings/             Hand-written ambient .d.ts files
│   ├── .eslintrc.js         393-line ESLint config (see §6)
│   ├── .stylelintrc         CSS lint rules
│   ├── .prettierrc.json     Prettier config
│   ├── build/               Webpack 5 config, loaders, CSS pipeline
│   └── src/
│       ├── index.ts         Polyfill entry
│       ├── bootstrap.tsx    Real SPA bootstrap (history, store, render)
│       ├── App/             App shell + AppRoutes
│       ├── Author/          Author index / details / edit
│       ├── Book/            Book index / details / edit / delete
│       ├── BookFile/        File editor / table
│       ├── Calendar/        Calendar page
│       ├── Commands/        Server-command bridges
│       ├── Components/      Shared UI primitives (Page, Modal, Form, Table…)
│       ├── InteractiveSearch/
│       ├── Organize/        File rename previews
│       ├── Retag/           Tag rewrite previews
│       ├── Settings/        All settings pages (Indexers, Quality, UI, …)
│       ├── Store/           Redux store, actions, reducers, selectors
│       ├── Styles/          Globals, themes, CSS variables
│       ├── System/          System status, tasks, logs, backups
│       ├── Utilities/       Date/string/api helpers, createAjaxRequest
│       └── Wanted/          Missing / cutoff-unmet pages
└── src/
    ├── Readarr.sln          The .NET solution
    ├── Readarr.sln.DotSettings  ReSharper team settings (committed)
    ├── Directory.Build.props    Root MSBuild props for every project
    ├── Directory.Build.targets  Near-empty targets file (75 bytes)
    ├── Directory.Packages.props Central Package Management (real CPM, see §6)
    ├── NuGet.config             Package sources + Servarr feed
    ├── stylecop.json            StyleCop rule settings
    ├── Stylecop.ruleset         StyleCop rule actions ("Rules for Radarr")
    ├── coverlet.runsettings     Coverage collector config
    ├── postgres.runsettings     Postgres-test connection settings
    │
    ├── Libraries/               Vendored Win32 firewall interop DLL
    ├── Targets/                 Custom MSBuild .targets (RID enumeration)
    ├── ServiceHelpers/          ServiceInstall/ServiceUninstall exe projects
    │
    ├── NzbDrone/                Readarr.csproj — main entry exe (tray shim)
    ├── NzbDrone.Common/         Cross-cutting utilities (no business logic)
    ├── NzbDrone.Console/        Console-mode entry point exe
    ├── NzbDrone.Core/           THE business-logic project (largest)
    ├── NzbDrone.Host/           ASP.NET Core host: Bootstrap + Startup, DI
    ├── NzbDrone.SignalR/        SignalR Hub for real-time UI push
    ├── NzbDrone.Mono/           Linux/macOS platform shim (posix, service)
    ├── NzbDrone.Windows/        Windows platform shim (service, firewall)
    ├── NzbDrone.Update/         Self-updater executable
    ├── Readarr.Api.V1/          REST API v1 — controllers + resources
    ├── Readarr.Http/            HTTP middleware: auth, errors, framing
    │
    ├── NzbDrone.Api.Test/       ← All 11 *.Test projects: NUnit + Moq
    ├── NzbDrone.Automation.Test/  Selenium-driven UI automation
    ├── NzbDrone.Common.Test/
    ├── NzbDrone.Core.Test/
    ├── NzbDrone.Host.Test/
    ├── NzbDrone.Integration.Test/ Full-stack web integration tests
    ├── NzbDrone.Libraries.Test/
    ├── NzbDrone.Mono.Test/
    ├── NzbDrone.Test.Common/      Shared test infrastructure (not actually a test
    │                              project — it's a fixture library)
    ├── NzbDrone.Test.Dummy/       Tiny dummy assembly used by tests
    ├── NzbDrone.Update.Test/
    └── NzbDrone.Windows.Test/
```

Top-level dirs/files have their own per-directory `README.md` for orientation;
this document is the canonical overview.

---

## 4. Backend (.NET) architecture

### 4.1 Project inventory

29 csproj projects total (15 production + 11 tests + 2 service-helper exes +
1 dummy). Counts derived from `grep -E "^Project\(" src/Readarr.sln`.

**Production (15):**

| Project (folder)            | csproj                  | Output       | Purpose |
|-----------------------------|-------------------------|--------------|---------|
| `NzbDrone`                  | `Readarr.csproj`        | exe (tray)   | Tray/Windows entry point — wraps `Bootstrap.Start` |
| `NzbDrone.Console`          | `Readarr.Console.csproj`| exe (console)| Console entry point — non-tray launcher |
| `NzbDrone.Common`           | `Readarr.Common.csproj` | library      | Disk, env, http, cache, json, instrumentation utilities |
| `NzbDrone.Core`             | `Readarr.Core.csproj`   | library      | Business logic — domain, ORM, providers, jobs, messaging |
| `NzbDrone.Host`             | `Readarr.Host.csproj`   | library      | `Bootstrap`, `Startup`, DI wiring, Kestrel + URL setup |
| `NzbDrone.SignalR`          | `Readarr.SignalR.csproj`| library      | `MessageHub` for real-time push to UI |
| `NzbDrone.Mono`             | `Readarr.Mono.csproj`   | library      | Linux/macOS platform shim (signals, service hosting) |
| `NzbDrone.Windows`          | `Readarr.Windows.csproj`| library      | Windows platform shim (service, firewall — `Libraries/Interop.NetFwTypeLib.dll`) |
| `NzbDrone.Update`           | `Readarr.Update.csproj` | exe (update) | Self-updater binary that replaces files |
| `Readarr.Api.V1`            | `Readarr.Api.V1.csproj` | library      | REST controllers and `*Resource` DTOs |
| `Readarr.Http`              | `Readarr.Http.csproj`   | library      | HTTP middleware: auth, error handling, CORS, framing |
| `ServiceHelpers/ServiceInstall`   | `ServiceInstall.csproj`   | exe | Windows service install helper |
| `ServiceHelpers/ServiceUninstall` | `ServiceUninstall.csproj` | exe | Windows service uninstall helper |

**Tests (11):** all named `*.Test`. They use NUnit 3.14.0 + Moq 4.17.2
(`src/Directory.Packages.props:29,38`) plus optional FluentAssertions, NBuilder,
AutoFixture. `NzbDrone.Test.Common` is a test fixture library (utilities)
rather than a test runner project.

**Build infrastructure (not csproj):**

| Dir                          | What                                          |
|------------------------------|-----------------------------------------------|
| `src/Targets/`               | Custom MSBuild `.targets` (RID enumeration)   |
| `src/Libraries/`             | Vendored `Interop.NetFwTypeLib.dll` (Win firewall API) |
| `src/Directory.Build.props`  | Common props applied to every project         |
| `src/Directory.Build.targets`| Near-empty — placeholder                      |
| `src/Directory.Packages.props`| Central package versions (CPM)               |
| `src/stylecop.json`          | StyleCop settings                             |
| `src/Stylecop.ruleset`       | StyleCop rule actions                         |
| `src/coverlet.runsettings`   | Coverage settings                             |
| `src/postgres.runsettings`   | Postgres-test connection                      |

### 4.2 Layering & dependency graph

```
NzbDrone (exe)  &  NzbDrone.Console (exe)
       │  references:
       ▼
NzbDrone.Host  ────────────────────────────►  Readarr.Http
       │      ──► NzbDrone.SignalR             │
       │      ──► Readarr.Api.V1   ────────────┤
       │                                       │
       ▼                                       ▼
NzbDrone.Core  ◄────────────────────────────────
       │
       ▼
NzbDrone.Common
       ▲
NzbDrone.Windows  / NzbDrone.Mono ───────────────► NzbDrone.Common
       (platform shims, used by Host)
```

Test projects depend on the corresponding production project plus
`NzbDrone.Test.Common`. The dependency graph is **strictly bottom-up** —
`NzbDrone.Common` has no dependency on `NzbDrone.Core`, and `NzbDrone.Core`
has no dependency on `Readarr.Http` or `Readarr.Api.V1`. The HTTP/API layer
talks to Core, not vice-versa.

### 4.3 Cross-cutting patterns

#### 4.3.1 Dependency Injection — **DryIoc**

The DI container is **DryIoc 5.4.3** (`src/Directory.Packages.props:7-8`),
bridged into ASP.NET Core via `DryIoc.Microsoft.DependencyInjection`. Setup
lives in `src/NzbDrone.Host/Bootstrap.cs`:

```csharp
// Bootstrap.cs:90 — utility mode
.UseServiceProviderFactory(new DryIocServiceProviderFactory(
    new Container(rules => rules.WithNzbDroneRules())))

// Bootstrap.cs:150 — host mode (Kestrel)
.UseServiceProviderFactory(new DryIocServiceProviderFactory(
    new Container(rules => rules.WithNzbDroneRules())))
```

The `WithNzbDroneRules()` extension and `AutoAddServices` registration scan a
fixed list of assemblies (`Bootstrap.cs:37-44`):

```csharp
public static readonly List<string> ASSEMBLIES = new List<string>
{
    "Readarr.Host", "Readarr.Core", "Readarr.SignalR",
    "Readarr.Api.V1", "Readarr.Http"
};
```

Auto-registration: convention-based scan of the listed assemblies, registering
every concrete public class against its implemented interfaces. Most lifetimes
are singletons; controllers and per-request services are scoped via the
ASP.NET Core bridge.

#### 4.3.2 Persistence — custom Dapper ORM + dual SQLite/Postgres

There is **no Entity Framework, no Marr.Data, and no raw Dapper-as-the-API**.
Instead, `NzbDrone.Core/Datastore/` is a hand-rolled mini-ORM on top of
**Dapper 2.0.151** (`src/Directory.Packages.props:6`). Core pieces:

- `Datastore/BasicRepository.cs` — generic `BasicRepository<TModel>` with
  `Insert`, `Update`, `Delete`, `Find`, `All`, `Single`, joins, paged queries.
- `Datastore/SqlBuilder.cs` — fluent SQL builder.
- `Datastore/WhereBuilderSqlite.cs` / `WhereBuilderPostgres.cs` — dialect-
  specific predicate translation.
- `Datastore/DbFactory.cs` + `Datastore/ConnectionStringFactory.cs` —
  picks SQLite (default) or Postgres based on `config.xml` /
  `Readarr:Postgres` env vars (`Bootstrap.cs:102,161`).
- `Datastore/MigrationController.cs` — runs migrations on startup using a
  Servarr-forked FluentMigrator:
  - `Servarr.FluentMigrator.Runner` 3.3.2.9
    (`Directory.Packages.props:12-14`).
  - Migration classes live in `Datastore/Migration/0xx_*.cs`, numbered.
  - There are 47 migrations as of 2026-07-30 (latest
    `046_author_audiobook_quality_profile`) — a working schema-evolution
    discipline.
- SQLite uses a forked `System.Data.SQLite.Core.Servarr` build
  (`Directory.Packages.props:52`) — necessary because the official
  System.Data.SQLite package doesn't support the platforms Servarr ships.

JSON column converters for complex types (`CustomFormat`, `EmbeddedDocument`,
`Quality`, `TimeSpan`, etc.) live under `Datastore/Converters/`. Models are
plain POCOs that inherit from `ModelBase` (with `Id` property).

#### 4.3.3 HTTP API — ASP.NET Core MVC, no Nancy

The API project `Readarr.Api.V1` exposes REST endpoints using **ASP.NET Core
MVC** (not Nancy — older Servarr code used Nancy, that's gone). Pattern:

- `{Entity}Controller : RestController<{Entity}Resource>` (e.g.
  `Readarr.Api.V1/Books/BookController.cs`).
- `{Entity}Resource` — DTO living next to the controller.
- Manual mapping (no AutoMapper) between domain models and resources.
- Some controllers extend a SignalR-aware base
  (`{Entity}ControllerWithSignalR<TResource>`), which broadcasts CRUD events.

`Readarr.Http` provides:

- API-key authentication (`AuthenticationBuilderExtensions.cs`).
- Error-to-HTTP exception filters.
- CORS, response framing, request logging.
- Static-content serving for the SPA.

Swagger schema is generated by `Swashbuckle.AspNetCore.SwaggerGen 6.5.0`
(`Directory.Packages.props:49`). The generated `openapi.json` is intentionally
excluded from CI triggers (`azure-pipelines.yml:33,43`) so committing a
regenerated spec doesn't kick off a build loop.

#### 4.3.4 SignalR — real-time UI push

`NzbDrone.SignalR/MessageHub` is the single hub. Controllers that need to push
updates inherit `*WithSignalR` base classes; events from the in-process
`EventAggregator` are forwarded to all connected clients. Used for:

- Command progress (`CommandUpdated`).
- Queue updates.
- Health-check changes.
- Entity CRUD (author/book added/deleted/updated).

Frontend consumes via `@microsoft/signalr` (`package.json:33`).

#### 4.3.5 Provider plugin model — `ThingiProvider`

The most distinctive backend pattern. **Indexers, Download Clients,
Notifications, Import Lists, and Metadata sources all share one plugin
abstraction** rooted in `NzbDrone.Core/ThingiProvider/`:

- `IProvider` — marker interface.
- `ProviderFactory<TProvider, TDefinition>` — discovers, instantiates, and
  caches providers.
- `ProviderStatusServiceBase` — tracks health with exponential back-off when
  a provider fails repeatedly.
- `IProviderRepository<TDefinition>` — stores per-instance config (settings,
  URL, API key, etc.) in the database.

Concrete provider hierarchies built on this:

| Domain         | Folder                                  | Base classes                                          |
|----------------|-----------------------------------------|-------------------------------------------------------|
| Indexers       | `NzbDrone.Core/Indexers/`              | `IndexerBase` → `HttpIndexerBase` → concrete          |
| Download clients | `NzbDrone.Core/Download/Clients/`    | `DownloadClientBase` → `TorrentClientBase` / `UsenetClientBase` → concrete |
| Notifications  | `NzbDrone.Core/Notifications/`         | `NotificationBase` → concrete (Discord/Slack/Email/…) |
| Import lists   | `NzbDrone.Core/ImportLists/`           | `ImportListBase` → `HttpImportListBase` → concrete    |
| Metadata sources | `NzbDrone.Core/MetadataSource/`      | per-source (e.g., `BookInfoProxy`)                    |

Concrete indexers: Newznab, Torznab, Gazelle, FileList, Nyaa, IPTorrents,
TorrentRss. Concrete download clients: Sabnzbd, NzbGet, NzbVortex,
QBittorrent, Transmission, Deluge, rTorrent, uTorrent, Aria2,
DownloadStation (torrent + usenet), Hadouken, Blackhole (torrent + usenet).
The base hierarchy is deep — see [§8](#8-contradictions-antipatterns-smells)
for the duplication smell.

#### 4.3.6 Messaging — commands + events

Two in-process channels, both in `NzbDrone.Core/Messaging/`:

- **`Events/`** — `EventAggregator` synchronous pub/sub. Anything implementing
  `IHandle<TEvent>` (or `IHandleAsync<TEvent>`) receives matching events.
  Handlers live next to the domain that owns them (e.g.,
  `Books/Events/Handlers/AuthorAddedHandler.cs`).
- **`Commands/`** — `CommandQueueManager` + `CommandExecutor` run
  `ICommand`s on a background queue. Handlers implement `IExecute<TCommand>`.
  Commands surface to the user via the `/api/v1/command` endpoint and
  SignalR `CommandUpdated` pushes.

Scheduling: `NzbDrone.Core/Jobs/` — `TaskManager`, `Scheduler`, `ScheduledTask`.
A timer enqueues commands at configured intervals (RSS sync, refresh
metadata, backup, housekeeping, etc.). Cleanup runs come from
`NzbDrone.Core/Housekeeping/` (~20 housekeeper classes).

#### 4.3.7 Configuration — XML file + database

Configuration splits across two stores:

- **`config.xml`** — bootstrap config (`Bootstrap.cs:237`):
  `BindAddress`, `Port`, `SslPort`, `SslCertPath`, `ApiKey`, etc. Managed by
  `NzbDrone.Core/Configuration/ConfigFileProvider.cs`.
- **Database `Config` table** — key/value JSON store for runtime app
  preferences. Accessed via `ConfigService` and `ConfigRepository`.

Postgres-specific options bind from a `Readarr:Postgres` configuration section
(`Bootstrap.cs:102,161`).

#### 4.3.8 Logging — NLog

NLog 5.1.4 (`Directory.Packages.props:34`). Targets:

- Console / file (rotated).
- `DatabaseTarget` — writes structured log rows to the database for the Logs
  page in the UI.
- Sentry sink (Sentry 4.0.2).
- Syslog target (`NLog.Targets.Syslog 7.0.0`).

`ReconfigureLogging` allows the UI to change log level at runtime (in
`NzbDrone.Common/Instrumentation/`).

#### 4.3.9 Decision engine — specification pattern

`NzbDrone.Core/DecisionEngine/` decides whether a release should be grabbed.
Each rule is an `ISpecification<RemoteBook>` (or `RemoteAuthor`). The
`DownloadDecisionMaker` runs every spec and produces an accept/reject result
with rejection reasons. Specs are auto-registered by the DI scan.

This pattern also drives the import pipeline
(`NzbDrone.Core/MediaFiles/BookImport/Specifications/`).

#### 4.3.10 Parser — regex-heavy release name parsing

`NzbDrone.Core/Parser/Parser.cs` is ~905 lines of regular expressions for
extracting author/title/year/quality from release names. Companion files:
`QualityParser.cs`, `LanguageParser.cs`, `IsoLanguages.cs`. File-format tag
readers live in `NzbDrone.Core/MediaFiles/`: `EpubTag.cs`, `AzwTag.cs`,
`AudioTag.cs` (~553 lines, wraps `TagLibSharp-Lidarr 2.2.0.19`).

#### 4.3.11 Testing — NUnit + Moq

| Project                     | Style                                                    |
|-----------------------------|----------------------------------------------------------|
| `*.Test`                    | NUnit unit tests, one project mirrors each production project |
| `NzbDrone.Test.Common`      | Shared `TestBase<T>`, mock builders, fixture helpers     |
| `NzbDrone.Integration.Test` | Spins up the full host and exercises the REST API + DB   |
| `NzbDrone.Automation.Test`  | Selenium WebDriver (Chrome) browser tests — pinned to **Selenium 3.141.0** and **ChromeDriver 91.0.4472.x** (`Directory.Packages.props:43-44`) — both several major versions behind current |

`coverlet.collector` is the coverage tool (`Directory.Packages.props:5`),
configured via `src/coverlet.runsettings`. Postgres-mode runs use
`src/postgres.runsettings`.

---

## 5. Frontend (React) architecture

### 5.1 Tech stack

A **React 18 + Redux 4 SPA**, JavaScript-first with a barely-started
TypeScript migration. All metadata is in the **root** `package.json` (no
nested `frontend/package.json` exists). Versions verified against
`package.json` on 2026-07-30:

| Concern      | Package & version                                                            |
|--------------|------------------------------------------------------------------------------|
| Framework    | `react 18.3.1`, `react-dom 18.3.1` — mounted with `createRoot` (`frontend/src/bootstrap.tsx:3,18`), not legacy `ReactDOM.render` |
| Router       | `react-router 5.2.0`, `react-router-dom 5.2.0`, `history 4.10.1`             |
| Redux        | `redux 4.2.1`, `react-redux 7.2.4`, `redux-actions 2.6.5`, `redux-thunk 2.4.2`, `redux-batched-actions 0.5.0`, `redux-localstorage 0.4.1`, `reselect 4.1.8`, `connected-react-router 6.9.3` |
| HTTP         | `jquery 3.7.1` (only inside `Utilities/createAjaxRequest.js`)                |

Two caveats that are easy to get wrong:

- **React 18 is real, but the ecosystem around it is not caught up.**
  `react-redux` is still 7.2.4, which predates React 18 and does not use
  `useSyncExternalStore`. Combined with legacy `createStore` and
  `connected-react-router` 6.x, the app is on React 18 without any of the
  concurrent features. Upgrading React was the easy half.
- **The TypeScript migration is ~6% done, not ~29%.** Any count of `.ts`
  files that does not exclude `*.css.d.ts` is wrong by an order of
  magnitude: there are 353 generated CSS declaration files against just
  32 hand-written `.ts` and 36 `.tsx`, versus 1004 `.js`.
| Realtime     | `@microsoft/signalr 6.0.25`                                                  |
| Types        | `typescript 5.1.6`, `prop-types 15.8.1` — coexist                            |
| Icons        | `@fortawesome/*` (free)                                                      |
| DnD          | `react-dnd 14.0.4` (+ HTML5/touch backends)                                  |
| Misc UI      | `react-tabs`, `react-popper`, `react-autosuggest`, `react-virtualized`, `react-custom-scrollbars-2`, `react-document-title`, `react-lazyload`, `react-measure`, `react-slider`, `mousetrap`, `clipboard`, `fuse.js`, `filesize`, `moment` |
| Lodash       | `lodash 4.17.21` — used heavily across actions and reducers                  |
| Error report | `@sentry/browser 7.51.2`, `@sentry/integrations 7.51.2`                      |
| Build        | `webpack 5.88.2`, `babel-loader 9.1.3`, `postcss 8.4.38`, `ts-loader 9.4.4`, `mini-css-extract-plugin`, `terser-webpack-plugin`, `css-modules-typescript-loader`, `typescript-plugin-css-modules` |
| Lint/format  | `eslint 8.57.0`, `eslint-plugin-react 7.34.1`, `eslint-plugin-react-hooks 4.6.0`, `eslint-plugin-prettier 4.2.1`, `prettier 2.8.8`, `stylelint 15.10.3` |
| Runtime pin  | **Node 24.18.1 LTS**, declared three ways that must agree: `.nvmrc`, `package.json` `engines` + `volta`, and `NODE_VERSION` in both GitHub workflows. Node 20 went EOL 2026-04-30 |

There is no test runner declared in `package.json` — the frontend has
**no actively running JS test suite** despite some `.test.js`-style files
scattered around. (Servarr frontend tests run via ESLint/Stylelint rules and
the Selenium automation suite on the backend side.)

### 5.2 Directory layout under `frontend/src/`

```
frontend/src/
├── index.ts                  Polyfill entry; imports bootstrap.tsx
├── bootstrap.tsx             SPA bootstrap — history, store, <App /> render
├── polyfills.js              Array/Object polyfills for older browsers
├── App/
│   ├── App.js                Top-level <Provider> + <ConnectedRouter>
│   ├── AppRoutes.js          Static route table (no lazy/code-splitting)
│   ├── ColorImpairedContext.js  Accessibility colour-mode context
│   └── …
├── Author/        Author index / details / edit / delete pages
├── Book/          Book index / details / edit / delete pages
├── BookFile/      File editor / table
├── Calendar/      Calendar page
├── AddNewItem/    "Add new" flows
├── AddSeries/     (legacy naming — leftover from Sonarr)
├── Activity/      Activity / queue / history / blocklist
├── Commands/      Server-command bridges
├── Components/    Shared UI primitives
│   ├── Form/      Form inputs (TagInput, …)
│   ├── Link/      Icon buttons, nav links
│   ├── Loading/   Spinners
│   ├── Menu/      Dropdowns
│   ├── Modal/     Modals, confirm, edit
│   ├── Page/      Page, PageHeader, PageSidebar, PageToolbar
│   ├── Router/    Switch wrapper
│   ├── Table/     VirtualTable
│   ├── Swipe/     Touch swipe headers
│   └── SignalRConnector.js   Single SignalR client → Redux dispatcher
├── InteractiveSearch/  Manual search UI
├── Organize/      Rename previews
├── Retag/         Retagging previews
├── Settings/      Settings pages (Indexers, Quality, Profiles, UI, …)
├── Store/         Redux store, actions, reducers, selectors, thunks
│   ├── Actions/        Action creators by feature
│   ├── Actions/Creators/   Reducer/handler factories
│   ├── Middleware/     appMiddleware, sentryMiddleware, …
│   ├── Migrators/      Local-storage schema migrators
│   ├── Reducers/       Combined root reducer
│   ├── Selectors/      Reselect selectors
│   ├── createAppStore.js   Store factory (legacy createStore)
│   └── thunks.js       Custom createThunk/handleThunks pattern
├── Styles/
│   ├── globals.css
│   ├── scaffolding.css
│   ├── Themes/         dark.js / light.js — colour tokens
│   └── Variables/      CSS variables for colors, fonts, mixins
├── System/        System status, tasks, logs, backups
├── Utilities/
│   ├── Api/
│   ├── Date/
│   ├── String/         translate (custom i18n shim)
│   ├── Object/
│   └── createAjaxRequest.js   jQuery $.ajax wrapper
├── Wanted/        Missing / cutoff-unmet pages
└── typings/       (sibling under frontend/, not under src/) ambient .d.ts
```

### 5.3 Component conventions

- **PascalCase folder per component.** A component, its CSS module, and its
  Redux container live together: `Foo/Foo.js` + `Foo/Foo.css` +
  `Foo/FooConnector.js`.
- **Class components dominate.** Most pages and complex components are class
  components with `constructor`/`componentDidMount`/`componentWillUnmount`.
  Newer code uses hooks — there are 136 hook callsites across just 28 files
  (`useState`/`useEffect`/`useCallback`/`useMemo`/…) tree-wide, so adoption has
  started but is partial.
- **PropTypes for `.js`, TS types for `.ts/.tsx`.** ESLint enforces
  `react/prop-types: 2` only on the JS side and turns it `off` on TS files
  (`frontend/.eslintrc.js:317,365`). Both worlds coexist by design while the
  migration is in progress.
- **File extensions are a migration signal — but only if you exclude the
  generated ones.** Counts of `frontend/src/**` on 2026-07-30:
  - `.js`: 1004
  - `.jsx`: 0 (everyone uses `.js` with JSX inside)
  - `.ts`: 32 hand-written, **plus 353 generated `*.css.d.ts`**
  - `.tsx`: 36

  **~6% TS by file count**, not the ~29% previously recorded here. The
  old figure counted the CSS Modules declaration files — which are emitted
  by the build, not written by anyone — as migrated TypeScript, making the
  migration look five times further along than it is.

### 5.4 State management

Redux store assembled the **legacy way** — `createStore` from `redux`, not
`configureStore` from RTK (`Store/createAppStore.js`). Action creator pattern:

```js
// Store/Actions/authorActions.js
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import createFetchHandler from './Creators/createFetchHandler';
import createHandleActions from './Creators/createHandleActions';
```

Two thunk styles coexist:

- The real `redux-thunk` middleware (declared in `package.json:84`).
- A **custom** `createThunk` / `handleThunks` pair under `Store/thunks.js`
  — it's the predominant pattern. Action creators register named thunks; a
  middleware dispatches them when matching action types fire.

Reducer boilerplate is factored into generator functions under
`Store/Actions/Creators/`:

- `createFetchHandler.js` — `isFetching` / `isPopulated` / `error` / `items`
  shape from a network fetch.
- `createHandleActions.js` — maps action types to reducer fragments.
- `createSetReducerValueReducer.js` — generic setter.

Selectors use `reselect`; deep-equality selectors via
`createDeepEqualSelector.js`.

### 5.5 API + SignalR data layer

- **`Utilities/createAjaxRequest.js`** wraps **jQuery `$.ajax`** as the sole
  HTTP client. Adds API-key header, content-type, request ID, error shaping.
  No `fetch`/`axios`.
- **`Components/SignalRConnector.js`** establishes the SignalR connection,
  subscribes to entity channels, and dispatches Redux actions directly when
  events arrive. It is mounted once near the root and lives for the SPA's
  lifetime.

### 5.6 Routing

Static route table in `App/AppRoutes.js` — every page is imported at module
load (no lazy import / no code splitting). React Router 5 with
`<ConnectedRouter>` from `connected-react-router` so router state lives in
Redux. No client-side auth guards — the backend rejects unauthenticated
requests via the `Readarr.Http` middleware.

### 5.7 Theming & i18n

- Themes are JS objects with colour tokens (`Styles/Themes/dark.js`,
  `Styles/Themes/light.js`) applied via CSS variables defined in
  `Styles/Variables/`. The user picks a theme in Settings → UI; it is
  persisted via `redux-localstorage`.
- **No real i18n library.** Translations are loaded from the backend and
  resolved via `Utilities/String/translate.js`. Strings live in
  `src/NzbDrone.Core/Localization/Core/*.json` (excluded from CI triggers —
  see `azure-pipelines.yml:42`).

### 5.8 Build pipeline

Webpack 5 config under `frontend/build/`:

- Babel transpiles JS/TS (`@babel/preset-env`, `@babel/preset-react`,
  `@babel/preset-typescript`).
- PostCSS pipeline (`postcss-cssnext`-style features via individual plugins:
  `postcss-mixins`, `postcss-nested`, `postcss-simple-vars`,
  `postcss-color-function`, `autoprefixer`).
- `ts-loader` + `fork-ts-checker-webpack-plugin` for the TS files.
- `css-modules-typescript-loader` + `typescript-plugin-css-modules` generate
  ambient `.css.d.ts` for CSS Module class names — that's the only reason
  ESLint disables `prettier/prettier` for `*.css.d.ts`
  (`frontend/.eslintrc.js:388`).
- Output goes to `./_output/UI` (`package.json:8`).

Run with `yarn start` (watch mode) or `yarn build`. The .NET build invokes
`yarn build` as a step in `build.sh`.

---

## 6. Build, CI, packaging, standards

### 6.1 CI/CD — Azure Pipelines

`azure-pipelines.yml` (~1257 lines) defines stages and a matrix:

```
Setup → Build_Backend (Linux | Mac | Windows matrix)
      → Build_Frontend
      → Unit_Test
      → Integration_Test (incl. FreeBSD via cross-publish)
      → Automation_Test (Selenium on Chrome)
      → Package (10 RIDs + InnoSetup .exe + .dmg)
      → Sign (Authenticode for Windows)
      → Release (publish to internal feeds + GitHub Releases)
```

Notable variables (`azure-pipelines.yml:6-23`):

- `majorVersion: '1.2.2-beta'` — the *real* shipping version
  (`azure-pipelines.yml:22`).
- `minorVersion: $[counter('minorVersion', 1)]` — auto-incremented.
- `dotnetVersion: '10.0.302'` — .NET 10 LTS (migrated 2026-07-30).
- `nodeVersion: '24.X'` — frontend runtime (Node 24 LTS).
- `innoVersion: '6.2.0'` — Windows installer toolchain.
- VM images: `windows-2022`, `ubuntu-20.04`, `macOS-11` — the macOS-11 image
  is deprecated by Azure DevOps; flag for maintenance.

Triggers:

- `develop` and `master` branches (`azure-pipelines.yml:25-29`).
- PRs to `develop` only (`azure-pipelines.yml:35-43`).
- Excluded paths: `.github`, `src/NzbDrone.Core/Localization/Core` (Weblate-
  generated translations), `src/Readarr.Api.*/openapi.json` (generated swagger).

**Subtle contradiction:** StyleCop analysis only runs on the Linux job
(`enableAnalysis: 'true'` at `azure-pipelines.yml:79`; `'false'` for Mac and
Windows on lines 83, 87). So even though `EnforceCodeStyleInBuild=true` is set
globally in `Directory.Build.props:5`, two of the three matrix legs ignore
StyleCop output. Style violations break CI only on Linux.

### 6.2 Build entrypoints

- **`build.sh`** (~442 lines) — drives the full backend + frontend build for
  one or many RIDs. Modes via flags: `--backend`, `--frontend`, `--packages`,
  `--enable-extra-platforms` (adds `freebsd-x64` and `linux-x86` to the
  bundled RIDs via a `sed` patch on the .NET SDK's
  `Microsoft.NETCoreSdk.BundledVersions.props`, see
  `azure-pipelines.yml:102-111`).
- **`test.sh`** — wraps `dotnet test` with the right runsettings.
- **`docs.sh`** — regenerates the OpenAPI/swagger.json artifact via the
  Swashbuckle CLI.

### 6.3 .NET build infrastructure

From `src/Directory.Build.props`:

- `TreatWarningsAsErrors=true` (l.4) — strict.
- `EnforceCodeStyleInBuild=true` (l.5) — strict, but see the CI-matrix caveat
  above.
- `ManagePackageVersionsCentrally=true` (l.7) — **real Central Package
  Management is on**. `Directory.Packages.props` uses proper `<PackageVersion>`
  elements (e.g., `<PackageVersion Include="DryIoc.dll" Version="5.4.3" />`).
- `<RuntimeIdentifiers>` (l.11) lists 10 RIDs: `win-x64`, `win-x86`, `osx-x64`,
  `osx-arm64`, `linux-x64`, `linux-musl-x64`, `linux-arm`, `linux-musl-arm`,
  `linux-arm64`, `linux-musl-arm64`. Note: **no `win-arm64`** despite the build
  exploring "Windows ARM64" — that platform is not currently produced.
- `AssemblyVersion=10.0.0.*` (l.77) — placeholder; CI overrides.
- `GenerateDocumentationFile=true` + `NoWarn=CS1591` (l.35-39) — XML doc file
  is produced, but the "missing XML doc on public member" warning is
  suppressed wholesale. The XML file is therefore mostly empty descriptions.
- `RootNamespace` remap to preserve `NzbDrone.*` (l.97-99).
- StyleCop wired up centrally with a guard for `EnableAnalyzers`
  (l.111-119) — so CI can disable per matrix leg.

Package list (highlights from `Directory.Packages.props`):

- DryIoc 5.4.3 (DI).
- Dapper 2.0.151.
- Servarr-forked FluentMigrator 3.3.2.9 (SQLite + Postgres runners).
- Servarr-forked `System.Data.SQLite.Core.Servarr 1.0.115.5-18`.
- Servarr-forked `Mono.Posix.NETStandard 5.20.1.34-servarr22`.
- Lidarr-forked `TagLibSharp-Lidarr 2.2.0.19`.
- NLog 5.1.4, NLog.Targets.Syslog 7.0.0.
- Newtonsoft.Json 13.0.3 + System.Text.Json 6.0.9 — **both** present.
- RestSharp 106.15.0 — last major before RestSharp 107's breaking rewrite;
  pinned to the legacy major.
- Polly 8.3.1.
- FluentValidation 9.5.4.
- SixLabors.ImageSharp 3.1.4.
- Sentry 4.0.2 (.NET).
- Selenium 3.141.0 + ChromeDriver 91.0.4472.10100 — several years old.

### 6.4 Packaging & distribution

- **Windows**: `distribution/windows/setup/` contains InnoSetup script(s);
  CI runs InnoSetup 6.2.0 (`azure-pipelines.yml:20`) to produce the installer
  `.exe`. Windows builds are Authenticode-signed.
- **macOS**: `distribution/osx/` contains the `.app` template and DMG creation
  scripts. Both Intel (`osx-x64`) and Apple Silicon (`osx-arm64`) builds.
- **Linux**: ships as `.tar.gz` archives per RID — no `.deb`/`.rpm`.

### 6.5 Coding standards

**StyleCop** is wired in via analyzer NuGet
(`Directory.Build.props:111-119`). `src/Stylecop.ruleset` (115 lines) sets
actions per rule. Highlights:

- SA1101 (`PrefixLocalCallsWithThis`) → `None`. **Inverted** via
  SX1101 (`Do not prefix local members with this`) → `Warning` (l.112) —
  the project actively bans `this.` prefix on instance members.
- Documentation rules SA1600–SA1652 are all `None` (l.55-107) — public APIs
  are not required to have XML doc comments. Pairs with the
  `NoWarn=CS1591` setting and ironically `GenerateDocumentationFile=true`.
- Naming rules SA1300, SA1301, SA1303, SA1304, SA1306, SA1309, SA1310 →
  `None` (l.26-32) — allows non-standard naming like underscore-prefixed
  fields (also encoded by SX1309 `_` for instance fields).
- Maintainability rule SA1402 (`FileMayOnlyContainASingleType`) → `None`
  (l.38).

`src/stylecop.json` enforces:

- 4-space indentation (no tabs).
- Newline required at end of file.
- `using` directives outside namespace, system usings first.

**EditorConfig** at the repo root applies cross-language formatting (final
newline, trim trailing whitespace, indent style/size).

**`Readarr.sln.DotSettings`** ships team ReSharper conventions in-repo.

**Frontend lint** (`frontend/.eslintrc.js`, 393 lines):

- `react/prop-types: 2` for `.js`, `off` for `.ts/.tsx` (l.317, 365).
- `react-hooks/rules-of-hooks: 'error'` and `react-hooks/exhaustive-deps:
  'error'` (l.322-323) — hooks usage is fully linted.
- `prettier/prettier: 'error'` for `.ts/.tsx` (l.366); only `off` for
  generated `*.css.d.ts` (l.388). The TS migration brings Prettier with it.
- `simple-import-sort`, `filenames/match-exported`, plus a long house style
  (single quotes, semicolons, 2-space indent, no nested ternaries, max
  depth 5).

No `husky` / `lint-staged` / pre-commit hooks — lint runs in CI only.

### 6.6 Testing infrastructure

NUnit 3.14.0 + Moq 4.17.2 across all `*.Test` projects; FluentAssertions
5.10.3, NBuilder 6.1.0, AutoFixture 4.17.0 in selected ones.
`coverlet.collector` is the coverage driver, configured by
`src/coverlet.runsettings` (no enforced threshold).
`src/postgres.runsettings` swaps the connection string so the same tests can
exercise Postgres.

`NzbDrone.Automation.Test` uses Selenium WebDriver against headless Chrome —
ChromeDriver 91 pins the Chrome major version, and Selenium 3.141 has been
unsupported upstream since the 4.x line. This is the only frontend-touching
test surface in the repo.

### 6.7 Governance & meta files

| File                  | Summary                                                       |
|-----------------------|---------------------------------------------------------------|
| `LICENSE.md`          | GPL v3 (full text, ~35 KB)                                    |
| `CLA.md`              | States there is **no** CLA — GPL v3 inbound = outbound. The fork dropped upstream's |
| `CODE_OF_CONDUCT.md`  | Contributor Covenant. Enforcement contact is the maintainer via private advisory, not upstream's dead mailbox |
| `CONTRIBUTING.md`     | Librarr-specific: branch off `main`, full `./build.sh`, and what happens when a PR is landed by cherry-pick |
| `SECURITY.md`         | Vuln-report pointer — GitHub advisories, not the retired Servarr channel |
| `README.md`           | Opens with the current release banner, then install / migration guidance |
| `schemas/torznab.xsd` | Torznab feed schema consumed by Newznab/Torznab indexer code  |

---

## 7. Conventions & standards

**Backend naming.** Per-domain, the file layout is consistent across `NzbDrone.Core`:

```
NzbDrone.Core/{Domain}/
├── Model/                       Domain POCOs (Book.cs, Author.cs, Edition.cs)
├── {Entity}Repository.cs        Persistence
├── {Entity}Service.cs           Application service / facade
├── {Entity}Cache.cs             Optional in-memory caches
├── Commands/                    *Command, ICommand impls
├── Events/                      *Event, IEvent impls
│   └── Handlers/                IHandle<*Event> classes
└── …
```

`Readarr.Api.V1/{Domain}/`:

```
Readarr.Api.V1/{Domain}/
├── {Entity}Controller.cs        REST controller
├── {Entity}Resource.cs          DTO
└── …
```

Common suffixes: `Service`, `Repository`, `Controller`, `Resource`,
`Command`, `Event`, `Handler`, `Specification`, `Proxy`, `Provider`,
`Definition`, `Factory`.

**Frontend naming.** PascalCase folder per component; CSS module imported as
`styles`; redux container suffixed `Connector` (e.g.,
`AuthorIndexConnector.js`). Action types use `SCREAMING_SNAKE_CASE`.

**Style enforcement.**

- C#: StyleCop analyzers run as part of build; `TreatWarningsAsErrors=true`
  fails compilation on any warning. The **CI matrix gates analysis only on
  the Linux leg**, so Windows/Mac builds will tolerate style violations.
- Frontend: ESLint + Stylelint runnable via `yarn lint` / `yarn stylelint-*`
  scripts (`package.json:11-14`). Not gated by a pre-commit hook.

**Threading.**

- Backend command queue uses a single background worker thread per command
  type; `EventAggregator` dispatches events synchronously on the publisher
  thread by default (async handlers opt in via `IHandleAsync`).
- ASP.NET Core controllers are async/await throughout; Kestrel
  `AllowSynchronousIO = false` enforces it (`Bootstrap.cs:179`).

---

## 8. Contradictions, antipatterns, smells

Cited so future cleanup can be precise.

### 8.1 Identity contradictions (the Sonarr fork is showing)

- **`NzbDrone.*` namespaces ↔ `Readarr.*` assemblies / csproj.** Set
  deliberately via the `RootNamespace` rewrite at
  `src/Directory.Build.props:97-99`. Newcomers will be confused; `using
  Readarr.Core;` won't compile, but `using NzbDrone.Core;` will.
- **`Stylecop.ruleset:1` describes itself as "Rules for Radarr"** — the
  ruleset was forked from Radarr without retitling.
- **Bootstrap assembly list mixes new + old names**
  (`Bootstrap.cs:37-44`): all entries are `Readarr.*`, but the namespace
  they're discovered into is `NzbDrone.Host`.
- **Folder names use `NzbDrone.*`** while the project elements inside are
  `Readarr.*.csproj` (e.g. `src/NzbDrone.Core/Readarr.Core.csproj`).

### 8.2 Mid-migration dual stacks (frontend)

- **JS ↔ TS coexist.** ~985 `.js` files vs ~375 `.ts` + 36 `.tsx`.
  PropTypes is enforced on the JS side (`eslintrc.js:317`) and turned off on
  the TS side (`eslintrc.js:365`). Some files mix both.
- **Class ↔ hooks coexist.** 136 hook callsites across 28 files, alongside a much larger
  fleet of class components. `react-hooks` is fully linted, so new code is
  expected to use hooks.
- **Two thunk patterns coexist.** `redux-thunk 2.3.0` is declared in
  `package.json:84`, but the dominant pattern is `Store/thunks.js`'s custom
  `createThunk` / `handleThunks`. Adding a new feature requires picking one.
- **`@types/react 18.2.79` and `@types/react-dom 18.2.25` in
  `package.json:37-38`**, but actual `react` and `react-dom` are pinned to
  `17.0.2`. The types describe a React version newer than what's installed.
- ~~**Volta vs CI Node mismatch.**~~ Resolved 2026-08-01. Volta, `.nvmrc`,
  `engines` and both workflows now all say Node 24. The divergence this
  warned about was real and had already bitten once: `@testing-library/jest-dom`
  6.10.0 requires Node >=22, installed cleanly on a developer's newer Node,
  and took down three CI jobs on Node 20 (`aef3e1f`).

### 8.3 Build/lint enforcement gaps

- **StyleCop only on Linux CI.** `azure-pipelines.yml:79,83,87` sets
  `enableAnalysis: true` for Linux, `false` for Mac and Windows. Two thirds of
  the matrix tolerate style violations.
- **`GenerateDocumentationFile=true` + `NoWarn=CS1591` + `SA1600`/SA1601 →
  `None`.** XML doc files are produced but missing-doc warnings are
  suppressed and the SA rules requiring docs are off — so the generated XML
  is mostly empty.
- **No pre-commit hooks.** No `husky`, `lint-staged`, or `pre-commit`
  manifest in the tree. Lint runs only in CI.
- **No coverage thresholds.** `coverlet.runsettings` has no fail-under
  setting — coverage can drift unmonitored.

### 8.4 Provider-model duplication

The `ThingiProvider` plugin model is good in spirit, but the concrete
hierarchies suffer from:

- **Deep inheritance** — `IIndexer` → `IndexerBase` → `HttpIndexerBase` →
  `TorznabIndexerBase` → `Torznab` (specific). Same shape for
  `DownloadClientBase` → `TorrentClientBase` → `QBittorrent`.
- **Copy-paste proxies.** Download clients each have a "proxy" class
  (`QBittorrentProxy`, `SabnzbdProxy`, `TransmissionProxy`, …) that
  reimplements the same RestSharp request → response → exception-wrap loop.
- **Largest provider files** (line counts approximate):
  - `Download/Clients/QBittorrent/QBittorrent.cs` ~725 LoC.
  - `Download/Clients/Sabnzbd/Sabnzbd.cs` ~539 LoC.
  - `Download/Clients/DownloadStation/TorrentDownloadStation.cs` ~459 LoC.

### 8.5 God classes and big files

| File                                                            | Approx LoC |
|-----------------------------------------------------------------|------------|
| `NzbDrone.Core/MetadataSource/BookInfo/BookInfoProxy.cs`         | ~993       |
| `NzbDrone.Core/Parser/Parser.cs`                                 | ~905       |
| `NzbDrone.Core/Download/Clients/QBittorrent/QBittorrent.cs`      | ~725       |
| `NzbDrone.Core/MediaFiles/Calibre/CalibreProxy.cs`               | ~682       |
| `NzbDrone.Core/MediaFiles/BookImport/ImportApprovedBooks.cs`     | ~575       |
| `NzbDrone.Core/MediaFiles/MediaFileService/FileNameBuilder.cs`   | ~578       |
| `NzbDrone.Core/MediaFiles/AudioTag.cs`                           | ~553       |
| `frontend/src/Book/Index/BookIndex.js`                           | ~500       |
| `frontend/src/Author/Index/AuthorIndex.js`                       | ~500       |

`Parser.cs` is the highest fragility risk — a single regex change can break
many release-name patterns at once.

### 8.6 Specific dated/unsafe choices

- **jQuery in a 2024-vintage React app.** `Utilities/createAjaxRequest.js`
  is the only consumer, so it's containable, but jQuery is in every bundle.
- **Selenium 3.141.0 + ChromeDriver 91** for the UI automation suite.
  Chrome 91 is years old and Selenium 4 has been the supported line since
  2021. The automation suite is therefore brittle and divergent from any
  current Chrome install.
- **Vendored Win32 interop DLL.** `src/Libraries/Interop.NetFwTypeLib.dll` is
  committed as a binary because the only way to call the Windows Firewall COM
  API from .NET is via this interop assembly. Provenance is opaque from a
  pure source-only audit.
- **`Newtonsoft.Json` *and* `System.Text.Json`** both ship. Migration to
  STJ is partial; many model converters still target `JsonConverter` from
  Newtonsoft.
- **`ImplicitUsings` / `Nullable` are not enabled** in
  `Directory.Build.props`. Pre-net6 idioms — extra `using` lines and no
  null-safety annotations. Enabling them would require a sweep but would
  catch real bugs (especially in the messaging and provider layers where
  `null` flows freely).
- **macOS-11 CI image** is on Azure's deprecation list (per their
  hosted-agent lifecycle). Builds will start failing when the image is
  removed.
- **`win-arm64` is not in the RID list** even though Windows on ARM has been
  shipping since 2021. Worth a note for Surface Pro X / WSL2-ARM users.

### 8.7 Smell: namespace placeholders and stub files

- `tsconfig.json` at the repo root is a 3-line stub that just `extends`
  `frontend/tsconfig.json`. Harmless, but easy to mistake for the real
  config.
- `Directory.Build.targets` (75 bytes) is essentially empty — its presence
  suggests intent that was never followed through.
- `CONTRIBUTING.md` (13 lines) is a redirect to the wiki; new contributors
  will look here first and bounce.

---

## 9. Open challenges

Aggregated from §8 and the repo-level survey.

1. **Twin migrations stall the frontend story.** JS→TS and class→hooks are
   both partial. Each new component requires a style decision; mixed files
   discourage refactors. Either commit to finishing one migration or
   freeze the other.
2. **Provider proliferation without abstraction.** Each new download
   client / indexer / notification produces a near-clone of the previous
   one. A shared `IProxyClient` for RestSharp boilerplate would shave
   hundreds of duplicate lines.
3. **`Parser.cs` regex fragility.** Release-name patterns shift constantly;
   `Parser.cs` is regex-only with no test fixture documenting which patterns
   matter most. Crowdsourced edge cases land in this file and rarely get
   refactored.
4. **Dual-DB dialect drift.** Every new query has to be valid SQLite AND
   Postgres. Date/time handling in particular has dedicated migrations
   (e.g., for Postgres `timestamptz`). Without a regression suite that runs
   on both, dialect drift is easy to ship.
5. **Selenium suite is years behind.** Selenium 3 / Chrome 91 means the
   automation tests test a fictional browser. Either upgrade in lockstep
   with Chrome or retire and replace with Playwright.
6. **macOS-11 retirement** will break CI when Azure removes the image. Plan
   the macos-12/macos-13 bump now.
7. **No SBOM / no dependency scanning.** Several vendored forks
   (`Servarr.FluentMigrator`, `System.Data.SQLite.Core.Servarr`,
   `TagLibSharp-Lidarr`) plus a vendored Win32 interop DLL mean the supply-
   chain story is opaque. Adding `--vulnerable` scanning to CI is cheap.
8. **CI matrix's split StyleCop gating** can let style regressions land if
   a contributor only ever hits the Windows/Mac legs.
9. **`NzbDrone.*` namespace retention** is a paper cut every time a new
   contributor reads the code. Either complete the rename or document the
   "namespace = NzbDrone, identity = Readarr" rule somewhere obvious (this
   doc is now one such place).
10. **Project is retired.** Upstream development stopped on 2025-06-27 with
    the "Retirement announcement" commit. The repo is archived on GitHub —
    issues and PRs are closed. Metadata source has become unusable and the
    community Open-Library transition stalled (per the retirement notice
    in `README.md:1-20`). Any future work happens in third-party forks (the
    notice points to `rreading-glasses` as the most popular mirror).
    The legacy "currently in beta testing" disclaimer further down in the
    README never made it to a stable release before retirement.

---

## 10. How to find your way around

| Want to…                                            | Go to                                                                |
|-----------------------------------------------------|----------------------------------------------------------------------|
| Add an indexer                                       | `src/NzbDrone.Core/Indexers/` — extend `HttpIndexerBase`              |
| Add a download client                                | `src/NzbDrone.Core/Download/Clients/` — extend `TorrentClientBase` or `UsenetClientBase` |
| Add a notification                                   | `src/NzbDrone.Core/Notifications/` — extend `NotificationBase`        |
| Add an import list                                   | `src/NzbDrone.Core/ImportLists/` — extend `HttpImportListBase`        |
| Add a database column                                | `src/NzbDrone.Core/Datastore/Migration/0XX_Whatever.cs` (FluentMigrator), plus update the model |
| Add an API endpoint                                  | `src/Readarr.Api.V1/{Domain}/{Entity}Controller.cs` + matching `{Entity}Resource.cs` |
| Add a background job                                 | `src/NzbDrone.Core/Jobs/` + define an `ICommand` and `IExecute<TCommand>` handler |
| Add a SignalR push                                   | `src/Readarr.Api.V1/{Domain}/{Entity}ControllerWithSignalR.cs` pattern; publish an event |
| Add a UI page                                        | `frontend/src/{Feature}/` + route in `frontend/src/App/AppRoutes.js`  |
| Add a Redux slice                                    | `frontend/src/Store/Actions/{feature}Actions.js` using the `Creators/` factories |
| Trace a request end-to-end                           | `Bootstrap.cs` → `Startup.cs` → `Readarr.Http` middleware → `Readarr.Api.V1/{Entity}Controller` → `NzbDrone.Core/{Domain}/{Entity}Service` → `Datastore/{Entity}Repository` |
| Find StyleCop rules                                  | `src/stylecop.json` + `src/Stylecop.ruleset`                          |
| Change the build matrix                              | `azure-pipelines.yml`                                                 |
| Change the bundled RID list                          | `src/Directory.Build.props:11`                                        |

Per-directory `README.md` files inside each major project / folder repeat
the relevant slice of this map locally.
