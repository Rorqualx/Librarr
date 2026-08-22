# Librarr roadmap

Status as of the 1.2.2-beta line. Items are roughly ordered by
priority; nothing here is a hard commitment.

## Open work at a glance

Everything still open across this file and
[`release-checklist.md`](release-checklist.md), tiered by what actually
forces the ordering. The tier is about *what unblocks it*, not how big
it is.

| Tier | Meaning | Items |
|---|---|---|
| **1 — Gates 1.0.0-stable** | The stable tag cannot honestly be cut until these close. | Manual operator walkthrough steps 3–5 · 30-day beta soak with no critical regressions · integration suite runnable in one pass |
| **2 — Coverage debt** | Real gaps. Nothing breaks by waiting, but each one is a place where a regression would ship unnoticed. | Cross-browser Playwright (Chromium-only today) · 500-book reidentify seed · decide `NzbDrone.Automation.Test`'s fate · move crash reporting off `sentry.servarr.com` |
| **3 — Wants its own branch** | Understood, scoped, and deliberately not bundled with anything else. | `<Nullable>enable</Nullable>` · React 18 ecosystem dep refresh (`react-dnd@14`, `react-popper@1`, `react-virtualized@9`, `react-redux@7.2.4`) |
| **4 — Trigger-gated** | Not scheduled at all. Revisit only when a named condition fires. | OL bulk-data dump fallback (four triggers in [`ol-bulk-data.md`](ol-bulk-data.md)) |
| **Won't** | Decided against, recorded so nobody re-opens them by accident. | Namespace rename · rreading-glasses shim · CLA reintroduction |

Tier 1 is the only one with a "should have happened by now" quality.
Tiers 2–4 are healthy backlog.

**There is no Tier 0.** There was, briefly: a bus-factor deadline of
2026-08-14 requiring a second maintainer or a maintenance-mode
declaration. It came from `docs/governance.md`, which was written
2026-05-17 by expanding two `MASTER-PLAN.md` Phase 11 bullets into a
governance model for an organization that has never existed here —
four roles for one person, an approval threshold that 0 of 2 PRs and 0
of 7 migrations ever met, and a countdown whose only enforcement was
publishing a maintenance-mode notice that would have been false on the
day it was due. That document is deleted and the deadline with it.
See [`state-of-the-fork/README.md`](state-of-the-fork/README.md) for
what was kept.

Librarr is maintained by one person. That is a fact about the project,
not an outstanding task in it.

## Now (1.0.0-beta cycle)

- [x] **Field-tag reidentify pass** (Phase 5b). Landed in commit
  `a4acdc9`. `ReidentifyService.FileTagPass` walks every Book with files,
  reads `IMetadataTagService` tags, looks up OL by ISBN → ASIN →
  Title+Author, and overwrites any non-Manual existing mapping with a
  `BookIdMappingSource.FileTag` row at 0.97 / 0.92 / 0.78 confidence
  respectively. `ResolveOverride` pure helper extracted for testing.

- [x] **Dedicated low-confidence review UI** (Phase 9c). Landed.
  New API endpoint `/api/v1/metadata/lowconfidencemapping` (GET list,
  PUT manual override). New Settings → Metadata panel
  `LowConfidenceMappings` rendering rows with confidence < 0.70, with
  inline OL Work/Edition ID editing and "Save as Manual" button. The
  Phase 5 wizard's "done" copy now points at this panel instead of
  System → Logs. Manual rows are pinned at confidence 1.0 and are
  protected from overwriting by both reidentify pass and the file-tag
  pass (per `ReidentifyService.ResolveOverride`).

- [x] **Cover URL wiring for OpenLibrary** (Phase 4b). Landed. New
  helper `OpenLibraryCoverUrls` constructs `covers.openlibrary.org/b/id/…`
  and `…/a/id/…` URLs from the integer IDs in OL JSON. Edition mapper
  now emits `MediaCoverTypes.Cover`, author mapper emits
  `MediaCoverTypes.Poster`, work mapper backfills editions that lack
  their own cover. Sentinel negative IDs are filtered out.

- [x] **OpenLibraryDescriptionConverter coverage of edge JSON shapes**.
  Landed. Converter now handles array-of-strings (joined with newlines),
  `{text: ...}` legacy form, and nested-object `value`. Unexpected
  scalars / non-string array contents return null with a debug-level
  log. Nine-row regression fixture covers each shape.

- [x] **Self-contained Dockerfile** (Phase 9b skeleton). Landed. The
  original `Dockerfile` was renamed to `Dockerfile.prebuilt`; the new
  default `Dockerfile` is a 3-stage build (`sdk:10.0-alpine` →
  `node:24-alpine` → `aspnet:10.0-alpine` runtime). Compiles inside
  the image — no local toolchain needed. Runtime smoke (`docker build
  && docker run`) has since been done on x86_64, both locally and on a
  real server, and on **native aarch64** against the published
  `1.1.0-beta` image: healthcheck, UI, live OL search, unmapped-folder
  scan, Library Import and a full discography refresh, no errors.
  `linux/arm/v7` completed the same workload only under emulation,
  where QEMU's own Thumb translator then asserted
  (`target/arm/tcg/translate.c`) — an emulator defect, not an
  application one, and so still not a verdict either way on real
  32-bit ARM hardware.

## Soon (1.0.0 stable)

- [x] **Audnex augmenter wired into RefreshBookService**. Landed.
  `RefreshBookService.GetSkyhookData` calls
  `IAugmentAudiobookInfo.Augment` after the primary metadata source
  returns. CanAugment gates on the opt-in config flag, so disabled
  installs pay no cost. Augmenter failures swallow into Debug — the
  primary refresh path is never blocked.

- [x] **OpenLibraryAuthorImportList + OpenLibraryTrendingImportList**.
  Landed. Both follow OpenLibrarySubjectImportList's shape:
  `IHttpClient + IOpenLibraryRequestBuilder` injection, `Fetch()` calls
  one OL endpoint, validation probes with `limit=1`. Author list reads
  `/authors/{key}/works.json`; trending reads `/trending/{period}.json`
  with the period restricted to OL's documented set (now/daily/weekly/
  monthly/yearly/forever). DryIoc auto-discovers both via reflection
  on `ImportListBase` — no manual registration needed.

- [x] **Narrator field on Edition**. Landed. Migration 042 adds
  `Editions.Narrators` (nullable text, comma-separated). Edition model
  + EditionResource carry it through. Audnex augmenter now writes
  narrator names (joined on `, `). Book details header shows
  "Narrated by …" alongside page count when present. A normalized
  Narrators table is a future refactor — see the migration comment.

- [x] **Real-world OL JSON cassettes for the test suite**. 117 real
  OL captures are committed under
  `src/NzbDrone.Core.Test/Files/OpenLibrary/`; the capture recipe
  itself is automated by `scripts/capture-ol-cassettes.sh`.
  `OpenLibraryFixtureLoader` + the README in that directory document
  the corpus categories and re-capture procedure. (Earlier `[~]`
  marker was stale; finalization-pass audit confirmed the corpus
  is in tree.)

- [x] **Reidentify regression test**. `ReidentifyRegressionFixture`
  runs in the default suite (`[TestFixture]`, not `[Explicit]`). It
  seeds 10 books programmatically — 5 ISBN-13s + 5 title+author
  shapes — and drives the real `OpenLibraryProxy` against a
  cassette-backed `IHttpClient` stub, asserting the recorded
  mappings clear the 0.85 threshold. The earlier `[~]` was based on
  an outdated reading of the harness comment; a 500-book snapshot
  is documented as a future "stable gate" enhancement (see
  `docs/release-checklist.md`) but is not blocking.

## Later (1.1+)

The items below are documented in `docs/deferred-modernization.md`
with the specific reason each is deferred. They remain explicitly
deferred per the v1.0.0 release checklist (`docs/release-checklist.md`).
See the deferred-modernization doc for the assessment per item.

(This preamble used to add "and none are safely-completable in an
offline LLM session." The .NET runtime move below then was, which is
why the claim is gone rather than reworded — it was a guess about
difficulty dressed up as a constraint.)

- [x] **.NET LTS upgrade — landed 2026-07-30, on .NET 10 not .NET 8.**
  The target was wrong twice over. .NET 8 *and* .NET 9 both reach end
  of support on 2026-11-10, so landing on 8 would have bought about a
  quarter; .NET 10 LTS is supported to 2028-11-14. And the recorded
  blocker — that the Servarr-forked NuGets have no modern build — was
  never real: `Servarr.FluentMigrator.Runner 3.3.2.9`,
  `System.Data.SQLite.Core.Servarr`, `TagLibSharp-Lidarr` and
  `Mono.Posix.NETStandard ...-servarr22` all restore and run on .NET 10
  unchanged, and all 48 migrations apply. No package needed replacing.

  The predicted cost (an analyzer-triage slog under
  `TreatWarningsAsErrors`) came to ~11 unique issues. The real cost was
  something nobody predicted: ASP.NET Core 10 stopped inferring
  `[FromBody]` on controllers that opt in via `IApiBehaviorMetadata`,
  so every write endpoint bound an all-default model and failed
  validation — the app could read but not write, with 2764 unit tests
  green. 39 actions now carry explicit `[FromBody]`/`[FromQuery]`.
  Full writeup in [`deferred-modernization.md`](deferred-modernization.md).

- [ ] **Nullable enable**. Several-thousand-error build without
  per-file human triage; not a single-session task.

- [x] **React core 17 → 18**. Landed in `ae4261b` (Phase 10
  closeout). `react` + `react-dom` at 18.3.1, bootstrap rewritten to
  use `createRoot`. Build clean, full unit suite passes on React 18.

- [ ] **React 18 ecosystem dep refresh**. `react-dnd@14`,
  `react-virtualized@9`, and `react-popper@1` still pinned. They
  work on React 18 as-is (verified by the Phase 10 swap not
  breaking the build), but each replacement is a non-trivial diff
  with breaking API changes. Not blocking; surface for a future
  visual-regression pass once Playwright has interaction coverage.

- [x] **Selenium → Playwright**. Landed as
  `src/NzbDrone.Playwright.Test/`. Seven page-load smokes (the six
  ported from the Selenium suite, plus Library Import), each asserting
  a page-specific DOM anchor, with the base class failing any test
  that leaves an error in the UI's `#errors` panel. Opt-in behind
  `READARR_RUN_PLAYWRIGHT=1` because it needs a built backend, a built
  frontend, and a ~250 MB browser bundle on disk. Interaction and
  visual-regression coverage remain out of scope and still want the
  cassette work below.

- [x] **Playwright suite actually runs.** Green on the pinned 1.40.0,
  four consecutive clean runs. It was eight tests when this line was
  written; the suite is **16** across six fixtures today (10 local-only,
  6 gated on live OpenLibrary). Getting there fixed
  three things: `add_author_page` matched two elements on "Add New" and
  had presumably never passed; `library_import_page` clicked a sidebar
  child without expanding its section first; and the per-fixture browser
  lifecycle raced with `NzbDroneRunner.KillAll()`, which kills every
  Readarr by name — the browser and instance now live in `AssemblyGate`,
  one per assembly. Notes in
  [`src/NzbDrone.Playwright.Test/README.md`](../src/NzbDrone.Playwright.Test/README.md).

- [ ] **Decide what happens to `NzbDrone.Automation.Test`.** The
  Selenium project is still in the tree beside the Playwright one,
  on years-old Selenium 3 + ChromeDriver pins, running in no job.
  Either port the remaining cases or delete it — keeping both is the
  worst of the three options, because two suites imply coverage only
  one of them provides. This is a decision, not a task; it needs a
  maintainer to make the call, not a session to do the work.

- [ ] **Cross-browser Playwright.** Firefox + WebKit launchers are
  one-liners against `_AssemblyGate.cs:70`, which is Chromium-only
  today. Wire when theme/CSS regression coverage needs it. Also
  tracked as a 1.0-stable gate in
  [`release-checklist.md`](release-checklist.md).

- [ ] **500-book reidentify regression seed.** The 10-book in-memory
  seed asserts the 0.85 threshold; a larger snapshot makes the
  assertion statistically meaningful. Also a 1.0-stable gate.

- [ ] **Integration suite runnable in one pass.** The nightly runs one
  fixture per invocation with a pause between them, which works but is
  a workaround for the real problem: every fixture starts from empty
  appdata, so nothing is cached and a single full run gets the source
  IP refused by openlibrary.org. Two untried routes — share one warm
  appdata across the assembly the way `NzbDrone.Playwright.Test`
  shares its instance, or cassette the OL responses (the machinery
  exists in `OpenLibraryCassetteFixture`).

- [x] **Enable private vulnerability reporting on the repository.**
  One toggle in Settings → Security. It was off, which meant both
  `SECURITY.md` and `CODE_OF_CONDUCT.md` directed people to
  `/security/advisories/new` — a form non-maintainers cannot reach
  while the setting is off. `SECURITY.md` had pointed there since
  2026-05-19, so the channel was documented-but-closed for 76 days,
  during which the fork had no private reporting route at all:
  Discussions and Issues are both public and no email address is
  published. Found 2026-08-03 while replacing upstream's dead
  `development@readarr.com` contact.

  **Resolved 2026-08-03:** enabled;
  `GET /repos/Rorqualx/Librarr/private-vulnerability-reporting` now
  returns `{"enabled": true}`. The reporter-side button was not
  verified directly — that needs a second account without push access,
  since `gh`'s token does not authenticate github.com HTML — so the
  API state is the evidence. `SECURITY.md` was rewritten in the same
  pass: it still claimed `1.0.0-beta` was current and promised an
  initial response within seven days, which is not a commitment one
  maintainer working in multi-week bursts can honour. It now states no
  deadline and tells reporters to treat 14 days of silence as unseen
  and disclose if severity warrants it.

- [ ] **Point crash reporting somewhere Librarr owns.**
  `src/NzbDrone.Common/Instrumentation/NzbDroneLogger.cs:71-77` still
  ships upstream's Sentry DSNs at `sentry.servarr.com`, so a Librarr
  install that hits an unhandled exception reports it into the
  retired parent project's infrastructure. Nobody agreed to receive
  that traffic and nobody here can read it, which makes it both a
  courtesy problem and a wasted signal. Found 2026-08-02 while costing
  out the Q2 writeup.

  **Resolved 2026-08-03:** the hard-coded DSNs are gone. Sentry is only
  registered when `LIBRARR_SENTRY_DSN` names one, so by default nothing
  is sent anywhere. Left unchecked until it has been exercised on a
  real .NET 10 build — see the note in the commit.

- [ ] **OL bulk-data dump fallback**. Fork position + trigger
  conditions to revisit are in [`docs/ol-bulk-data.md`](ol-bulk-data.md).
  Phase 12+ candidate.

  **One trigger is worth re-checking:** the condition is "install count
  >100", and Docker Hub had served 370+ pulls by 2026-08-02. Pulls are
  not installs — CI re-pulls and each multi-arch manifest fetch counts
  — so this is a ceiling, not a count, and it is *not* being treated as
  fired. But it is the first time the figure has been in the right
  order of magnitude, so the next writeup should look at it rather than
  assume.

## Won't (until persuaded otherwise)

- [ ] Namespace rename NzbDrone.* → Librarr.*. Cosmetic, ~2000 file
  touch. Directory.Build.props:97-99 deliberately keeps the legacy
  namespace as a heritage signal.

- [ ] rreading-glasses shim adoption. The fork explicitly rejected
  this in favor of native OL — see Phase 0 design discussion.

- [ ] Reintroducing the CLA. The Librarr fork dropped the upstream
  CLA in favor of GPL inbound = outbound (CLA.md), and there's no
  current pressure to reverse that.

---

Reorder freely. Open a PR against this file with the rationale for
any priority changes.
