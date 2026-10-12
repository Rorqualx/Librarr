# LibraryImport/ — Library Import wizard

Adopts an existing on-disk collection. Walks the folders inside a root folder
that no author currently occupies and pairs each one with an Open Library
author, then adds them all in one request.

Librarr fork addition — upstream Readarr dropped this page when it forked from
Lidarr, leaving `UnmappedFolder.cs` and `ImportAuthorDefaults.cs` behind as dead
code. The Servarr siblings all still ship the equivalent (`ImportSeries`,
`ImportMovies`, `ImportArtist`).

## Why it exists alongside the root folder rescan

Rescan (Settings → Media Management → Root Folders) is file-driven: it reads
tags and filenames, searches Open Library per file, and takes the best match by
edit distance. That works for a tidy library and leaves everything else in
Unmapped Files with no way to resolve it in bulk.

This page is folder-driven and asks the user to confirm the author. Because the
author is then known, the subsequent refresh and scan match files against that
author's books rather than against all of Open Library — a much narrower and
more reliable search.

## Files

- `LibraryImportSelectFolder.js` — `/add/import`. Root folder picker showing how
  many unmapped folders each one has.
- `LibraryImport.js` — `/add/import/:rootFolderId`. The table, selection state,
  and the shared-options footer.
- `LibraryImportRow.js` — one folder: editable search term, lookup state, and
  the author dropdown.
- `LibraryImportFooter.js` — monitor / monitor-new-items / quality profile /
  metadata profile / tags applied to every author in the import, plus the
  import button.

## Redux

`../Store/Actions/libraryImportActions.js`. Two things there are load-bearing:

- **Lookups are queued, not parallel.** A root folder with a few hundred author
  folders would otherwise fire that many Open Library searches on mount. Search
  is one of the metered OL endpoints (unlike covers keyed by CoverID or OLID),
  so `CONCURRENT_LOOKUPS` caps how many run at once.
- **Nothing is selected automatically.** Importing creates authors and binds
  them to real directories; the default has to be the reversible one.

## Backend

- `RootFolderService.GetUnmappedFolders` fills `RootFolderResource.unmappedFolders`.
- `POST /api/v1/author/import` (`AuthorImportController`) bulk-adds. It returns
  `{ imported, skipped }`: the authors that were created, and one entry per row
  that was not, keyed by the folder `path` it was sent with and carrying a
  `reason` (`duplicate` — an earlier row picked the same author; `exists` —
  already in the library; `failed` — lookup or validation, see `message`).
  `AddAuthorService.ImportAuthors` never lets one row fail the batch, so the
  page removes the rows that succeeded and labels each one that stayed.
