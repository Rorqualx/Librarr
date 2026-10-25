# Changelog

All notable changes to Librarr are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/),
and this project loosely follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.6-beta] — 2026-10-25

qBittorrent 5.2 and later connect again, with optional API key login.
Also two API requests that returned a server error now return a
validation error, plus dependency updates (Npgsql 10, NLog 6,
MonoTorrent 3).

### Added

- **qBittorrent API key.** qBittorrent 5.2 can issue an API key
  (Options → Web UI → Authentication). Paste it into the new **API Key** field of the
  qBittorrent download client and leave Username and Password empty.
  Librarr then authenticates with it instead of logging in.

### Fixed

- **qBittorrent 5.2 and later connect again** ([#101](https://github.com/Rorqualx/Librarr/issues/101)).
  The login check failed on qBittorrent 5.2's empty login response,
  Librarr sent a Basic auth header that 5.2 rejects, and a 401 wasn't
  treated as a login failure. Also, a wrong password could pass the
  connection test after one good login, because the session cookie went
  into a jar shared across the app. qBittorrent cookies are now kept
  out of that jar. Tested against qBittorrent 5.2 with a password and
  with an API key.
- **Saving a relative recycle bin path** now returns a validation error
  (HTTP 400) instead of a server error (HTTP 500)
  ([#96](https://github.com/Rorqualx/Librarr/issues/96)).
- **Saving a quality profile that sets a minimum custom format score but
  has no custom formats** now returns a validation error (HTTP 400)
  instead of a server error (HTTP 500)
  ([#97](https://github.com/Rorqualx/Librarr/issues/97)).
- **The log file API's `contentsUrl` works.** It pointed at
  `/api/v1//readarr.txt` (and `/api/v1/update/…` for update logs), both
  404. It now points at `/api/v1/log/file/…` and `/api/v1/log/file/update/…`.
  The Logs page uses the download links and was not affected
  ([#108](https://github.com/Rorqualx/Librarr/pull/108)).

### Dependencies

- Backend: Npgsql 10.0.3 (was 7.0; the PostgreSQL 14 and 18 test suites
  pass), NLog 6.2.2 (was 5.5; log file names, rotation and the Logs page
  are unchanged), MonoTorrent 3.0.2 (was 2.0; torrent info hashes are
  unchanged).
- Frontend: reselect 5.3.0.
- Build and test only: Microsoft.NET.Test.Sdk 18.11.0,
  Microsoft.SourceLink.GitHub 10.0.402, @typescript-eslint/eslint-plugin 7,
  postcss-loader 8.2.1, @babel/eslint-parser 7.29.9 and
  @babel/plugin-proposal-export-default-from 7.29.7.

## [1.2.5-beta] — 2026-10-14

A fix release for the blank page behind a reverse proxy in 1.2.4-beta,
plus dependency updates. Read the bold line under Changed before
upgrading if you create root folders through the API.

### Fixed

- **A URL base works again behind a reverse proxy** ([#67](https://github.com/Rorqualx/Librarr/issues/67)).
  1.2.4-beta showed a blank page at e.g. `example.net/readarr/`: the page
  asked for its scripts and stylesheets at `/index-….js` and
  `/Content/…` instead of under `/readarr/`. The webpack upgrade in that
  release minified `index.html` itself and dropped the quotes around
  attribute values, and the server only added the URL base to quoted
  ones. The build no longer lets webpack minify the HTML, and the server
  now adds the URL base whether or not a value is quoted.

### Changed

- **Saving a root folder with a quality profile that doesn't exist is now
  rejected** with "Quality Profile does not exist" (HTTP 400). Before, the
  check was skipped whenever an audiobook profile was also set, so an API
  request with a nonexistent profile id was saved. The UI only offers
  existing profiles and is not affected
  ([#81](https://github.com/Rorqualx/Librarr/pull/81)).
- Validation error responses from the API come from FluentValidation 11
  (was 9). `propertyName`, `errorMessage`, `attemptedValue`, `severity`
  and `isWarning` are unchanged; the always-empty
  `formattedMessageArguments` is gone, and
  `formattedMessagePlaceholderValues` gains `propertyPath`.
- Font Awesome 7.3.1 (was 6.7). A few icons look slightly different.

### Dependencies

- Backend: Microsoft.Extensions.* and SignalR client 10.0.13 (the
  caching and SignalR packages were still on 6.0), FluentValidation 11.12.0,
  ImpromptuInterface 8.1.0, System.IO.Abstractions 22.3.0, NLog 5.5.1,
  Sentry 4.13.0, Dapper 2.1.89, MailKit 4.18.1, Polly 8.8.0 and other
  minor and patch updates.
- Frontend: Font Awesome 7.3.1, react-google-recaptcha 3.1.0.
- Build and test only: FluentAssertions 7.2.2 (the last Apache-licensed
  major), vitest 4, Babel 7.29, postcss-mixins 12, Playwright 1.63, and
  re-resolved transitive npm packages that clear every critical
  `yarn audit` advisory.

## [1.2.4-beta] — 2026-10-12

The fixes from a full audit of the fork: security hardening, the Readarr
migration path, OpenLibrary handling, PostgreSQL, Windows and the Docker
image. Read the bold lines under Security and Changed before upgrading;
several defaults are different.

### Security

- **Librarr no longer talks to the retired Readarr project's servers.**
  Inherited code still asked `readarr.servarr.com` whether an update existed
  and trusted the package URL and hash it returned, fetched health-page
  banners from it, and used it for the system-time and proxy checks, reporting
  version, OS, architecture, runtime and database type along the way. That
  host is not ours: it answered "no update" to every Librarr version and
  listed Readarr 0.4.18 as the newest release, and whoever runs it could have
  offered any package. Now:
  - Update checks and System → Updates read this project's own
    [GitHub releases](https://github.com/Rorqualx/Librarr/releases). The
    package for your OS and architecture is picked locally and nothing about
    the install is sent. The hash comes from the release's `SHA256SUMS`; a
    package with no checksum entry is not offered.
  - Health-page notifications from upstream are gone.
  - The system-time and proxy checks use Open Library, which Librarr already
    talks to.
  - The web UI no longer reports browser errors to `sentry.servarr.com`
    (the backend half was fixed in 1.2.1-beta).

- **Passwords are stored with a slow, salted hash.** The login password was
  kept as a single unsalted SHA-256. It is now PBKDF2-HMAC-SHA512 with 210,000
  iterations and a per-user salt (migration 048). Existing passwords are
  upgraded the first time each user logs in; nobody has to reset anything. The
  API no longer returns the stored hash, only a `********` placeholder.
  Clients using Basic authentication pay for one derivation per request.

- **`X-Forwarded-*` headers are only believed from local and private
  addresses.** Any client could previously claim to be a local address and
  skip "authentication disabled for local addresses". A reverse proxy on the
  same host or LAN keeps working; **a proxy on a public address is no longer
  trusted, and there is no setting to trust one.**

- **Files served from disk stay inside their folder.** Every static file
  route (UI, covers, backups, logs, update logs) now refuses a path that
  resolves outside its root. On Windows a crafted URL could reach other files
  the service account could read.

- **Backup and update archives cannot write outside their target folder.**
  Zip and tar entries with `..` or absolute paths are refused.

- **A pinned cover must be an `https` image on `covers.openlibrary.org`.**
  The cover picker accepted any URL and the server fetched it.

- **API key comparison is constant-time.**

- **Windows installer:** the install folder's permissions are locked down to
  administrators and the service account, and the installer no longer removes
  an existing Readarr service unless run with `/REPLACESERVICE=1`.

- **The Goodreads notifications are removed.** "Goodreads Bookshelf" and
  "Goodreads Owned Books" posted Goodreads book ids, which a library on
  OpenLibrary does not have, and their sign-in went through
  `auth.servarr.com`, a server of the retired upstream project. **A saved
  notification of either type is deleted at startup**, with a `Removing`
  line in the log.

- **Dependency advisories:** the frontend advisories `yarn audit` reported
  (6 high, 11 moderate) were cleared, and MailKit/MimeKit, the JWT packages
  and ImageSharp were updated. Not everything is clear: Dependabot alerts,
  switched on for this release, list further advisories in npm packages used
  to build and test the UI, and ImageSharp 3.1.12 has open advisories fixed
  only in the 4.x line, which needs a licence key at build time.

### Changed

- **Automatic update installation is off by default on every platform.** It
  used to default to on for Windows. Release packages are checksummed but not
  signed, so installing them unattended is now opt-in (Settings → General →
  Updates, with advanced settings shown). **Windows installs that never
  changed this setting stop auto-installing after this upgrade**, because the
  old default was never written to `config.xml`. Updates are still offered in
  System → Updates and can be installed from there.

- **The Docker image drops root at start.** A container started as root now
  runs the app as `PUID`/`PGID` (default 1000:1000) and honours `UMASK_SET`
  and `TZ`. **A container that relied on running as root must set
  `PUID=0`/`PGID=0` or fix ownership of `/config`.** Containers started with
  `--user` behave as before. The compose example now uses the `:beta` tag.

- **Calibre root folders are ebook-only.** Audiobook files are refused there,
  an audiobook quality profile cannot be set on a Calibre root, and a health
  check reports roots configured that way. Calibre has no model for a
  multi-file audiobook, and the old behaviour deleted the wrong formats.

- **Readarr-to-Librarr id mappings below 0.70 confidence are not used until
  confirmed** on Settings → Metadata. They used to be applied silently.

- **Library Import's API now returns `{ imported, skipped }`** instead of the
  list of created authors.

- **Manually refreshing one author or book always asks OpenLibrary**, rather
  than serving the 7-day cache.

- **PostgreSQL is now tested in CI.** Every database test runs against
  PostgreSQL 14 and 18, and the built app is started on an empty PostgreSQL
  server so that all migrations are applied by the real host. Before this,
  nothing the fork added had ever been run on PostgreSQL.

- **Windows is now tested in CI** by a smoke job on `windows-2022`.

- **Release workflow:** the image is started and checked before it is pushed,
  the SBOM job really runs and can fail the build, third-party actions are
  pinned by commit, and Dependabot watches NuGet, npm and Actions.

- **Migration guide corrected:** the flag is `-data=`, not `--appdata=`; copy
  the Readarr data folder before installing; both services can coexist.

### Fixed

- **PostgreSQL: duplicate monitored editions were never cleaned up.** The
  housekeeping task that leaves one monitored edition per book ran a statement
  on PostgreSQL that changed nothing. On SQLite it fixed a book with two
  monitored editions but needed several runs for three or more. Both engines
  now leave exactly one (the most recently added) in a single run.

- **PostgreSQL: two timestamps were stored without a time zone.** The date of
  each Goodreads-to-OpenLibrary id mapping and the dates in the update history
  were shifted by the server's UTC offset when read back, on any PostgreSQL
  server not running in UTC. Migration 049 converts both columns; existing
  values are corrected as part of the conversion. SQLite is unaffected and the
  migration does nothing there.

- **Readarr migration: books could be merged, deleted or mis-identified.**
  Book and author ids share a number space on Goodreads and were looked up in
  one table, so an author mapping could be applied to a book. Two legacy books
  that map to the same OpenLibrary work are now both kept instead of one being
  merged away; one book that cannot be fetched no longer fails the whole
  author; legacy authors with no mapping are skipped with a log line instead
  of being refreshed against the wrong record.

- **Readarr migration: a wrong author match could delete books.** A book
  that came from Readarr is listed by OpenLibrary only if its author and work
  were matched correctly. A confident but wrong match left the author's books
  off the list, and the next refresh removed every one that had no files.
  Books that still carry a Readarr id are now never removed for being missing
  from the source; removing one is up to you.

- **A missing ebook file blocked audiobook grabs, and the other way round.**
  With "unmonitor deleted books" on, RSS sync rejected a release if any file
  of the book was missing from disk. It now looks only at files of the
  release's own format.

- **The API key in `config.xml` could differ from the one the server
  accepted after a first start.** Two parts of the app rewrote the file at
  the same moment on a first-ever start, so the key could be missing from
  the file or be a different one, and anything that read it got 401 until
  Librarr was restarted. Found by the new Windows CI job; not specific to
  Windows.

- **Low-confidence mapping page.** It listed authors as books, saved ids
  without validating them, made one request per row and showed nothing when a
  row failed. It now distinguishes authors from books, validates the id,
  reports errors per row and reloads after a change. Rows already damaged by
  the old page are not repaired automatically.

- **OpenLibrary import lists could not add books.** All three list types
  produced items the importer rejected, and the sync reported items as added
  that were not.

- **A rejected download raised a false "import failed" health error.**

- **A book with both an EPUB and an audiobook imported only one of them.**
  Custom-format scoring, the pending queue and release ranking are now
  format-aware too.

- **Narrator pages returned 404 after an edition was deleted.** Orphaned
  narrator rows are cleaned up by housekeeping.

- **OpenLibrary outages and slow responses.** Timeouts are now retried and
  counted towards backing off, `Retry-After` is capped at 10 seconds, an ISBN
  search that finds nothing returns an empty result instead of an error, and
  redirected or deleted records are followed or skipped.

- **OpenLibrary data quality.** ISBNs listed on the work are used for
  matching, the 1900-01-01 placeholder is no longer treated as a release date
  or as a "new" book, authors with more than 1,000 works are paged, and more
  date formats parse.

- **Library Import** created duplicate authors, re-added existing ones, and
  could leave the spinner running or show stale folders.

- **Calibre:** upgrading a book removed every format instead of the one being
  replaced, and deleting one file removed the whole book.

- **Books with no monitored edition, or more than one, threw** in several
  places that assumed exactly one.

- **API parameter binding.** Controller parameters are no longer filled from
  the service container by inference, the root cause of the empty request
  bodies seen after the move to .NET 10. An author's audiobook quality profile id is
  validated. A missing SSL certificate file is reported by name.

- **Windows:** a service installed with `/i` never listened on its port, and
  a fatal startup error hung for an hour instead of exiting.

## [1.2.3-beta] — 2026-10-11

A bug-fix release. Most of these are regressions from the .NET 10 migration
or gaps in the OpenLibrary and legacy-migration paths, found by an audit of
everything the fork has added since Readarr.

### Fixed

- **Logged-out browsers got a blank 401 instead of the login page.** Reported
  by [@solbu](https://github.com/solbu) in
  [#17](https://github.com/Rorqualx/Librarr/issues/17). ASP.NET Core 10's
  cookie handler no longer redirects on `[ApiController]` endpoints, and the
  controller that serves the UI is one. Forms-auth installs on 1.1.0-beta and
  later were affected. The UI pages redirect to `/login` again; the API still
  answers 401. ([#18](https://github.com/Rorqualx/Librarr/pull/18))

- **Five API endpoints ignored the id in their URL.** The .NET 10 binding
  sweep bound five route parameters from the query string, so each always
  arrived as `0` or empty. Restoring a backup from the list, grabbing a
  pending release from the queue, pinning a book cover, reordering delay
  profiles, and every provider action (loading an indexer's categories, the
  OAuth steps for notifications) all failed. Reordering also read its target
  from `afterId` while the UI has always sent `after`; it reads `after` now.
  ([#19](https://github.com/Rorqualx/Librarr/pull/19))

- **The scheduled author refresh never refreshed anyone.** OpenLibrary has no
  "what changed" feed, and Librarr reported that as "nothing changed" rather
  than "cannot tell", so every scheduled run skipped the whole library. New
  books and metadata changes only arrived on a manual refresh. A run more than
  14 days after the previous one had the same problem, inherited from Readarr.

  The first scheduled refresh after upgrading will catch up on every author
  that is due, which on a long-running install may be most of the library.
  Requests go through the usual rate limit.
  ([#20](https://github.com/Rorqualx/Librarr/pull/20))

- **The Readarr migration could mark itself finished without finishing, and
  books were then deleted.** If OpenLibrary was unreachable during the
  first-boot migration, or the app restarted part-way through, the migration
  was recorded as complete with some or all books unmapped. The next author
  refresh deleted those books if they had no files.

  Reidentify now fails, rather than completing, when any lookup could not
  reach OpenLibrary, and reports how many. An interrupted or failed migration
  resumes on the next startup instead of concluding. And a refresh no longer
  deletes a book that still carries its Readarr id and has no mapping: not
  being able to look a book up is not the same as it having been removed.
  Such a book is kept and logged once per refresh.
  ([#21](https://github.com/Rorqualx/Librarr/pull/21))

- **A refresh could delete the edition a file belongs to.** Librarr reads the
  first 200 editions of a work from OpenLibrary, and popular works have more
  (*To Kill a Mockingbird* has 213). An edition outside that list was treated
  as removed and deleted, leaving its file attached to nothing and the book
  showing no files. Editions that hold files are now kept, and the edition
  with the file is the one monitored, so the book is not reported missing.
  ([#22](https://github.com/Rorqualx/Librarr/pull/22))

- **A refresh could leave a book with no monitored edition.** When the
  monitored edition changed during a refresh, the old one could be saved as
  unmonitored while the new one was not saved at all.
  ([#22](https://github.com/Rorqualx/Librarr/pull/22))

- **Choosing an edition did not last.** An edition picked in Edit Book was
  replaced by OpenLibrary's default pick on the next refresh. The choice now
  stays until you choose another edition, or an import moves monitoring to the
  edition it imported.

  Books added from search also start on a better edition. They used to start
  on whichever edition OpenLibrary listed first, often a translation; they now
  start on the edition Librarr would pick itself (English, with a cover and an
  ISBN where there is one). Books added before this release keep the edition
  they were given. ([#24](https://github.com/Rorqualx/Librarr/pull/24))

### Changed

- **A manual reidentify can now end as failed.** It used to report completed
  whatever happened. It now reports failed when lookups could not reach
  OpenLibrary; the mappings it did write are kept, and running it again
  continues from there.

## [1.2.2-beta] — 2026-08-22

### Added

- **Librarr now stops importing when Open Library stops answering.** Since
  imports stopped aborting on metadata errors, a systematic refusal — a
  rate-limit ban, an outage — no longer announced itself: every lookup failed,
  every book imported unmatched, and the run reported success. Continuing to
  hammer a rate-limiting endpoint also extends the ban.

  After five refusals with no successful contact in between, Librarr stops
  sending for 5 minutes, then 15, then an hour, reset by any successful answer.
  The import stops rather than grinding through the rest of the library, and
  says how far it got. The existing "Open Library is unreachable" health check
  now re-runs the moment this trips — until now it only fired at startup and on
  a config save, so an outage beginning during a long import was invisible on
  the health page for exactly as long as it mattered.

  A 404 deliberately does not count. That is the source answering about a
  record it does not have, which any library of obscure ISBNs produces
  constantly; counting those would trip the breaker on a perfectly healthy
  import. Only the absence of an answer counts — a retry-exhausted 429 or 5xx,
  or a network-level failure such as a DNS error.

### Fixed

- **ISBN imports matched on a hair's margin.** Diagnosed by
  [@KevlarD-67](https://github.com/KevlarD-67) in
  [#4](https://github.com/Rorqualx/Librarr/pull/4); fixed in
  [#7](https://github.com/Rorqualx/Librarr/issues/7). OpenLibrary's `/isbn/`
  endpoint returns author *keys* and never author *names*, and the matcher
  scores an absent name as maximum author-distance rather than "unknown". So an
  otherwise-perfect ISBN candidate — right title, ISBN matching at weight
  `10.0` — arrived at `0.1875` against the `0.20` accept gate. It matched, but
  94% of the error budget was spent before anything else was considered, and a
  single word of title difference was enough to reject it: `0.2277` without a
  name against `0.0402` with one.

  The edition mapper now attaches the author key and the proxy resolves the
  display name separately. Two details carry most of the value. The lookup
  fetches only `/authors/{key}.json` rather than going through `GetAuthorInfo`,
  which would also pull `works.json?limit=1000` and re-map an author's entire
  discography to obtain one string. And it happens *outside* the 30-day
  response cache — inside it, one OpenLibrary hiccup would have persisted a
  nameless book for a month.

  Writing the test for that second point found the same bug a level down:
  LazyCache stores a faulted factory, so caching the name lookup with
  `GetOrAdd` retained the failure for its whole TTL. It now caches successes
  only.

- **One bad book could kill an entire library import.** Reported and fixed by
  [@KevlarD-67](https://github.com/KevlarD-67) in
  [#5](https://github.com/Rorqualx/Librarr/pull/5) and
  [#6](https://github.com/Rorqualx/Librarr/pull/6), against the same live
  ~33k-book library as [#3](https://github.com/Rorqualx/Librarr/pull/3). Two
  independent causes, both of which aborted the run rather than skipping the
  one book they could not handle.

  The first is a leftover from the Goodreads-to-OpenLibrary migration. All six
  metadata lookups in `CandidateService` sit in a `try`/`catch` that logs
  "skipping … search" and carries on, so the code reads as though it tolerates
  a dead edition. Every one of those handlers caught `GoodreadsException`,
  which `OpenLibraryException` is a *sibling* of rather than a subtype — and a
  404 arrives as `HttpException`, which is not in that hierarchy at all. The
  handlers could never fire. They now catch what the OpenLibrary path actually
  throws.

  The second is that import identification assumed every `Book` it was handed
  had been through a database lazy-load. Candidates mapped straight from a
  metadata source have not: the ISBN/ASIN edition lookup builds a slim book
  with no author, and `SeriesLinks` arrives unset. `DistanceCalculator` and
  `LocalEdition.PopulateMatch` dereferenced both unconditionally, so the first
  such candidate threw a `NullReferenceException` and took the run with it.

  Both are covered by tests confirmed to fail against the unfixed code, not
  merely to pass with it — five NRE reproductions plus the `OpenLibraryException`
  and `HttpException` cases `CandidateServiceFixture` had been missing.

  Two notes on what changed beyond the reported bugs. `DistanceCalculator` had
  been left half-guarded, still dereferencing `edition.Book.Value.SeriesLinks`
  twenty lines below a guarded read of the same object; a partial null-guard
  reads as handled without being it, so the book is now resolved once. And an
  HTTP failure that survives the retry loop in `OpenLibraryProxy.Send` now logs
  at **Warn**, not Info: a 404 on one edition is routine, but an exhausted 429
  means OpenLibrary is refusing us and the run is about to finish having
  matched nothing. Aborting is no longer how you find that out, so it needs to
  be audible. A counter that gives up after N consecutive refusals is the
  fuller answer and is not done here.

- **Editions carrying only an ISBN-10 now match on ISBN.** Diagnosed and
  fixed by [@KevlarD-67](https://github.com/KevlarD-67) in
  [#13](https://github.com/Rorqualx/Librarr/pull/13), continuing the
  live-library ISBN work above. Open Library records many editions with an
  `isbn_10` and no `isbn_13`. Those reached the import matcher with
  `Edition.Isbn13` null, so identification took the `isbn_missing` branch
  (weight `0.1`) instead of the `isbn` distance bucket (weight `10.0`) an
  ISBN-bearing candidate is supposed to win on. The edition mapper now
  derives the ISBN-13 an ISBN-10-only edition actually carries — the `978`
  prefix, the first nine digits, and a recomputed mod-10 check digit.

  The ISBN-10 is validated first and dropped when it fails, never converted
  mechanically. A *bogus* derived ISBN-13 scores the full `1.0` distance at
  weight `10.0` and is strictly worse than an absent one at `0.1`, so a
  malformed source ISBN must forfeit the bucket rather than poison it.

- **Books whose edition names no author now fall back to the work's author.**
  [#14](https://github.com/Rorqualx/Librarr/pull/14). Some Open Library
  editions carry no `authors` of their own and lean on the parent work for
  them. The mapper read the author only from the edition, so those books
  arrived authorless and scored maximum author-distance at the matcher — the
  same failure mode as the nameless ISBN-candidate bug above, one level up.
  The work's author is now used when the edition has none.

## [1.2.1-beta] — 2026-08-03

Ships what 1.2.0-beta was meant to ship. Everything in the 1.2.0-beta
section below applies to this release too.

### Fixed

- **Downloadable builds needed a .NET install to start.** Up to .NET 6,
  publishing with a RuntimeIdentifier implied a self-contained build, so every
  Librarr archive carried its own runtime. .NET 7 reversed that default and
  nothing in this repository ever set the property explicitly, so the .NET 10
  migration silently made every artifact framework-dependent — the linux-x64
  tarball fell from 97 MB to 25 MB with no `libcoreclr.so` in it. Unpacking one
  on a machine without .NET 10 would have failed at launch.

  Nothing caught it. The solution builds, 3471 tests pass, and the E2E smoke
  boots the artifact happily, because CI runners already have .NET installed.
  The only signal was the file size.

  `SelfContained` is now set explicitly for any publish that names a runtime
  (`src/Directory.Build.props`), and the release pipeline asserts each
  published RID actually contains its host CLR before it will package
  anything — including a floor on how many RIDs it inspected, so a check that
  finds nothing cannot pass by default.

  **1.2.0-beta's downloads were affected and were never published.** The draft
  was deleted unpublished with zero downloads. Its Docker images are fine and
  remain available: the image opts out of self-contained deliberately, because
  its `aspnet:10.0-alpine` base already carries the runtime.

## [1.2.0-beta] — 2026-08-03

Minor rather than patch: this line adds per-format quality profiles, a
root-folder audiobook profile default carrying schema migration 047,
work counts in author search, and the first Windows installers Librarr
has ever shipped. It also turns off crash reporting that had been
going to the retired upstream since the fork began — read that entry
before upgrading if you relied on `AnalyticsEnabled`.

### Added

- **Frontend unit tests, where there were none.** `yarn test:frontend` runs
  vitest against jsdom with @testing-library/react; config lives in
  `frontend/build/`, tests sit next to their component as `*.test.js`, and a
  `Unit tests (frontend)` CI job runs them separately from linting. The first
  24 cover the surfaces with the least prior verification: the 0-is-falsy
  hazard in `QualityProfileSelectInputConnector`, the bulk editor's No Change
  handling, the Add Author search result card and the root folder card. This
  is a foundation, not coverage -- four files out of roughly a thousand.

- **The integration fixtures run again.** Six fixtures carried
  `[Ignore("Waiting for metadata to be back again")]` from the bookinfo.club
  retirement. The marker was never the blocker: `EnsureAuthor` looked authors
  up with `edition:<goodreads id>`, and `/author/lookup` has no prefix
  handling, so the prefix was ignored and a Goodreads number searched as a
  name. Authors are now identified by their OpenLibrary id, collected in
  `OpenLibraryFixtureData`, and 30 previously-skipped tests pass against live
  OpenLibrary. CalendarFixture no longer asserts a hard-coded February 2020
  window -- OpenLibrary dates none of the author's works, so the window is
  derived from the book's own release date, which tests the calendar rather
  than OpenLibrary's agreement with Goodreads.

- **A root-folder default for the audiobook quality profile.** Set one on a
  root folder and authors created there inherit it, so a dual-format library
  no longer needs a per-author or bulk edit for every new arrival. Leaving it
  unset keeps those authors single-format, which is what every existing root
  folder gets. Migration 047; the setting lives beside the existing quality
  profile default in the root folder dialog.

  This matters most for authors nobody added by hand: when files land in a
  folder for an author that is not in the library, `ImportApprovedBooks`
  creates the author from the root folder's defaults, and until now there was
  no way to give that author an audiobook profile before its first file was
  judged.

- **Work counts in the Add Author search results.** Open Library carries
  several author records for most well-known names, identical on every field
  the card showed. Searching "Tolkien" now distinguishes J.R.R. Tolkien, 355
  works, from the "Tolkien" record with 1, and shows what each author is best
  known for. The data was already on the wire; only the wizard was using it.

- **Windows installers, and optional Authenticode signing.** The release
  pipeline now builds `Librarr.<version>.win-x64-installer.exe` and its
  x86 twin with Inno Setup, alongside the portable zips, and attaches
  both to the draft release with checksums. Signing is opt-in: set the
  `WINDOWS_CERT_PFX` and `WINDOWS_CERT_PASSWORD` repository secrets and
  `distribution/windows/sign.ps1` signs the first-party binaries before
  they are packaged and the installer after it is built, RFC3161
  timestamp included. With no certificate configured it says so and
  exits 0, so a fork without one still gets a complete release — see
  `docs/release-checklist.md`.

  The installer now targets `%ProgramData%\Librarr`. Inherited from
  Readarr, it targeted `%ProgramData%\Readarr` — which is neither where
  `AppFolderInfo` puts Librarr's data nor an empty directory on a
  machine with Readarr installed, and `[InstallDelete]` wipes
  `{app}\bin` on every install.

- **Per-format quality profiles.** An author can now carry a second,
  optional quality profile that applies only to audiobooks
  (`Author.AudiobookQualityProfileId`, migration 046, settable on the
  author edit form and in the bulk editor). Leave it unset — 0, the
  default for every existing row, so the migration needs no backfill —
  and nothing changes: audiobooks are ranked by the same profile as
  everything else. Set it and audiobook releases get their own ranking
  and their own cutoff, which is what makes it possible to want both an
  EPUB and an M4B of the same book instead of having them compete for
  one slot. See `docs/ebooks-and-audiobooks.md`.

### Changed

- **There is now a private way to report a vulnerability.** `SECURITY.md` had
  directed reporters to `/security/advisories/new` since 2026-05-19, but
  private vulnerability reporting was never enabled on the repository, so that
  form was unreachable by anyone without push access. For 76 days the project
  documented a private channel it did not have — Issues and Discussions are
  both public and no email address is published, so there was no private route
  at all. The setting is on. `SECURITY.md` also no longer promises an initial
  response within seven days: that was inherited boilerplate one maintainer
  working in multi-week bursts cannot honour, and it is replaced with an
  explicit invitation to disclose after 14 days of silence rather than wait.

- **Crash reporting is off by default, and no longer goes to Readarr.** The
  inherited logger hard-coded upstream's `sentry.servarr.com` DSNs while
  `AnalyticsEnabled` defaulted to true, so every production Librarr install
  has been reporting its exceptions into the infrastructure of a project
  archived in 2025 — to maintainers who never agreed to receive them and
  cannot act on them. Sentry is now registered only when `LIBRARR_SENTRY_DSN`
  names a DSN you control; unset, which is the default, nothing leaves the
  machine. The README's claim that fork telemetry was "none" is finally true.

- **The app's own links point at Librarr.** System → Status → More Info
  offered Home page, Reddit, Discord, Source and Feature Requests, all of
  which led to Readarr — including Source, so the app told users its code
  lived in the archived repository. The major-version update prompt sent
  people to `readarr.com/#downloads` to fetch a build. Both now point at
  this project. Deep links into `wiki.servarr.com` are deliberately kept:
  logging, remote path mappings and connection settings are unchanged in the
  fork, so that guide is still the accurate documentation for them — only the
  metadata-source material is obsolete.

- **The frontend toolchain moved from Node 20 to Node 24 LTS.** Node 20 went
  end-of-life on 2026-04-30, so CI had been building and releasing on an
  unsupported runtime for three months. Node 24 is supported to 2028-04-30,
  which outlives the .NET 10 window the backend is pinned to.

  Contributors building from source now need Node 24: `package.json` declares
  `engines.node >=24.0.0`, and there is a `.nvmrc` for the first time. That
  pin previously existed in six places that had drifted apart — volta said
  20.11.1, CI said 20.x, and volta wasn't installed on the machine doing the
  development, so local work happened on Node 26. The gap was not theoretical:
  `@testing-library/jest-dom` 6.10.0 requires Node >=22, installed cleanly on
  the newer local Node, and then failed `yarn install --frozen-lockfile` on
  CI's Node 20 and took all three frontend jobs down. The Docker frontend
  stage moves to `node:24-alpine` with it.

- **GitHub Actions updated, clearing the Node 20 runtime deprecation
  warnings.** These warnings are about the runtime each action declares in
  its own `action.yml`, not about the Node the build runs on — a separate
  problem from the one above, and GitHub was already forcing the affected
  actions onto Node 24 regardless. The pins were well behind:
  `download-artifact` v4→v8, `checkout` v4→v7, `setup-node` v4→v7,
  `upload-artifact` v4→v7, `setup-dotnet` v4→v6, `labeler` v4→v7,
  `label-actions` v3→v5, and the `docker/*` actions a major or two each.
  `actions/labeler` v5 redesigned its config schema and rejects the old
  format outright, so `.github/labeler.yml` was rewritten accordingly.

- **The gated test suites run in CI instead of nowhere.** Four suites sat
  behind env-var gates or NUnit categories that no CI job ever set, so a green
  badge said nothing about them. They are wired in by network dependence:
  Playwright's ten local-only tests block the build, its six OpenLibrary-seeded
  ones and `HttpClientFixture`'s 54 run non-blocking alongside, and
  `NzbDrone.Integration.Test`'s 94 move to a nightly workflow that runs one
  fixture at a time — running them in one pass gets the source IP refused by
  openlibrary.org, and hammering a free public service on every push is not a
  reasonable thing to do. Anything reaching a third party is non-blocking on
  purpose: red should mean "we broke it", not "their server is down".

### Fixed

- **Re-running the release pipeline orphaned a draft release each time.**
  `gh release create --draft` always creates a new release, and a draft holds
  no git tag, so GitHub had nothing to collide with and never replaced the
  previous one — it did not even object when the tag was already published.
  Debugging the pipeline against `v1.0.0-beta` therefore left three releases
  for one tag: the published one, plus two invisible drafts sitting on a full
  ~880 MB set of artifacts each. The job now clears any draft for the tag
  before creating the next, and fails loudly rather than producing a draft
  nobody can see if the tag has already shipped. The two orphans are deleted;
  neither had ever been downloaded, and the published release's `SHA256SUMS`
  was verified against its own assets first.

- **One account's credentials could be sent on another account's requests.**
  Every request that authenticates with a `NetworkCredential` — Calibre,
  rTorrent, Flood and Mailgun; the `BasicNetworkCredential` users are
  unaffected — shared a single process-wide `System.Net.CredentialCache`.
  That type prefix-matches, and .NET truncates the prefix at its last `/`, so
  an entry registered for `/ajax/books/lib1` also answered a request for
  `/ajax/books/lib2`. Two root folders on one Calibre server under different
  accounts therefore resolved to whichever was registered first, and with
  `PreAuthenticate` enabled that password was sent proactively. Host and port
  do isolate, so the exposure was same-host, same-port, different path.

  Each distinct credential now gets its own `HttpClient` and its own cache, so
  a prefix collision can only ever return the credential that client already
  holds. As a side effect the per-request rewriting of a shared object is gone,
  which also closes a window where a request could go out unauthenticated
  because the entry was momentarily absent — an intermittent 401 that looked
  nothing like the crash it shared a cause with.

- **Calibre libraries could scan as empty.** Reported and fixed by
  [@KevlarD-67](https://github.com/KevlarD-67) in
  [#3](https://github.com/Rorqualx/Librarr/pull/3), against a live ~33k-book
  library. `ManagedHttpDispatcher` keeps one shared `System.Net.CredentialCache`
  and rewrote it per request; that type is not thread-safe, so two concurrent
  requests to the same URL could both pass `Remove()` and then collide on
  `Add()`. The scheduled `CalibreRootFolderCheck` and a `RescanFolders` disk
  scan are exactly that pair — the only two callers of
  `CalibreProxy.GetAllBookFilePaths` — so the health check overlapping a rescan
  threw `An item with the same key has already been added`, aborted the scan,
  and left the log saying only `Scan folder is empty`. The mutation is now
  serialized.

  Worth knowing how loudly this fires: mirroring the old pattern across four
  threads on .NET 10 fails on ~99% of iterations, and through the real
  dispatcher the backing dictionary also reported its own corruption
  (`a concurrent update ... corrupted its state`). Covered now by
  `ManagedHttpDispatcherFixture`, which was checked to fail without the fix
  rather than merely pass with it.

- **`build.yml` installed a .NET 6 SDK on every run that nothing used.** The
  .NET 10 migration updated `release.yml` and `azure-pipelines.yml` but missed
  `build.yml`, which had carried `DOTNET_VERSION: '6.0.x'` since the Phase 0
  skeleton. CI stayed green because `global.json` pins 10.0.302, so the dotnet
  CLI ignored the freshly-installed 6.0.428 and selected a .NET 10 SDK that
  happened to be preinstalled on the runner image — while still paying for a
  188 MB download across four jobs. The build always targeted net10.0; the pin
  was waste that would have surfaced as an unrelated-looking `global.json`
  resolution failure the day the runner image stopped shipping a matching SDK.

- **CI was red on `main`, and had been since the .NET 10 migration.** Three
  separate breakages, all found while running the integration suite. The
  unit-test job passed `TEST_DIR=_tests/net6.0/...`, which the inline comment
  beside it already warned would make vstest report "Failed tests: 1" against
  paths that do not exist. The e2e-smoke job could not `chmod` a binary under
  `_output/net6.0/`. And three CSS property-order violations from the Library
  Import wizard failed stylelint. The stale `net6.0` paths are also gone from
  `docs.sh`, `tests/e2e/smoke.sh` and the Playwright/Docker docs.

- **The integration suite booted a binary from before the .NET 10 migration.**
  `NzbDroneRunner` started `_output/net6.0/Readarr` in Debug, and that tree
  survives on disk from any earlier build, so the tests ran against months-old
  code and said nothing. It now resolves `net10.0`, fails with the path in the
  message if the binary is missing, and prints which file it launched and when
  that file was built.

- **A quality profile used only for audiobooks could be deleted.** The
  in-use check that stops you deleting a profile still assigned to an author,
  an import list or a root folder never learned about the audiobook profile
  the dual-format work added, so a profile referenced only there passed the
  check and left those rows pointing at nothing.

- **"Open in Open Library" was a 404 on every search result.** Both the
  author and book cards in Add Author built a `goodreads.com` URL out of an
  identifier that has been an Open Library key since the metadata cutover.
  The link exists so you can check a match against the source, which made it
  the one control on the card that most needed to work.

- **The bulk editor's audiobook profile never reset after a save.** Every
  other control in the author editor footer returns to "No Change" once the
  save lands; `audiobookQualityProfileId` was added to the footer's initial
  state and its render but not to that reset, so it went on displaying the
  profile it had just applied as though it were still pending. Caught by the
  first component test written against it.

- **`build.sh --installer` could not have worked for anyone.** It fetched
  Inno Setup from `files.jrsoftware.org/is/6/innosetup-6.2.0.exe`;
  jrsoftware has since moved binary distribution to GitHub Releases and
  that path now 404s for every version. Because the download used
  `curl -s` with no `-f`, curl cheerfully saved the 404 page as
  `innosetup.exe` and the next line ran the HTML through the shell,
  reporting `syntax error near unexpected token 'newline'`. It now pins
  a live URL, prefers an Inno Setup already installed on the machine
  (which GitHub's windows runners have), and fails on the download
  rather than several steps later.

- **Importing an audiobook could delete the ebook you already had.**
  `UpgradeMediaFileService.UpgradeBookFile` walked *every* existing file
  for a book, recycle-binned each one and deleted its row — so an
  incoming M4B did not merely outrank an EPUB, it destroyed it. Existing
  files are now filtered to the format of the incoming release before
  anything is replaced, in the upgrade path and in every cutoff, upgrade,
  queue, history and pending decision that walks the same list. This
  applies whether or not an author has a second quality profile set.

- **The Playwright smoke suite had never actually been run, and did not
  pass when it was.** Three separate faults, all only findable by running
  it: `add_author_page` matched two elements on `"Add New"` (the sidebar
  link and the empty-index button) and failed strict mode; the new
  `library_import_page` clicked a sidebar child link without expanding
  its section first, so it waited forever on an element that is in the
  DOM but not visible; and the per-fixture browser lifecycle raced with
  `NzbDroneRunner.KillAll()`, which kills *every* Readarr process by name
  rather than its own — one fixture's teardown shot down another's
  instance, surfacing as an intermittent `TargetClosedException` out of
  `OneTimeSetUp`. The browser and the Librarr instance now live in
  `AssemblyGate`, one per assembly, which removes the race by
  construction and takes the suite from ~35s to ~3s.

## [1.1.0-beta] — 2026-07-30

First release since `1.0.0-beta`, and the first to publish images for
`linux/arm64` and `linux/arm/v7` — ARM users no longer have to build
locally.

### Added

- **Library Import wizard** (`/add/import`, `frontend/src/LibraryImport/`).
  Walks the folders inside a root folder that no author occupies and pairs
  each one with an Open Library author, then adds them in a single
  request. The author is bound to the folder that already exists on disk
  rather than one derived from the naming format, so existing files are
  adopted instead of orphaned. Backed by
  `RootFolderService.GetUnmappedFolders()` (surfaced as
  `RootFolderResource.unmappedFolders`) and `POST /api/v1/author/import`.
  Readarr never shipped this page — it was dropped when Readarr forked
  from Lidarr, leaving `UnmappedFolder.cs` and `ImportArtistDefaults.cs`
  as dead code.
- **Rescan button on each root folder** (Settings → Media Management).
  Triggers `RescanFoldersCommand` for that folder alone, so an existing
  collection can be picked up without waiting for the scheduled task.
- **Multi-arch docker images.** `linux/amd64`, `linux/arm64` and
  `linux/arm/v7`, published as one manifest list to GHCR and Docker Hub.
  Both `Dockerfile` build stages now pin `--platform=$BUILDPLATFORM` and
  resolve the .NET RID from `TARGETARCH`/`TARGETVARIANT`, so the
  cross-compile happens natively instead of under QEMU emulation.

### Fixed

- **Author search returned OpenLibrary's ranking unchanged, which is wrong
  for folder names.** `/author/lookup` passed OL's `/search/authors.json`
  order straight through, and the Library Import wizard auto-selects the
  first result — so whatever OL ranked first is what got imported. Searching
  `Tolkien, J.R.R.` (an ordinary Calibre folder convention) put a 1-work
  archaeology report ahead of the real 355-work J.R.R. Tolkien, purely
  because the report's title contains `(Tolkien, J`. Results are now
  re-ranked on name match, with OL's work count as a bounded tiebreak so a
  prolific unrelated author can never displace an exact match. Nothing is
  filtered out — a stub record is demoted, never hidden, because OL's index
  lags and a genuinely new author can legitimately report zero works.
  *(`OpenLibrarySearchMapper.ReRankAndMapAuthors`.)*
- **Same-named authors were impossible to tell apart.** OL routinely returns
  several records per author that are identical in every field Librarr
  mapped — three "Stephen King" records at 606, 48 and 7 works, the last of
  whom wrote *Principles of Macroeconomics*. The work count and best-known
  title were already in OL's response and already parsed; they were simply
  discarded by the mapper. They now reach the UI, so the wizard's dropdown
  reads `Stephen King — 606 work(s), Carrie` instead of three identical
  rows. Carried as transient fields — never persisted, and excluded from
  entity equality so they can't make an author look permanently dirty to
  `AuthorMetadataRepository.UpsertMany`.
- **`scripts/playwright-install.sh` could never work in this repo.** It
  looked for the Playwright CLI under `src/NzbDrone.Playwright.Test/bin`,
  which nothing writes to — `Directory.Build.props` redirects all test
  output to `_tests/` — and it shelled out to `pwsh`, requiring PowerShell
  on a Linux or macOS dev box. It now locates the CLI under `_tests/` and
  drives Playwright's own bundled Node directly.
- **Interactive search returned HTTP 500 for any book with no monitored
  edition.** `ReleaseSearchService` used `SingleOrDefault` on the
  monitored-edition set, which throws on both zero matches and more than
  one. Measured at 42% of books in a freshly added library. Now prefers a
  monitored edition, falls back to any edition, and finally to the book
  title. *(`src/NzbDrone.Core/IndexerSearch/ReleaseSearchService.cs`.)*
- **Cover fetches could be rejected by Open Library.** The covers API
  meters requests keyed by ISBN but explicitly exempts CoverID and OLID
  lookups, and a library refresh issues one ISBN-keyed request per
  edition lacking a work-level cover. Only that form is now throttled —
  throttling the exempt majority would slow every refresh for nothing.
- **`HttpClient.DownloadFileAsync` accepted a `userAgent` and discarded
  it.** Callers that asked to identify themselves were silently sending
  the default. Now applied, along with the request rate limit.
- **Docker images were never version-stamped**, so `RuntimeInfo`
  classified every containerised install as a non-production build.

### Changed

- **Librarr now identifies itself honestly to Open Library, Wikidata and
  Audnex** — a real User-Agent carrying the app name, version and a
  contact URL, per each service's stated policy
  (`MetadataUserAgent.cs`). The spoofed browser strings in
  `GoodreadsProxy` are deliberately left alone and documented as such;
  that proxy exists only for the legacy rollback path.
- `.gitignore` tightened to cover local docker volumes, smoke-test
  output, coverage results, test logs and `.env` files.

### Documentation

- `docs/ebooks-and-audiobooks.md` — what one instance can and cannot do
  with both formats. The usual one-line summary ("single format per
  instance") is not accurate: the real constraint is per-author, because
  an author carries exactly one quality profile and that profile is a
  single ordered ranking.
- `distribution/docker/README.md` — corrected the claim that images are
  not published anywhere, and documented the versioning and toolchain
  caveats of `Dockerfile.prebuilt`.
- `docs/roadmap.md` — two entries were recording things that aren't true.
  The .NET 8 upgrade was listed as blocked on the Servarr-forked NuGet
  packages having no `net8.0` build; they all ship `netstandard2.0`, which
  `net8.0` consumes unchanged. The real cost is triaging three frameworks'
  worth of analyzer changes under `TreatWarningsAsErrors`. Selenium →
  Playwright was listed as quarantined, but the suite has shipped.
- `src/NzbDrone.Playwright.Test/README.md` — documented why the suite
  cannot currently be executed (the pinned 1.40.0 browser build is no
  longer served) and what a version bump has to contend with.

## [1.0.0-beta] — 2026-05-19

First public release of Librarr — the engineering-gate-cleared
continuation of the archived
[Readarr/Readarr](https://github.com/Readarr/Readarr) project on
OpenLibrary metadata. Forked from upstream `0b79d300` (2025-06-27,
"Retirement announcement"); see
[`MASTER-PLAN.md`](MASTER-PLAN.md) for the strategic blueprint and
[`ARCHITECTURE.md`](ARCHITECTURE.md) § "Librarr fork additions" for the
fork's code-level inventory.

### Added

- **Native OpenLibrary metadata source.** `OpenLibraryProxy` plus
  author / book / edition search services and mappers replace the
  retired BookInfo / GoodReads-derived path entirely
  (`src/NzbDrone.Core/MetadataSource/OpenLibrary/`).
- **`BookIdMapping` bridge table** (migration `041_book_id_mapping.cs`)
  records confidence-scored GoodReads → OpenLibrary ID mappings for
  every legacy book in the library. Backed by
  `BookIdMappingRepository.cs`. *(Cycles 4, 5.)*
- **`ReidentifyLibraryCommand` + `ReidentifyService`** walks the
  library, matches every existing book against OpenLibrary using
  ISBN / ASIN / title-author confidence scoring, and writes mappings
  into the bridge table.
- **First-boot legacy migration.** `LegacyMigrationService` runs on
  `ApplicationStartedEvent`: detects GoodReads-shaped IDs, flips
  `MonitorNewItems` to `None` per-author for the duration, enqueues
  `ReidentifyLibraryCommand` at high priority, and sets a persisted
  marker on completion so it never re-runs. Companion
  `LegacyMigrationCheck` surfaces stuck state via the health system.
  *(Cycle 6.)*
- **Frontend migration banner** (`frontend/src/App/LegacyMigrationBanner.{js,css,Connector.js}`),
  wired into `Page.js`. Polls health + active commands every 15 s
  and reports migration progress; auto-hides when the marker sets.
  *(Cycle 6.)*
- **Pickable cover modal** with canonical OpenLibrary cover as the
  default. Backed by `Book.PreferredCoverUrl` column (migration
  `045_book_preferred_cover_url.cs`). Includes a bench harness in
  `scripts/bench_le_guin.py` for evaluating cover-pick quality.
  *(Cycle 1.)*
- **Edition-language mapping.** OpenLibrary's two-letter and verbose
  language identifiers now hydrate `Edition.Language`. Bench harness
  reports mean coverage of language metadata up from 0 / 240 to
  240 / 240 (+9.6 percentage points overall). *(Cycle 2.)*
- **Edition-richness tiebreaker.** When OpenLibrary returns multiple
  candidate editions for a work, the picker now prefers richness
  (covers, descriptions, ISBN/OCLC presence, language, number of
  pages). +33 books picked up at least one previously-blank field
  across seven categories. *(Cycle 3.)*
- **Narrator surface for audiobooks.** Normalized narrators schema
  (`043_normalized_narrators.cs`), `NarratorService` wired into
  `RefreshEditionService`, narrator-chips frontend, dedicated
  per-narrator detail page, REST surface in `Readarr.Api.V1.Narrators`.
- **`.azw` file recognition** (Kindle KF7 / older Mobipocket).
  Now mapped to `Quality.MOBI` in `MediaFiles/MediaFileExtensions.cs`.
  *(Cycle 7c.)*
- **`CHANGELOG.md`** (this file). Future cycles will append their
  entries to `[Unreleased]` above.
- **`distribution/docker/README.md`** — local docker quickstart.

### Changed

- **Default `MetadataSourceType` is now OpenLibrary** in fresh
  installs. `bookinfo.club` is retired and no longer reachable.
- **Search prefix syntax** routes to OpenLibrary by identifier shape
  (drops the GoodReads-specific prefixes).
- **Refresh path** stops cascade-adding the entire author's
  discography on Add Book, and stops wiping real metadata on retry.
  Add Book now refreshes just the book; Add Author refreshes the
  full discography but defaults to unmonitored.
- **Books library** keeps to explicit adds only; the author page
  exposes the full discography for browse / pick.
- **CI** moved from Azure Pipelines to GitHub Actions.
- **Selenium → Playwright** scaffolding for end-to-end tests.
- **React 17 → 18** (minimal swap; class-component dominant code
  unchanged).
- **Identity rebrand.** User-facing strings, README, and packaging
  artifacts now say "Librarr". Internal `Readarr.*` csproj names and
  `NzbDrone.*` namespaces are deliberately preserved — see
  [`CLAUDE.md`](CLAUDE.md) "Identity quirk".
- **CI version** bumped to `1.0.0-beta` in
  `azure-pipelines.yml:22`.

### Fixed

- **NZB grabs were 100 % failing on NZBgeek / NzbPlanet etc.** Caused
  by the dev-mode redirect-rejection guardrail in `HttpClient.cs:101`
  firing for every CDN redirect because locally-built docker images
  don't run as Azure `officialBuild`. Fix: opt the NZB-grab request
  into `AllowAutoRedirect = true` in
  `Download/UsenetClientBase.cs`, matching the explicit pattern used
  in `OpenLibraryProxy.cs` and elsewhere. *(Cycle 7a.)*
- **Silent import failures.** Rejected `ImportDecision`s were
  swallowed at debug level and never surfaced to the user. Fix:
  `ImportApprovedBooks.cs` now materializes every rejection as a
  visible `ImportResult` entry, logs a Warn line with the rejection
  reasons, and publishes `TrackImportFailedEvent`. Real downloads
  also light up a `BookImportIncomplete` row in Activity history via
  the existing `CompletedDownloadService.Process` chain. *(Cycle 7d.)*
- **`Add Book` NREs** on the search → add flow.
- **Single-character search queries** no longer hammer OpenLibrary
  and produce 422-noise warnings.
- **Search dedupes author tiles** by normalized name and prefers
  book-derived OLIDs over fuzzy guesses.
- **`Add Author with Monitor=None`** actually means None (used to
  silently slip into "All").
- **Refresh path** hardened against transient failures and
  zero-edition aborts.

### Migration notes

- Existing Readarr libraries: mount your old config directory at
  `/config`, start the container, and the first-boot
  `LegacyMigrationService` handles the rest. See
  ["Migrating from Readarr"](README.md#migrating-from-readarr) in the
  README, or
  `src/NzbDrone.Core/Books/Services/LegacyMigrationService.cs` for
  the source of truth.
- `MetadataSourceType` flips to OpenLibrary by default. If you have
  the old `bookinfo.club` value persisted, it is silently ignored —
  the endpoint is gone.
- The `BookIdMapping` table is additive and uses migration `041`.
  Library DB schema migrations also include `042-045` for
  edition narrators, narrator normalization, dropping the legacy
  `Editions.Narrators` column, and `Book.PreferredCoverUrl`.

### Out of scope (deferred)

- Duplicate-book-record dedupe — ~128 author-title clusters
  introduced by the Cycle 5 OL refresh. Needs a normalized-title
  dedupe pass; tracked for a future cycle.
- Public docker registry publish.
- GitHub remote push + GitHub Release artifact.
- Internal `Readarr.*` csproj rename / `NzbDrone.*` namespace
  rebrand (deliberately preserved).

[Unreleased]: https://github.com/Rorqualx/Librarr/compare/v1.2.6-beta...HEAD
[1.2.6-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.5-beta...v1.2.6-beta
[1.2.5-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.4-beta...v1.2.5-beta
[1.2.4-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.3-beta...v1.2.4-beta
[1.2.3-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.2-beta...v1.2.3-beta
[1.2.2-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.1-beta...v1.2.2-beta
[1.2.1-beta]: https://github.com/Rorqualx/Librarr/compare/v1.2.0-beta...v1.2.1-beta
[1.2.0-beta]: https://github.com/Rorqualx/Librarr/compare/v1.1.0-beta...v1.2.0-beta
[1.1.0-beta]: https://github.com/Rorqualx/Librarr/compare/v1.0.0-beta...v1.1.0-beta
[1.0.0-beta]: https://github.com/Rorqualx/Librarr/releases/tag/v1.0.0-beta
