using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Model;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.MetadataSource
{
    // Dispatcher between the legacy BookInfo (Goodreads-derived) proxy and
    // the new OpenLibrary proxy, selected by IConfigService.MetadataSourceType.
    //
    // This factory is the ONLY class in the codebase that declares the
    // IProvide* / ISearchForNew* interfaces post-Phase-5 — by design.
    // BookInfoProxy and OpenLibraryProxy expose the same method shapes
    // but do not declare the interfaces themselves; that prevents
    // DryIoc's RegisterMany (Composition/Extensions.cs:31) from binding
    // two impls per interface and producing nondeterministic resolution.
    //
    // Per master plan Phase 5: switching from BookInfo → OpenLibrary on a
    // live install is gated on the reidentify wizard, which writes
    // BookIdMapping rows BEFORE flipping the config value. Consumers
    // never need to handle a "wrong proxy" state at runtime — once the
    // wizard commits, the factory routes consistently.
    public class MetadataSourceFactory
        : IProvideAuthorInfo,
          IProvideBookInfo,
          IProvideSeriesInfo,
          IProvideListInfo,
          ISearchForNewAuthor,
          ISearchForNewBook,
          ISearchForNewEntity,
          IIdentifyUnmappedLegacyBooks
    {
        public const string BookInfoSource = "BookInfo";
        public const string OpenLibrarySource = "OpenLibrary";

        private readonly IConfigService _configService;
        private readonly BookInfoProxy _bookInfo;
        private readonly OpenLibraryProxy _openLibrary;
        private readonly GoodreadsProxy _goodreadsProxy;
        private readonly OpenLibrarySeriesProxy _openLibrarySeries;
        private readonly IBookIdMappingRepository _mappingRepo;
        private readonly IBookRepository _bookRepo;
        private readonly Logger _logger;

        public MetadataSourceFactory(IConfigService configService,
                                     BookInfoProxy bookInfo,
                                     OpenLibraryProxy openLibrary,
                                     GoodreadsProxy goodreadsProxy,
                                     OpenLibrarySeriesProxy openLibrarySeries,
                                     IBookIdMappingRepository mappingRepo,
                                     IBookRepository bookRepo,
                                     Logger logger)
        {
            _configService = configService;
            _bookInfo = bookInfo;
            _openLibrary = openLibrary;
            _goodreadsProxy = goodreadsProxy;
            _openLibrarySeries = openLibrarySeries;
            _mappingRepo = mappingRepo;
            _bookRepo = bookRepo;
            _logger = logger;
        }

        private bool IsOpenLibrary => _configService.MetadataSourceType.EqualsIgnoreCase(OpenLibrarySource);

        // IProvideAuthorInfo
        public Author GetAuthorInfo(string foreignAuthorId, bool useCache = true)
        {
            if (!IsOpenLibrary)
            {
                return _bookInfo.GetAuthorInfo(foreignAuthorId, useCache);
            }

            // Translate GoodReads → OL on the way in via the BookIdMapping
            // table populated by ReidentifyLibraryCommand. Without this
            // step, the OL proxy 404s on every GoodReads-shaped author id
            // ("3345" for Joseph Conrad) imported from a pre-cutover DB.
            var olAuthorId = TranslateAuthorToOpenLibrary(foreignAuthorId);
            var lookupId = olAuthorId ?? foreignAuthorId;

            var author = _openLibrary.GetAuthorInfo(lookupId, useCache);

            // Restore local identity on the way out so RefreshAuthorService
            // still correlates the returned author + books to the existing
            // GoodReads-shaped DB rows. Books whose OL work id has no
            // mapping keep their OL ForeignBookId — those represent new
            // books OL knows about that weren't in the original library.
            return olAuthorId != null
                ? RestoreLocalIdentityOnAuthor(author, foreignAuthorId)
                : author;
        }

        public HashSet<string> GetChangedAuthors(DateTime startTime)
            => IsOpenLibrary
                ? _openLibrary.GetChangedAuthors(startTime)
                : _bookInfo.GetChangedAuthors(startTime);

        // IProvideBookInfo
        public Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string id, bool useCache = true)
        {
            if (!IsOpenLibrary)
            {
                return _bookInfo.GetBookInfo(id, useCache);
            }

            var olWorkId = TranslateBookToOpenLibrary(id);
            var lookupId = olWorkId ?? id;

            var result = _openLibrary.GetBookInfo(lookupId, useCache);

            return olWorkId != null
                ? RestoreLocalIdentityOnBookInfo(result, originalForeignBookId: id)
                : result;
        }

        // BookIdMapping rows for authors carry the GoodReads id as
        // GoodreadsId and the OL author OLID in OpenLibraryEditionId
        // (book mappings reuse that column for the edition id — see
        // ReidentifyService.MapAuthor / .MapBook for the asymmetry).
        // Returns null when the input already looks OL-shaped, no
        // mapping exists, the mapping has an empty translation, or the
        // mapping is a guess the user has not confirmed
        // (BookIdMapping.IsTrusted).
        //
        // The lookup asks for the author row by name. Goodreads numbered
        // authors and works separately, so a work can share this id, and
        // its row must not be read as the author's.
        private string TranslateAuthorToOpenLibrary(string foreignAuthorId)
        {
            if (foreignAuthorId.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (OpenLibraryIdHelper.IsAuthorId(foreignAuthorId))
            {
                return null;
            }

            var mapping = _mappingRepo.FindAuthorByGoodreadsId(foreignAuthorId);
            if (mapping == null || !mapping.IsTrusted())
            {
                return null;
            }

            var olKey = mapping.OpenLibraryEditionId;
            if (olKey.IsNullOrWhiteSpace() || !OpenLibraryIdHelper.IsAuthorId(olKey))
            {
                return null;
            }

            _logger.Debug("Translated GoodReads author {0} → OL {1} via BookIdMapping", foreignAuthorId, olKey);
            return olKey;
        }

        public bool IsUnmappedLegacyBook(string foreignBookId)
        {
            return IsOpenLibrary &&
                   foreignBookId.IsNotNullOrWhiteSpace() &&
                   !OpenLibraryIdHelper.IsWorkId(foreignBookId) &&
                   TranslateBookToOpenLibrary(foreignBookId) == null;
        }

        public bool IsLegacyBook(string foreignBookId)
        {
            return IsOpenLibrary &&
                   foreignBookId.IsNotNullOrWhiteSpace() &&
                   !OpenLibraryIdHelper.IsWorkId(foreignBookId);
        }

        public bool IsUnmappedLegacyAuthor(string foreignAuthorId)
        {
            return IsOpenLibrary &&
                   foreignAuthorId.IsNotNullOrWhiteSpace() &&
                   !OpenLibraryIdHelper.IsAuthorId(foreignAuthorId) &&
                   TranslateAuthorToOpenLibrary(foreignAuthorId) == null;
        }

        public string FindBookRefreshedInstead(string foreignBookId)
        {
            if (!IsOpenLibrary || foreignBookId.IsNullOrWhiteSpace())
            {
                return null;
            }

            // A book added since the switch already carries its work id.
            var olWorkId = OpenLibraryIdHelper.IsWorkId(foreignBookId)
                ? foreignBookId
                : TranslateBookToOpenLibrary(foreignBookId);

            if (olWorkId == null)
            {
                return null;
            }

            // The id an author's works list gives this work on the way out.
            // No legacy book claiming it leaves the work under its own id.
            var refreshed = ReverseBookId(olWorkId) ?? olWorkId;

            return string.Equals(refreshed, foreignBookId, StringComparison.OrdinalIgnoreCase) ? null : refreshed;
        }

        // The OL work id to ask Open Library about for a book as it is stored
        // locally: the id itself when it is already OL-shaped, its BookIdMapping
        // translation for a book migrated from Readarr, or null when it has none.
        // For callers that talk to OpenLibraryProxy directly instead of going
        // through the IProvide* methods above.
        public string GetOpenLibraryWorkId(string foreignBookId)
        {
            if (foreignBookId.IsNullOrWhiteSpace())
            {
                return null;
            }

            return OpenLibraryIdHelper.IsWorkId(foreignBookId)
                ? foreignBookId
                : TranslateBookToOpenLibrary(foreignBookId);
        }

        private string TranslateBookToOpenLibrary(string foreignBookId)
        {
            if (foreignBookId.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (OpenLibraryIdHelper.IsWorkId(foreignBookId))
            {
                return null;
            }

            // The book row, by name: an author can share this id (see
            // TranslateAuthorToOpenLibrary), and a guess is not a mapping.
            var mapping = _mappingRepo.FindBookByGoodreadsId(foreignBookId);
            if (mapping == null || !mapping.IsTrusted())
            {
                return null;
            }

            var olKey = mapping.OpenLibraryWorkId;
            if (olKey.IsNullOrWhiteSpace() || !OpenLibraryIdHelper.IsWorkId(olKey))
            {
                return null;
            }

            _logger.Debug("Translated GoodReads book {0} → OL {1} via BookIdMapping", foreignBookId, olKey);
            return olKey;
        }

        private Author RestoreLocalIdentityOnAuthor(Author author, string originalForeignAuthorId)
        {
            if (author?.Metadata?.Value != null)
            {
                author.Metadata.Value.ForeignAuthorId = originalForeignAuthorId;
            }

            // Books surfaced inside the Author response (LazyLoaded) get
            // their ForeignBookId reverse-translated when a mapping
            // exists, so RefreshAuthorService's id-based correlation
            // matches them to the existing DB rows instead of treating
            // them as a fresh set + marking the originals removed.
            if (author?.Books?.IsLoaded == true && author.Books.Value != null)
            {
                foreach (var book in author.Books.Value)
                {
                    book.ForeignBookId = ReverseBookId(book.ForeignBookId) ?? book.ForeignBookId;
                }
            }

            return author;
        }

        private Tuple<string, Book, List<AuthorMetadata>> RestoreLocalIdentityOnBookInfo(
            Tuple<string, Book, List<AuthorMetadata>> result,
            string originalForeignBookId)
        {
            if (result == null)
            {
                return null;
            }

            var book = result.Item2;
            if (book != null)
            {
                book.ForeignBookId = originalForeignBookId;
            }

            // Item1 is the primary author's foreign id (per OpenLibraryProxy.GetBookInfo
            // comment block) — reverse-translate it if we have an author mapping
            // for the OL author OLID returned in Item3.
            var authors = result.Item3;
            var primaryAuthorMeta = authors != null && authors.Count > 0 ? authors[0] : null;
            var restoredPrimaryAuthorId = primaryAuthorMeta?.ForeignAuthorId;
            if (primaryAuthorMeta != null && OpenLibraryIdHelper.IsAuthorId(primaryAuthorMeta.ForeignAuthorId))
            {
                // Only through a mapping that is in use on the way in. Handing
                // back a legacy id that GetAuthorInfo then refuses to
                // translate would send the caller to the source with it.
                var revAuthor = _mappingRepo.FindAuthorByOpenLibraryAuthorId(primaryAuthorMeta.ForeignAuthorId);
                if (revAuthor != null && revAuthor.IsTrusted() && revAuthor.GoodreadsId.IsNotNullOrWhiteSpace())
                {
                    primaryAuthorMeta.ForeignAuthorId = revAuthor.GoodreadsId;
                    primaryAuthorMeta.TitleSlug = revAuthor.GoodreadsId;
                    restoredPrimaryAuthorId = revAuthor.GoodreadsId;
                }
            }

            return Tuple.Create(restoredPrimaryAuthorId ?? result.Item1, book, authors);
        }

        private string ReverseBookId(string foreignBookId)
        {
            if (foreignBookId.IsNullOrWhiteSpace() || !OpenLibraryIdHelper.IsWorkId(foreignBookId))
            {
                return null;
            }

            // Several legacy books can map to this one work (OpenLibrary has
            // merged works Goodreads kept apart), but the work comes back
            // once and can carry one id. The rows arrive in a fixed order of
            // preference - the user's own choice, then the best match, then
            // the oldest - so the same book answers for the work on every
            // refresh. The others are told so by FindBookRefreshedInstead.
            //
            // Rows outlive the books they were written for. One that points
            // at a book no longer in the library must not claim the work:
            // the refresh would add a new book under the dead legacy id and
            // set aside the one that is really there.
            var mapping = _mappingRepo.FindBooksByOpenLibraryWorkId(foreignBookId)
                .Where(x => x.IsTrusted() && x.GoodreadsId.IsNotNullOrWhiteSpace())
                .FirstOrDefault(x => _bookRepo.FindById(x.GoodreadsId) != null);

            return mapping?.GoodreadsId;
        }

        // IProvideSeriesInfo
        public SeriesInfo GetSeriesInfo(string foreignSeriesId, bool useCache = true)
            => IsOpenLibrary
                ? _openLibrarySeries.GetSeriesInfo(foreignSeriesId, useCache)
                : _goodreadsProxy.GetSeriesInfo(foreignSeriesId, useCache);

        // IProvideListInfo
        public ListInfo GetListInfo(string foreignListId, int page, bool useCache = true)
            => IsOpenLibrary
                ? _openLibrarySeries.GetListInfo(foreignListId, page, useCache)
                : _goodreadsProxy.GetListInfo(foreignListId, page, useCache);

        // ISearchForNewAuthor
        public List<Author> SearchForNewAuthor(string title)
            => IsOpenLibrary
                ? _openLibrary.SearchForNewAuthor(title)
                : _bookInfo.SearchForNewAuthor(title);

        // ISearchForNewBook
        public List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true)
            => IsOpenLibrary
                ? _openLibrary.SearchForNewBook(title, author, getAllEditions)
                : _bookInfo.SearchForNewBook(title, author, getAllEditions);

        public List<Book> SearchByIsbn(string isbn)
            => IsOpenLibrary ? _openLibrary.SearchByIsbn(isbn) : _bookInfo.SearchByIsbn(isbn);

        public List<Book> SearchByAsin(string asin)
            => IsOpenLibrary ? _openLibrary.SearchByAsin(asin) : _bookInfo.SearchByAsin(asin);

        public List<Book> SearchByForeignBookId(string foreignBookId, bool getAllEditions)
            => IsOpenLibrary
                ? _openLibrary.SearchByForeignBookId(foreignBookId, getAllEditions)
                : _bookInfo.SearchByForeignBookId(foreignBookId, getAllEditions);

        // ISearchForNewEntity
        public List<object> SearchForNewEntity(string title)
            => IsOpenLibrary
                ? _openLibrary.SearchForNewEntity(title)
                : _bookInfo.SearchForNewEntity(title);
    }
}
