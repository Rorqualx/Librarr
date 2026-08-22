# CLAUDE.md

Project memory for Claude sessions working in this repo.

## Project

**Librarr** — ebook/audiobook collection manager, the OpenLibrary-based
continuation of the archived Readarr (Servarr-family sibling of Sonarr,
Radarr, Lidarr). Forked from Readarr at upstream `develop` HEAD
`0b79d300` ("Retirement announcement", 2025-06-27). Currently at
**`1.2.2-beta`** — engineering gate cleared, see
[`CHANGELOG.md`](CHANGELOG.md). The previous upstream tagged release was
`v0.4.18.2805` (commit `7cc02f95`, 2025-06-10).

Full architecture map: **`ARCHITECTURE.md`** at repo root (including a
"Librarr fork additions" section), plus per-folder `README.md` files in
every major directory.

## Identity quirk (read this first)

The csproj/assembly names are `Readarr.*` but C# **namespaces are still
`NzbDrone.*`** — set deliberately in `src/Directory.Build.props:97-99` via
a `RootNamespace` rewrite. `using Readarr.Core;` will NOT compile —
`using NzbDrone.Core;` will. Don't "fix" this; it's intentional. The
`Stylecop.ruleset:1` file even still labels itself "Rules for Radarr"
from when the ruleset was forked over.

Also intentionally kept as `readarr`/`Readarr`:

- **On-disk identifiers** in
  `src/NzbDrone.Common/Extensions/PathExtensions.cs:15-26` — `readarr.db`,
  `readarr.restore`, `readarr_update`, `readarr_backup`,
  `readarr_appdata_backup`, `Readarr.Update`. Renaming would break
  `LegacyMigrationService` (expects `readarr.db` as input) and any
  existing install upgrading in place.
- **Binary names** produced by csproj — `Readarr.exe`,
  `Readarr.Console.exe`, macOS `CFBundleExecutable=Readarr`. These
  follow the csproj names above.
- **Cross-app icons** under
  `frontend/src/Content/Images/Icons/logo-{readarr,sonarr,radarr,lidarr,prowlarr}.png`
  — these display *other* Servarr family members in the UI, not us.

## Common commands

```bash
# Frontend
yarn install                                    # install deps (root package.json)
yarn start                                      # webpack --watch
yarn build                                      # one-shot → _output/UI
yarn lint                                       # ESLint
yarn stylelint-linux                            # Stylelint over CSS
yarn test:frontend                              # vitest (jsdom) — one-shot
yarn test:frontend-watch                        # vitest in watch mode

# Backend
./build.sh --backend --enable-extra-platforms   # full backend build (multi-RID)
./test.sh                                       # backend tests
dotnet test src/NzbDrone.Core.Test/             # one test project
dotnet run --project src/NzbDrone.Console/      # run on :8787 (HTTPS :6868)

# Single test
dotnet test src/NzbDrone.Core.Test/ --filter "FullyQualifiedName~MyClassTests"
```

CI uses the same scripts. **Note:** StyleCop only enabled on the Linux CI
leg (`azure-pipelines.yml:79`); Mac/Windows skip it.

## Stack (verified 2026-07-30 — re-check before trusting)

Every figure below was counted, not remembered. The previous version of
this section had drifted badly (see the TypeScript note), and a precise
number that is wrong is worse than no number, because it gets trusted.

- **Backend:** .NET 10 LTS (`dotnetVersion: '10.0.302'`, `azure-pipelines.yml:28`,
  pinned in `global.json`),
  ASP.NET Core, **DryIoc 5.4.3** DI (`src/NzbDrone.Host/Bootstrap.cs:9-10,90`),
  custom Dapper-based ORM in `NzbDrone.Core/Datastore/`, Servarr-forked
  FluentMigrator (`Servarr.FluentMigrator.Runner 3.3.2.9`; **48 migrations**,
  latest `047_root_folder_audiobook_quality_profile`), dual **SQLite +
  PostgreSQL**, NLog logging, **Sentry 4.0.2**. Shipping version
  `1.2.2-beta` (`azure-pipelines.yml:22`). `Directory.Build.props:77`
  `AssemblyVersion 10.0.0.*` is the historical Readarr placeholder the CI
  overwrites at build time; not the shipping version.

  Migrated from .NET 6 (EOL 2024-11-12) on 2026-07-30. Supported to
  2028-11-14. The Servarr-forked packages did **not** need replacing —
  `Servarr.FluentMigrator.Runner 3.3.2.9`,
  `System.Data.SQLite.Core.Servarr` and `Mono.Posix...-servarr22` all run
  on .NET 10 unchanged, and all 48 migrations apply.

  **The trap that migration exposed:** ASP.NET Core 10 no longer infers
  `[FromBody]` for complex parameters on controllers that opt in via
  `IApiBehaviorMetadata` (which is how `V1ApiControllerAttribute` works).
  Every write action silently bound an all-default model and failed
  validation. All 39 write actions now carry explicit `[FromBody]` /
  `[FromQuery]`, matching Sonarr v5. **Any new POST/PUT action must
  annotate its parameters explicitly** — inference will not save you.
- **Frontend:** **React 18.3.1** (real `createRoot` root API —
  `frontend/src/bootstrap.tsx:3,18` — not legacy mode) + Redux 4.2.1 with
  **legacy `createStore`**, not RTK. `react-redux` is still 7.2.4. Webpack 5,
  CSS Modules via PostCSS, `@microsoft/signalr`.

  **TypeScript migration is ~6% done, not ~29%.** The old figure counted
  353 auto-generated `.css.d.ts` files as hand-written TypeScript. Actual
  hand-written source: **1004 `.js` / 32 `.ts` / 36 `.tsx`**. Hooks appear
  136 times across just 28 files; the codebase is still overwhelmingly
  class components.
- **Tests:** NUnit + Moq + FluentAssertions; **2789 passing** in
  `NzbDrone.Core.Test`. Selenium + ChromeDriver in
  `NzbDrone.Automation.Test` is years out of date — treat as historical,
  and it is the one suite still running nowhere.

  **The gated suites now run in CI** (as of 2026-08-02; before that they
  ran in no job at all and rotted silently). The env gates still exist —
  they keep a bare `dotnet test` fast — but CI sets them:

  | Suite | Where it runs | Blocking? |
  |---|---|---|
  | `NzbDrone.Playwright.Test`, 10 local-only tests | `build.yml` per push | **yes** |
  | `NzbDrone.Playwright.Test`, 6 `RequiresOpenLibrary` | `build.yml` per push | no |
  | `Category=IntegrationTest` minus the integration suite (HttpClientFixture's 54) | `build.yml` per push | no |
  | `NzbDrone.Integration.Test` (94) | `nightly-integration.yml`, one fixture at a time | n/a (scheduled) |

  Anything touching a third party is non-blocking on purpose — red should
  mean "we broke it", not "their server is down".

  **Do not move the integration suite into the push pipeline.** Running
  its fixtures in one pass gets the source IP refused by openlibrary.org
  (26 of 88 tests, refusals persisting for minutes) because each fixture
  starts with empty appdata and re-fetches from scratch. That is why the
  nightly runs one fixture per invocation with a pause between them.

  **Frontend: vitest + @testing-library/react + jsdom**, `yarn
  test:frontend`, config in `frontend/build/vitest.config.mjs`. Tests are
  `*.test.js` next to the component. Coverage is *thin* — four files,
  24 tests, all on the dual-format and Add Author surfaces. Treat any
  other component as untested.

  One thing in that config is load-bearing and non-obvious: JSX inside
  `.js` needs a `transform` plugin, not the `esbuild: { loader, include }`
  option — that option's `include` replaces Vite's default and silently
  stops `.ts`/`.tsx` being compiled.

  (The setup file also creates `#root` and `#portal-root`. That used to be
  order-sensitive — `Portal.js` resolved `#portal-root` into `defaultProps`
  at import time, so an element created later was too late. The
  `defaultProps` migration made it a default parameter, evaluated per
  render, and the ordering constraint is gone.)

## Conventions

- **Strict build:** `TreatWarningsAsErrors=true`,
  `EnforceCodeStyleInBuild=true` (`src/Directory.Build.props:4-5`).
- **Backend file layout** per domain under `NzbDrone.Core/{Domain}/`:
  `Model/`, `{Entity}Repository.cs`, `{Entity}Service.cs`, `Commands/`,
  `Events/` (with `Handlers/`).
- **REST:** `Readarr.Api.V1/{Domain}/{Entity}Controller.cs` +
  `{Entity}Resource.cs` DTO. Manual mapping (no AutoMapper).
- **Frontend:** PascalCase folder per component, `Foo.js` + `Foo.css` +
  `FooConnector.js`. PropTypes for `.js`, TS types for `.ts/.tsx`
  (`react/prop-types: 2 / off` in `frontend/.eslintrc.js:317,365`).
- **Provider plugins** (indexers, download clients, notifications, import
  lists, metadata): all derive from a `ThingiProvider`-rooted base and
  are auto-discovered by DryIoc reflection — no manual registration.
- **Messaging:** `EventAggregator` (in-process pub/sub) + `CommandQueueManager`
  (DB-backed background queue). Handlers implement `IHandle<TEvent>` /
  `IExecute<TCommand>`.

## Where to add things

| Task | Location |
|---|---|
| New indexer | `src/NzbDrone.Core/Indexers/` — extend `HttpIndexerBase` |
| New download client | `src/NzbDrone.Core/Download/Clients/` — extend `TorrentClientBase` or `UsenetClientBase` |
| New notification | `src/NzbDrone.Core/Notifications/` — extend `NotificationBase` |
| New import list | `src/NzbDrone.Core/ImportLists/` — extend `HttpImportListBase` |
| New DB column | `src/NzbDrone.Core/Datastore/Migration/0XX_Name.cs` (FluentMigrator) + update model |
| New API endpoint | `src/Readarr.Api.V1/{Domain}/{Entity}Controller.cs` + `{Entity}Resource.cs` |
| New background job | Define `ICommand` + `IExecute<TCommand>` handler; schedule in `src/NzbDrone.Core/Jobs/TaskManager.cs` |
| New UI page | `frontend/src/{Feature}/` + route in `frontend/src/App/AppRoutes.js` (no lazy loading) |
| New Redux slice | `frontend/src/Store/Actions/{feature}Actions.js`, use the `Creators/` factories |

## Gotchas

- **Dual SQLite + Postgres:** every new query must be valid on both. Use
  `WhereBuilderSqlite` / `WhereBuilderPostgres` for predicate translation.
  Date/time types in particular have dedicated Postgres migrations.
- **`Parser/Parser.cs` is ~905 lines of regex** — most fragile file. A
  single careless change breaks many release-name patterns. There is no
  golden-corpus test fixture; treat with respect.
- **jQuery `$.ajax`** is the only HTTP client on the frontend
  (`frontend/src/Utilities/createAjaxRequest.js`). Don't introduce `fetch`
  or `axios` without a plan to migrate the whole layer.
- **`NzbDrone.Windows` vs `NzbDrone.Mono`** — platform shims, only one is
  active at runtime. Don't reference them directly from `Core/`; let DI
  pick via `OsInfo.IsWindows`.
- **`win-arm64` is intentionally NOT in the RID list**
  (`src/Directory.Build.props:11`). Windows-on-ARM is unsupported.
- **`config.xml` reload-on-change is disabled** (`Bootstrap.cs:237`) —
  changes to bootstrap config require a restart.
- **No pre-commit hooks** — lint runs in CI only.
- **Upstream Readarr is retired** — any changes here diverge from the
  rest of the Servarr ecosystem. Librarr is the continuation; the
  fork swaps `bookinfo.club` for native OpenLibrary as the primary
  metadata source. Do **not** add a `rreading-glasses` dependency —
  the fork is committed to direct OL.

## Process modes

`Bootstrap.GetApplicationMode` (`src/NzbDrone.Host/Bootstrap.cs:186-227`)
picks one of: `Help`, `RegisterUrl`, `InstallService`, `UninstallService`,
`Service` (Windows service), `Interactive` (tray/console). The
self-updater is a separate exe (`src/NzbDrone.Update/`).

## Default ports

- HTTP `8787`, HTTPS `6868` (`Bootstrap.cs:135-136`).
- Override via `config.xml` (`Port`, `SslPort`, `BindAddress`).

## Documentation here

- **`README.md`** — Librarr overview + "Migrating from Readarr" guide.
- **`CHANGELOG.md`** — Keep-a-Changelog format release notes.
- **`ARCHITECTURE.md`** — full code map; includes a "Librarr fork
  additions" section near the top that inventories everything new
  since upstream.
- **`MASTER-PLAN.md`** — strategic blueprint for the 12-phase revival
  (most of phases 0-11 are now shipped; 12 is post-1.0 backlog).
- **`METADATA-MIGRATION.md`** — historical sketch; superseded by
  the shipped `LegacyMigrationService` + `ReidentifyService`.
- **`src/*/README.md` and `frontend/src/*/README.md`** — per-folder
  signposts pointing back to `ARCHITECTURE.md` sections.
