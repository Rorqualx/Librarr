# Migrating from Readarr to Librarr

This guide walks an existing Readarr user through the upgrade to Librarr
1.0.0-beta.1, the fork that rebuilds the metadata layer on Open Library.
If you've never run Readarr, you don't need this guide — install Librarr
fresh per the README.

## TL;DR

1. **Back up** `Readarr/config.xml` and the `Readarr.db` SQLite file.
2. **Stop the Readarr service** (or quit the tray app on Windows /
   macOS). Migrating with the DB held open by Readarr causes silent
   half-writes.
3. **Give Librarr access to your existing data folder.** Pick one of
   the two recipes below (see *Data folder location*). The short
   version: either copy `…/Readarr/` to `…/Librarr/` — **before**
   the Windows installer or Librarr's first start creates that
   folder — or start Librarr with `-data=/path/to/your/Readarr` and
   let it adopt the existing folder in place.
4. **Install Librarr.** It installs to its own folder and Windows
   lists it separately from Readarr, so the two sets of files sit
   side by side. The Windows *service* is the exception: see
   *Windows: one service, not two* below.
5. **Open Settings → Metadata → Switch Metadata Source** and click
   *Start reidentify*. The wizard remaps every Goodreads ID to its OL
   equivalent and stores the mapping in the new `BookIdMapping` table.
6. **Review Settings → Metadata → *Low-confidence metadata mappings*.** Anything
   under the configurable threshold (default `0.70`) shows up there
   with inline OL Work/Edition ID editing and a *Save as Manual*
   button. The legacy `Low-confidence mapping for book` log lines
   still fire in System → Logs as a secondary signal.
7. **Uninstall Readarr** once you're satisfied the migration carried
   over correctly.

## Data folder location

Librarr 1.0.0-beta.1 looks for its data folder under a path named
**`Librarr`**, not `Readarr` (Phase-0 rename, see CLAUDE.md
*Identity quirk*). Both apps default to the same parent directory,
so the difference is exactly one folder name.

| OS | Readarr default | Librarr default |
|---|---|---|
| Linux | `~/.config/Readarr/` (or `$XDG_CONFIG_HOME/Readarr/`) | `~/.config/Librarr/` |
| macOS | `~/.config/Readarr/` | `~/.config/Librarr/` |
| Windows | `C:\ProgramData\Readarr\` | `C:\ProgramData\Librarr\` |

The folder's *contents* are identical between the two: `config.xml`,
the SQLite DB (`readarr.db` is kept as the database filename — it's
tied to the binary names, which Phase 0 chose to leave as `Readarr.*`),
`Backups/`, `MediaCover/`, `UpdateLogs/`, `logs/`. Nothing inside the
folder needs renaming.

### Recipe A — copy the folder (recommended)

Run while both apps are stopped, and **before the `Librarr` folder
exists**: on Windows that means before running the Librarr installer
(it installs into `C:\ProgramData\Librarr`, so the folder is there as
soon as setup finishes); everywhere it means before starting Librarr
for the first time (which creates the folder with an empty database).
Pick the path for your OS from the table above.

```bash
# Linux / macOS
cp -a ~/.config/Readarr ~/.config/Librarr
```

```powershell
# Windows (PowerShell, elevated)
Copy-Item -Recurse 'C:\ProgramData\Readarr' 'C:\ProgramData\Librarr'
```

Or use the helper scripts shipped in this repo — they do the same
copy, but also pre-flight the source/destination paths, refuse to
overwrite an existing `Librarr` folder, and pause if a Readarr or
Librarr process is still running:

```bash
# Linux / macOS — dry run first if you want to see what would happen
./scripts/migrate-readarr-folder.sh --dry-run
./scripts/migrate-readarr-folder.sh
```

```powershell
# Windows — dry run first if you want to see what would happen
.\scripts\migrate-readarr-folder.ps1 -DryRun
.\scripts\migrate-readarr-folder.ps1
```

Both scripts accept `--src=…` / `-Src` and `--dst=…` / `-Dst` if
your folders live somewhere non-default. `--force` / `-Force` skips
the running-process check + interactive prompt for scripted runs.

If `Librarr` already exists, the scripts stop with "destination
already exists" and the plain commands above do something worse: they
nest the copy as `Librarr/Readarr`, which Librarr never looks at. Stop
Librarr and move the folder out of the way first. On Windows that
folder also holds the program (`bin\`), so run the installer again
after the copy.

Then install Librarr (Windows) and start it. It picks up the copied
folder on first launch — same DB, same library, same indexers, same
download clients. On Windows the copy brings Readarr's `bin\` along;
the installer replaces it. Readarr's folder is left untouched so you
can roll back to it if needed.

### Recipe B — point Librarr at the existing folder

Don't copy anything; just tell Librarr where to look:

```bash
# Linux / macOS — service unit, launchd plist, or just the shell
ExecStart=/usr/bin/Librarr -data=/home/you/.config/Readarr
```

```powershell
# Windows service (sc.exe binPath, or via NSSM args)
-data="C:\ProgramData\Readarr"
```

The argument is `-data=` (`/data=` also works). `--appdata=` is not an
argument Librarr knows; it is ignored without complaint and Librarr
opens the default `Librarr` folder instead.

This is the lowest-friction option but means Librarr and Readarr
share a single state directory. **Never run both apps at the same
time** in this configuration — the SQLite WAL can't safely arbitrate
two writers. Use this only if you've already committed to retiring
the Readarr binary.

### If you're running as a system service

Update the `ExecStart=` line in the systemd unit (Linux), the
`Program` / `ProgramArguments` keys in the launchd plist (macOS),
or the binary path in `services.msc` (Windows) so the service runs
the Librarr executable. Whichever recipe you picked above, the
`-data=` argument (or its absence) determines which folder
Librarr opens.

### Windows: one service, not two

Librarr registers its Windows service under the name **`Readarr`**,
the same name stock Readarr uses (kept so an existing Librarr install
upgrades in place). Windows allows one service per name, so the two
apps cannot both be installed as services: the installer's *Windows
Service* option replaces Readarr's service with Librarr's.

Installers up to and including 1.2.3-beta go further and remove an
existing `Readarr` service during setup without asking, whether or not
you pick that option. If your installer instead asks before replacing
a service that is not its own, an unattended install needs
`/REPLACESERVICE=1` to say yes.

Readarr's files and data folder are not touched either way. To go
back, uninstall Librarr's service and reinstall Readarr's from its own
folder (`C:\ProgramData\Readarr\bin\Readarr.Console.exe /i`).

## If you only have the books, not the config folder

Everything above assumes you have a Readarr data folder to carry across.
If you don't — the DB was lost, you were never on Readarr, or you're
coming from Calibre or a hand-managed shelf — the reidentify wizard has
nothing to reidentify. Use **Library Import** instead:

1. Add your books directory as a root folder
   (Settings → Media Management → Root Folders).
2. Go to **Library → Library Import** and pick that root folder.
3. Each subfolder that isn't already an author appears as a row,
   pre-matched against Open Library. Correct any bad guesses in the
   search box, select the rows you want, and import.
4. Librarr attaches each author to the folder already on disk, then
   refreshes and scans it. Nothing is moved or renamed.

The two paths are not alternatives to each other so much as answers to
different starting points:

| You have | Use |
|---|---|
| Readarr `config/` + `readarr.db` | The reidentify wizard above — preserves monitoring, history, profiles, quality decisions |
| Books on disk only | Library Import — creates fresh author records and adopts the existing folders |
| Both | Migrate the config folder first. Library Import afterwards will only list folders no author claims, so it's a safe way to catch anything the DB didn't know about |

Library Import is also the tool for adding new shelves later — it always
shows only what isn't already mapped.

## What changes

| Area | Readarr | Librarr |
|---|---|---|
| Primary metadata source | BookInfo (Goodreads-derived) | Open Library |
| Series metadata | Goodreads listopia | Wikidata SPARQL (P179 / P1545) |
| Audiobook enrichment | None | audnex.us (opt-in) |
| Import lists | Goodreads bookshelves / listopia | OL subject tags + author works |
| Foreign IDs in DB | Goodreads numeric IDs | OL keys (`OL12345W` / `OL12345M`) |
| Files / monitoring / history | Preserved as-is during migration | Preserved as-is during migration |
| Quality profiles | Preserved | Preserved |
| Download client + indexer config | Preserved | Preserved |
| Default data folder name | `Readarr` (under `%ProgramData%` / `~/.config`) | `Librarr` |
| Default service / binary name | `Readarr` / `Readarr.exe` | `Readarr` / `Readarr.exe` (binary names kept; only the data folder + display name changed) |
| iCal feed URL | `/feed/v1/calendar/Readarr.ics` | `/feed/v1/calendar/Librarr.ics` (old URL still served as an alias) |
| User-Agent + Sentry release tag | `Readarr/<version>` | `Librarr/<version>` |

The reidentify wizard rewrites the metadata layer only. Everything else
in your library — paths, downloaded files, monitoring flags, profile
assignments, history rows — stays bit-identical.

## What can go wrong

* **Low-confidence matches.** ~5–15% of typical libraries land at
  `<0.7` confidence and need manual confirmation. Review them at
  **Settings → Metadata → *Low-confidence metadata mappings*** — inline OL
  Work/Edition ID editing, *Save as Manual* button per row.
  Confirmed rows are pinned at confidence `1.0` with source
  `Manual` and are protected from being overwritten by any later
  reidentify or file-tag pass. Legacy `Low-confidence mapping for
  book` log lines in System → Logs also still fire.
* **Books with no OL presence.** Niche self-published or out-of-print
  works may not exist in Open Library. The original Goodreads-ID row in
  `BookIdMapping` stays unmapped (`OpenLibraryWorkId` is NULL). You can
  either add them manually via the low-confidence review screen
  (paste the OL Work/Edition ID, hit *Save as Manual*) or leave them
  as-is — the existing books are not deleted.
* **Audiobook narrators.** Stored as a comma-separated string on
  `Editions.Narrators` (migration 042). Populated automatically when
  the audnex augmenter is enabled and the edition has an ASIN; the
  audiobook detail page renders "Narrated by …" below the page
  count. A normalized `Narrators` table is a planned future
  refactor — see `docs/roadmap.md`.
* **Switching back.** Setting `MetadataSourceType = "BookInfo"` in
  Settings → Metadata reverses the routing (the original Goodreads-ID
  rows are still in `Books.ForeignBookId`). The `BookIdMapping` rows
  stay intact so a second forward-migration is fast.

## Sanity-check checklist

After running the wizard:

- [ ] `select count(*) from BookIdMapping where OpenLibraryWorkId is not null;`
      reports >= 85% of your total `Books` row count.
- [ ] `select Source, count(*) from BookIdMapping group by Source;`
      — the distribution shows the breakdown across `Isbn`, `Asin`,
      `TitleAuthor`, `FileTag`, and `Manual`. ISBN/ASIN matches are
      the most reliable; large `TitleAuthor` counts mean review the
      low-confidence panel.
- [ ] Top 50 most-monitored authors all show a populated overview /
      birth-date / cover in the UI.
- [ ] A random sample of 20 books across the library show the same
      monitoring flags as before the migration.
- [ ] `select count(*) from BookFiles where BookId is null;` is 0
      (no file orphaned by the reidentify pass).
- [ ] Settings → Metadata → *Low-confidence metadata mappings* is empty, or
      every row is reviewed + saved as `Manual`.

If any of the four fails, **don't uninstall Readarr yet.** Open an
issue on the Librarr GitHub with the log slice and the wizard's
exit summary. The `BookIdMapping` table is the audit trail — every
row records the confidence + source method for forensics.

## Rolling back

If the migration goes badly, what you do depends on which folder
recipe you picked.

### From Recipe A (copied folder)

You still have the original Readarr folder untouched at its old
path. The simplest rollback is:

1. Stop Librarr.
2. Start Readarr against `~/.config/Readarr/` (or the Windows /
   macOS equivalent). It uses the pre-migration DB and sees its
   original state. No schema changes were applied to this copy.
3. Once you've confirmed Readarr is healthy, delete the
   `~/.config/Librarr/` copy at your leisure.

### From Recipe B (shared `-data=` folder)

Librarr and Readarr have been writing to the same DB. To revert the
metadata routing without losing post-migration changes:

1. Stop Librarr.
2. In `config.xml`, set `MetadataSourceType` to `BookInfo`.
   (Or do this from Settings → Metadata in the UI before stopping.)
3. Start Readarr against the same folder. Switching the
   metadata-source pointer reverses the routing; the
   `BookIdMapping` table stays on disk but isn't consulted.
4. *Optional* — drop the BookIdMapping schema entirely with the
   migration-041 down step:
   `dotnet ef database update 040 --project src/NzbDrone.Core`
   (or manually `drop table BookIdMapping;` if you don't have the
   EF tooling installed). Edition.Narrators (migration 042) and
   later changes survive — they're additive columns Readarr
   ignores.

### What survives in the binary

Librarr 1.0.0-beta.1 keeps `BookInfoProxy` compiled in alongside
`OpenLibraryProxy`. The rollback path remains supported until
Phase 10 deletes the legacy proxy.

### Migration 044 (narrator-column drop) rollback

Migration 044 deletes the legacy `Editions.Narrators` comma-joined
column — by 044 the data has been normalized into the `Narrators` +
`EditionNarrators` tables (migration 043 + the audnex augmenter sync
in commit `19e5092`), so no data is lost on the upgrade.

**There is intentionally no down-migration.** Re-creating the column
SQL-wise is trivial; re-populating it with a `GROUP_CONCAT` over the
join is also trivial. But a downgraded Readarr/Librarr binary running
against the re-created column would never refill it — the audnex
augmenter stopped writing to `Editions.Narrators` in commit
`19e5092`. The column would go stale on the first refresh.

If you must revert past migration 044, restore a pre-044 SQLite
backup (`readarr.db` snapshot taken before the upgrade) rather than
relying on a down-migration. The migration's own header comment in
`src/NzbDrone.Core/Datastore/Migration/044_drop_editions_narrators.cs`
re-states this policy in case a future contributor is tempted to
write the down-step.
