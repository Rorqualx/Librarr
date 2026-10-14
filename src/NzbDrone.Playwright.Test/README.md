# Readarr.Playwright.Test

End-to-end smoke suite, replacing the gated legacy Selenium suite
(`src/NzbDrone.Automation.Test/`, retired but kept for git blame).

Scope today: **17 tests**, in three tiers.

| Fixture | Tests | Needs |
|---|---|---|
| `MainPagesTest` | 7 | — |
| `NarratorPageTest` | 1 | — |
| `SettingsFormsTest` | 2 | — |
| `UrlBaseTest` | 1 | — (starts its own instance on 8788) |
| `AddAuthorSearchTest` | 2 | OpenLibrary |
| `BookDetailsTest` | 2 | OpenLibrary + a seeded library |
| `SeededLibraryTest` | 2 | OpenLibrary + a seeded library |

The first tier mounts a page and asserts a page-specific DOM anchor
exists. The second and third assert on rendered content, so they make a
real OpenLibrary call — see `LibrarySeeder` for why they degrade to
`Inconclusive` rather than failing when OL is unreachable.

`UrlBaseTest` is the regression point for serving under a URL base
(issue #67). It boots a second instance with `UrlBase=/readarr` and fails
if any request the page makes is outside `/readarr/` or comes back
4xx/5xx. That is the only way the bug shows: without a reverse proxy in
front, a root-relative asset still resolves, so the page renders fine.

Visual regression is still out of scope; that's gated on the OL cassette
work in the roadmap.

## Prerequisites

1. **A coherent app tree on disk.** The reliable way is one command:

   ```bash
   yarn install && ./build.sh
   ```

   A full `./build.sh` builds the backend, builds the frontend, and then
   copies `_output/UI` next to the binary. **No shorter command does all
   three**, and the partial ones interact badly:

   | Command | Effect |
   |---|---|
   | `./build.sh --backend` | rebuilds the app — and opens with `rm -rf _output`, so it **deletes** `_output/UI` |
   | `yarn build` | writes `_output/UI`, and nothing else — it never reaches the running app |

   The app serves the `UI/` folder beside its own binary, which only the
   packaging step populates. So iterating with the two partial commands,
   in either order, leaves the suite testing whichever frontend was
   copied there last. If you must iterate that way, copy it yourself:

   ```bash
   yarn build && cp -r _output/UI _output/net10.0/<rid>/
   ```

   `NzbDroneRunner` prints the binary it launched with its build time,
   and fails outright if `_output/UI` is newer than the copy beside the
   binary. Read that line before believing a failure — an app tree
   predating the feature under test produces failures that look exactly
   like bugs, and passes that look exactly like coverage.

3. **Playwright browser bundle.** Run the helper once per machine:

   ```bash
   # Linux / macOS
   ./scripts/playwright-install.sh
   ```

   ```powershell
   # Windows
   .\scripts\playwright-install.ps1
   ```

   The script restores the NuGet packages and then invokes the
   bundled `playwright install` tool to download Chromium into

   | OS | Cache |
   |---|---|
   | Linux | `~/.cache/ms-playwright/` |
   | macOS | `~/Library/Caches/ms-playwright/` |
   | Windows | `%LOCALAPPDATA%\ms-playwright\` |

   ~250 MB on first run; subsequent runs are cached. The macOS path
   is **not** `~/.cache` — installing there with
   `PLAYWRIGHT_BROWSERS_PATH` set downloads a browser Playwright will
   never look at, and the suite still fails as if nothing were
   installed.

   Browsers are cached per driver build, so bumping the
   `Microsoft.Playwright` version in `src/Directory.Packages.props`
   means re-running this script.

## Running

The suite is gated by `READARR_RUN_PLAYWRIGHT=1` (see
`_AssemblyGate.cs`). Without that env var, every test reports
`Skipped` — the default `dotnet test src/Readarr.sln` invocation
stays green.

**CI sets it.** Since 2026-08-02 the `Playwright smoke` job in
`build.yml` runs the whole suite on every push: the ten network-free
tests block the build, the six `RequiresOpenLibrary` ones run in a step
that is allowed to fail. Before that this suite ran in no job at all,
which is how a stale driver and a frontend that had been deleted both
went unnoticed. If you change the fixture layout, check that job — it
assembles the app tree by hand and copies the `.playwright` driver into
the publish folder, because neither is where a per-RID publish puts it.

```bash
# All 17
READARR_RUN_PLAYWRIGHT=1 dotnet test src/NzbDrone.Playwright.Test/

# Only the 11 that need no network. The six that seed a library through
# the API perform a live OpenLibrary fetch and carry the
# RequiresOpenLibrary category; filter on that rather than on fixture
# names, which go stale the moment a seeded fixture is added.
READARR_RUN_PLAYWRIGHT=1 dotnet test src/NzbDrone.Playwright.Test/ \
  --filter "Category!=RequiresOpenLibrary"

# Single test (mirrors the Selenium suite filter syntax)
READARR_RUN_PLAYWRIGHT=1 dotnet test src/NzbDrone.Playwright.Test/ \
  --filter "FullyQualifiedName~MainPagesTest.author_page"
```

Screenshots land in the working directory as
`{test_name}_test_screenshot.png`. They're per-run artefacts —
`.gitignore` excludes them from the repo.

## First-run friction

The suite runs green on the pinned `Microsoft.Playwright` **1.55.0**;
verified 2026-08-02 in CI, 16 tests (10 blocking + 6 live OpenLibrary),
across three consecutive runs on an ubuntu-22.04 runner.

**`Executable doesn't exist at .../chromium-XXXX/...`** means the browser
in the cache is not the one the driver wants. Almost always that is a
stale driver, not a bad download. `_tests/` is shared, keyed by target
framework and RID, and never cleaned, so it accumulates driver copies —
after the .NET 6 → 10 migration this tree held four, two of them 1.40.0
leftovers still asking for `chromium-1091`. Both install scripts now
select the driver whose version matches the pin in
`src/Directory.Packages.props` and print which one they chose; if none
matches, they list what they found and stop rather than installing a
browser the tests will not use. `_AssemblyGate` makes the same check at
run time. If the scripts report only stale drivers, delete `_tests/` and
rebuild.

An earlier version of this file blamed that symptom on the 1.5x packages
shipping a node driver one revision ahead of their own .NET assembly.
That was wrong — driver and assembly agree, and both `install` and
`dotnet test` go through the same driver. The mismatch came from
`playwright-install.sh` picking whichever driver `find` reached first.

The other thing that will make you think it's broken is the first
download: it is slow and silent for long stretches. It does complete.
Interrupting it leaves a partial browser directory, which looks like a
present-but-broken install and produces the same message as above —
delete that directory under `~/Library/Caches/ms-playwright` (macOS) or
`~/.cache/ms-playwright` (Linux) and re-run the installer.

Both install scripts previously looked only under
`src/NzbDrone.Playwright.Test/bin`, which this repo never writes to
(`Directory.Build.props` redirects output to `_tests/`) — on Windows that
meant `playwright-install.ps1` never found the CLI at all. They now
search `_tests/`, and the bash one drives Playwright's own bundled Node
directly rather than requiring PowerShell.

## One instance per assembly

The browser and the Librarr process are owned by `AssemblyGate`, not by
`PlaywrightTestBase`, and that is deliberate. `NzbDroneRunner.KillAll()`
kills every Readarr process by name rather than only its own, and every
fixture wants port 8787 — so with a per-fixture lifecycle, one fixture's
teardown shot down another fixture's instance and the suite failed
intermittently with `TargetClosedException` raised from `OneTimeSetUp`.
Booting once per assembly removes the race by construction, and takes
the suite from ~35s to ~3s.

The consequence to keep in mind when adding tests: **every test shares
one page and one database.** Tests must navigate to where they need to
be rather than assuming a starting location, and must not depend on the
library being empty.

## Why a separate project (not extending Automation.Test)

* Selenium 3.141 + ChromeDriver 91 in the legacy project literally
  won't load on a 2026-era Chrome — the package versions are pinned
  there for git-archaeology purposes, not because they work.
* Playwright's NUnit integration is its own package
  (`Microsoft.Playwright.NUnit`) and pulls a different test SDK
  chain; collocating them risks DI conflicts.
* The legacy assembly gate (`READARR_RUN_AUTOMATION=1`) lets a
  forensic-minded user still exercise the Selenium harness if they
  want; this project's gate (`READARR_RUN_PLAYWRIGHT=1`) is the
  forward path.

## What this does NOT cover yet

* Login-required flows (the smokes hit the bootstrap state where
  authentication is off by default).
* Asserting page contents — only mount-time existence of an anchor
  element. Visual diffs need a cassette / golden-image strategy
  that's still ahead of us on the roadmap.
* Cross-browser. Chromium only; Firefox + WebKit launchers are
  Playwright one-liners but not wired in until they're needed.
