using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Books
{
    public interface IReidentifyService
    {
    }

    public class ReidentifyService : IReidentifyService, IExecute<ReidentifyLibraryCommand>
    {
        // High-confidence cut. Anything below this surfaces in the Phase 5
        // wizard's manual-review step.
        private const double HighConfidence = 0.85;

        // Below this a mapping is a guess. It is still written, so the
        // review page can show it, but nothing translates through it until
        // the user confirms it there (BookIdMapping.IsTrusted). The author
        // or book it belongs to stays as it was imported in the meantime:
        // kept, and not refreshed.
        private const double MediumConfidence = BookIdMapping.MinimumTrustedConfidence;

        // Confidence assigned when a file-tag-derived ID resolves on OL.
        // ISBN-13 from the file is the strongest signal we get — stronger
        // than any server-side title/author scoring — so it ranks above
        // HighConfidence on purpose.
        private const double FileTagIsbnConfidence = 0.97;
        private const double FileTagAsinConfidence = 0.92;
        private const double FileTagTitleAuthorConfidence = 0.78;

        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IBookIdMappingRepository _mappingRepo;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly OpenLibraryProxy _openLibrary;
        private readonly Logger _logger;

        public ReidentifyService(IAuthorService authorService,
                                 IBookService bookService,
                                 IEditionService editionService,
                                 IBookIdMappingRepository mappingRepo,
                                 IMediaFileService mediaFileService,
                                 IMetadataTagService metadataTagService,
                                 OpenLibraryProxy openLibrary,
                                 Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mappingRepo = mappingRepo;
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _openLibrary = openLibrary;
            _logger = logger;
        }

        public void Execute(ReidentifyLibraryCommand command)
        {
            _logger.ProgressInfo("Starting library reidentification onto Open Library");

            // Lookups that failed because the source could not be reached,
            // as opposed to ones it answered with "no match". Those items
            // were never really tried, so the run is not finished.
            var unreachable = 0;

            var authors = _authorService.GetAllAuthors();
            _logger.Info("Reidentifying {0} authors", authors.Count);
            foreach (var author in authors)
            {
                try
                {
                    MapAuthor(author);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to reidentify author {0}", author);
                    unreachable += IsSourceUnreachable(ex) ? 1 : 0;
                }
            }

            var books = _bookService.GetAllBooks();
            _logger.Info("Reidentifying {0} books", books.Count);
            foreach (var book in books)
            {
                try
                {
                    MapBook(book);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to reidentify book {0}", book);
                    unreachable += IsSourceUnreachable(ex) ? 1 : 0;
                }
            }

            _logger.ProgressInfo("Reidentifying from file tags");
            unreachable += FileTagPass(books);

            if (unreachable > 0)
            {
                // Fail the command so nothing treats this run as complete.
                // Mappings written so far are kept and skipped next time.
                throw new ReidentifyIncompleteException(unreachable);
            }

            _logger.ProgressInfo("Library reidentification finished");
        }

        public static bool IsSourceUnreachable(Exception ex)
        {
            // The breaker's own "not asking" is unreachable too, though it is
            // not a failure the breaker should count. For everything else
            // there is one definition of "no answer", and it is not ours.
            return ex is MetadataSourceUnavailableException ||
                   MetadataSourceStatusService.IndicatesUnavailable(ex);
        }

        // Phase 5b. File tags are the authoritative source: a reader who
        // tagged their MOBI with the right ISBN trusts that ISBN more than
        // any server search. So if a tag-derived OL ID exists for a book,
        // it overwrites any server-side mapping — EXCEPT Manual entries,
        // which are user overrides and must never be touched.
        private int FileTagPass(List<Book> books)
        {
            var unreachable = 0;

            foreach (var book in books)
            {
                if (book.ForeignBookId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                try
                {
                    ReidentifyBookFromTags(book);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "File-tag reidentify failed for book {0}", book);
                    unreachable += IsSourceUnreachable(ex) ? 1 : 0;
                }
            }

            return unreachable;
        }

        private void ReidentifyBookFromTags(Book book)
        {
            var files = _mediaFileService.GetFilesByBook(book.Id);
            if (files == null || files.Count == 0)
            {
                return;
            }

            foreach (var file in files)
            {
                var tags = ReadTagsSafe(file);
                if (tags == null)
                {
                    continue;
                }

                var fileTagMapping = LookupFromTags(book, tags);
                if (fileTagMapping == null)
                {
                    continue;
                }

                ApplyFileTagMapping(book, fileTagMapping);
                return;
            }
        }

        private ParsedTrackInfo ReadTagsSafe(BookFile file)
        {
            if (file?.Path.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            try
            {
                var fileInfo = new FileInfo(file.Path);
                if (!fileInfo.Exists)
                {
                    return null;
                }

                return _metadataTagService.ReadTags((FileInfoBase)fileInfo);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read tags from {0}", file.Path);
                return null;
            }
        }

        private BookIdMapping LookupFromTags(Book book, ParsedTrackInfo tags)
        {
            if (tags.Isbn.IsNotNullOrWhiteSpace())
            {
                var hits = SafeSearch(() => _openLibrary.SearchByIsbn(tags.Isbn));
                var built = BuildMapping(book, hits, FileTagIsbnConfidence);
                if (built != null)
                {
                    return built;
                }
            }

            if (tags.Asin.IsNotNullOrWhiteSpace())
            {
                var hits = SafeSearch(() => _openLibrary.SearchByAsin(tags.Asin));
                var built = BuildMapping(book, hits, FileTagAsinConfidence);
                if (built != null)
                {
                    return built;
                }
            }

            if (tags.BookTitle.IsNotNullOrWhiteSpace() && tags.AuthorTitle.IsNotNullOrWhiteSpace())
            {
                var hits = SafeSearch(() => _openLibrary.SearchForNewBook(tags.BookTitle, tags.AuthorTitle, false));
                return BuildMapping(book, hits, FileTagTitleAuthorConfidence);
            }

            return null;
        }

        private List<Book> SafeSearch(Func<List<Book>> search)
        {
            try
            {
                return search() ?? new List<Book>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "OL lookup failed during file-tag pass");
                return new List<Book>();
            }
        }

        private BookIdMapping BuildMapping(Book book, List<Book> hits, double confidence)
        {
            var hit = hits?.FirstOrDefault();
            if (hit == null || hit.ForeignBookId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var primaryEdition = hit.Editions?.Value?.FirstOrDefault(e => e.Monitored)
                                 ?? hit.Editions?.Value?.FirstOrDefault();

            return new BookIdMapping
            {
                GoodreadsId = book.ForeignBookId,
                OpenLibraryWorkId = hit.ForeignBookId,
                OpenLibraryEditionId = primaryEdition?.ForeignEditionId,
                Confidence = confidence,
                Source = BookIdMappingSource.FileTag,
                CreatedUtc = DateTime.UtcNow
            };
        }

        private void ApplyFileTagMapping(Book book, BookIdMapping fileTagMapping)
        {
            // The book's own row. An author can have the same Goodreads id
            // (they were numbered separately), and taking the author's row
            // here overwrote it with this book's work: the author then
            // stopped translating and every refresh of it failed.
            var existing = _mappingRepo.FindBookByGoodreadsId(book.ForeignBookId);
            var verdict = ResolveOverride(existing, fileTagMapping);

            switch (verdict)
            {
                case OverrideVerdict.Insert:
                    _mappingRepo.Insert(fileTagMapping);
                    _logger.Info("Mapped book '{0}' via file tag to OL {1} (confidence {2:F2})", book.Title, fileTagMapping.OpenLibraryWorkId, fileTagMapping.Confidence);
                    break;
                case OverrideVerdict.Update:
                    fileTagMapping.Id = existing.Id;
                    _mappingRepo.Update(fileTagMapping);
                    _logger.Info("File tag overrode server mapping for book '{0}': now OL {1} (was {2}, confidence {3:F2} → {4:F2})",
                        book.Title,
                        fileTagMapping.OpenLibraryWorkId,
                        existing.OpenLibraryWorkId,
                        existing.Confidence,
                        fileTagMapping.Confidence);
                    break;
                case OverrideVerdict.Skip:
                    _logger.Debug("Keeping manual mapping for book '{0}' — file tags do not override user overrides", book.Title);
                    break;
            }
        }

        // Pure-function decision: how should a freshly-derived file-tag
        // mapping interact with whatever's currently on disk for the same
        // Goodreads ID? Pulled out as a static so the Phase 5b regression
        // fixture can exercise it without the full DI graph.
        internal static OverrideVerdict ResolveOverride(BookIdMapping existing, BookIdMapping incoming)
        {
            if (incoming == null)
            {
                return OverrideVerdict.Skip;
            }

            if (existing == null)
            {
                return OverrideVerdict.Insert;
            }

            if (existing.Source == BookIdMappingSource.Manual)
            {
                // Manual rows are the user saying "no, it's THIS one".
                // Never override.
                return OverrideVerdict.Skip;
            }

            return OverrideVerdict.Update;
        }

        internal enum OverrideVerdict
        {
            Insert,
            Update,
            Skip
        }

        private void MapAuthor(Author author)
        {
            var goodreadsId = author.ForeignAuthorId;
            if (goodreadsId.IsNullOrWhiteSpace())
            {
                return;
            }

            // Already mapped as an author. A book with the same number is
            // somebody else's row and says nothing about this author.
            if (_mappingRepo.FindAuthorByGoodreadsId(goodreadsId) != null)
            {
                return;
            }

            var name = author.Name;
            if (name.IsNullOrWhiteSpace())
            {
                return;
            }

            var candidates = _openLibrary.SearchForNewAuthor(name);
            if (candidates.Count == 0)
            {
                _logger.Debug("No OL author candidates for '{0}'", name);
                return;
            }

            var best = PickBestAuthor(candidates, author);

            // What makes this an author row is the empty work id together
            // with an author OLID in the edition column. Anything else in
            // that column would be a row no lookup recognises.
            if (best == null || !OpenLibraryIdHelper.IsAuthorId(best.ForeignAuthorId))
            {
                return;
            }

            var confidence = ScoreAuthorMatch(best, author);

            _mappingRepo.Insert(new BookIdMapping
            {
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = null,
                OpenLibraryEditionId = best.ForeignAuthorId,
                Confidence = confidence,
                Source = BookIdMappingSource.TitleAuthor,
                CreatedUtc = DateTime.UtcNow
            });

            if (confidence < MediumConfidence)
            {
                _logger.Info("Low-confidence match for author '{0}': '{1}' ({2}, confidence {3:F2}). It will not be used, and the author will not be refreshed, until it is confirmed on the low-confidence mappings page", name, best.Name, best.ForeignAuthorId, confidence);
            }
        }

        private void MapBook(Book book)
        {
            if (book.ForeignBookId.IsNullOrWhiteSpace())
            {
                return;
            }

            // Already mapped as a book. The author with the same number has
            // a row of its own, and finding that one here left the book
            // unmapped for good.
            if (_mappingRepo.FindBookByGoodreadsId(book.ForeignBookId) != null)
            {
                return;
            }

            // ISBN-13 → ASIN → title+author fallback chain. The fallback
            // path matches MASTER-PLAN.md Phase 5.
            var editions = _editionService.GetEditionsByBook(book.Id);
            var isbn13 = editions.Select(e => e.Isbn13).FirstOrDefault(i => i.IsNotNullOrWhiteSpace());
            if (isbn13.IsNotNullOrWhiteSpace())
            {
                var hits = _openLibrary.SearchByIsbn(isbn13);
                if (TryWriteMapping(book, hits, BookIdMappingSource.Isbn, 0.95))
                {
                    return;
                }
            }

            var asin = editions.Select(e => e.Asin).FirstOrDefault(a => a.IsNotNullOrWhiteSpace());
            if (asin.IsNotNullOrWhiteSpace())
            {
                var hits = _openLibrary.SearchByAsin(asin);
                if (TryWriteMapping(book, hits, BookIdMappingSource.Asin, 0.88))
                {
                    return;
                }
            }

            var authorName = book.Author?.Value?.Name;
            var titleHits = _openLibrary.SearchForNewBook(book.Title, authorName, false);
            TryWriteMapping(book, titleHits, BookIdMappingSource.TitleAuthor, ScoreTitleMatch(titleHits, book));
        }

        private bool TryWriteMapping(Book book, List<Book> hits, string source, double confidence)
        {
            var hit = hits?.FirstOrDefault();
            if (hit == null || hit.ForeignBookId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var primaryEdition = hit.Editions?.Value?.FirstOrDefault(e => e.Monitored)
                                 ?? hit.Editions?.Value?.FirstOrDefault();

            _mappingRepo.Insert(new BookIdMapping
            {
                GoodreadsId = book.ForeignBookId,
                OpenLibraryWorkId = hit.ForeignBookId,
                OpenLibraryEditionId = primaryEdition?.ForeignEditionId,
                Confidence = confidence,
                Source = source,
                CreatedUtc = DateTime.UtcNow
            });

            if (confidence < MediumConfidence)
            {
                _logger.Info("Low-confidence mapping for book '{0}' (confidence {1:F2}) — needs manual review. It will not be used, and the book will not be refreshed, until it is confirmed on the low-confidence mappings page", book.Title, confidence);
            }

            return true;
        }

        private static Author PickBestAuthor(List<Author> candidates, Author target)
        {
            // Prefer name-equality match; otherwise first result.
            // TODO Phase 5b: weight by birth/death year overlap when known.
            return candidates.FirstOrDefault(c => string.Equals(c.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                   ?? candidates.FirstOrDefault();
        }

        private static double ScoreAuthorMatch(Author candidate, Author target)
        {
            // Exact-name → 0.9, anything else → 0.5. The second is "the
            // first thing the search returned", which is below
            // MediumConfidence on purpose: it is recorded for review and
            // not used.
            if (string.Equals(candidate.Name, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                return 0.90;
            }

            return 0.50;
        }

        private static double ScoreTitleMatch(List<Book> hits, Book target)
        {
            var first = hits?.FirstOrDefault();
            if (first == null)
            {
                return 0;
            }

            if (string.Equals(first.Title?.Trim(), target.Title?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return HighConfidence;
            }

            return 0.55;
        }
    }
}
