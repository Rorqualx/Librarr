using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books
{
    public interface IRefreshBookService
    {
        bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags);
        bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate);
    }

    public class RefreshBookService : RefreshEntityServiceBase<Book, Edition>,
        IRefreshBookService,
        IExecute<RefreshBookCommand>,
        IExecute<BulkRefreshBookCommand>
    {
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IAddAuthorService _addAuthorService;
        private readonly IEditionService _editionService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IAugmentAudiobookInfo _audiobookAugmenter;
        private readonly IRefreshEditionService _refreshEditionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IHistoryService _historyService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ICheckIfBookShouldBeRefreshed _checkIfBookShouldBeRefreshed;
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IIdentifyUnmappedLegacyBooks _legacyBooks;
        private readonly Logger _logger;

        public RefreshBookService(IBookService bookService,
                                  IAuthorService authorService,
                                  IRootFolderService rootFolderService,
                                  IAddAuthorService addAuthorService,
                                  IEditionService editionService,
                                  IAuthorMetadataService authorMetadataService,
                                  IProvideAuthorInfo authorInfo,
                                  IProvideBookInfo bookInfo,
                                  IAugmentAudiobookInfo audiobookAugmenter,
                                  IRefreshEditionService refreshEditionService,
                                  IMediaFileService mediaFileService,
                                  IHistoryService historyService,
                                  IEventAggregator eventAggregator,
                                  ICheckIfBookShouldBeRefreshed checkIfBookShouldBeRefreshed,
                                  IMapCoversToLocal mediaCoverService,
                                  IIdentifyUnmappedLegacyBooks legacyBooks,
                                  Logger logger)
        : base(logger, authorMetadataService)
        {
            _bookService = bookService;
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _addAuthorService = addAuthorService;
            _editionService = editionService;
            _authorInfo = authorInfo;
            _bookInfo = bookInfo;
            _audiobookAugmenter = audiobookAugmenter;
            _refreshEditionService = refreshEditionService;
            _mediaFileService = mediaFileService;
            _historyService = historyService;
            _eventAggregator = eventAggregator;
            _checkIfBookShouldBeRefreshed = checkIfBookShouldBeRefreshed;
            _mediaCoverService = mediaCoverService;
            _legacyBooks = legacyBooks;
            _logger = logger;
        }

        private Author GetSkyhookData(Book book)
        {
            try
            {
                var tuple = _bookInfo.GetBookInfo(book.ForeignBookId);
                var author = _authorInfo.GetAuthorInfo(tuple.Item1);
                var newbook = tuple.Item2;

                newbook.Author = author;
                newbook.AuthorMetadata = author.Metadata.Value;
                newbook.AuthorMetadataId = book.AuthorMetadataId;
                newbook.AuthorMetadata.Value.Id = book.AuthorMetadataId;

                // Audnex audiobook augmenter (Phase 7b wire-up). CanAugment
                // gates the whole thing on the opt-in config flag + the
                // book having at least one ASIN'd audiobook edition, so a
                // no-op call is cheap. Failures are swallowed inside
                // Augment — the primary refresh path is never blocked.
                newbook = _audiobookAugmenter.Augment(newbook);

                author.Books = new List<Book> { newbook };
                return author;
            }
            catch (BookNotFoundException)
            {
                _logger.Error($"Could not find book with id {book.ForeignBookId}");
            }

            return null;
        }

        protected override RemoteData GetRemoteData(Book local, List<Book> remote, Author data)
        {
            var result = new RemoteData();

            var book = remote.SingleOrDefault(x => x.ForeignBookId == local.ForeignBookId);

            if (book == null && ShouldDelete(local))
            {
                return result;
            }

            // Asking the source about an id it cannot translate fails the
            // lookup and, with it, the rest of this author's refresh.
            if (book == null && _legacyBooks.IsUnmappedLegacyBook(local.ForeignBookId))
            {
                return result;
            }

            // The `remote` list comes from RefreshAuthorService → the per-author
            // works payload. With OpenLibrary as the metadata source that list
            // is hydrated by OpenLibraryAuthorMapper.ToSlimBooks, which gives
            // each book a single placeholder Edition (ForeignEditionId == work
            // key) just to satisfy MetadataProfileService.FilterBooks. Using
            // that slim view as the remote source for the edition diff would
            // treat all real M-keyed editions inserted by Add Book / refresh
            // as "removed" and silently delete them — the user would then see
            // only placeholder data on the book detail page (Dec 31 1899,
            // 0.0 rating, no cover). Re-fetch per-book via GetSkyhookData so
            // the diff sees real editions. OpenLibraryProxy.GetBookInfo
            // memoizes the underlying HTTP resources for 7 days, so the
            // per-book fan-out is cheap on every refresh after the first.
            var hydrated = GetSkyhookData(local);
            if (hydrated != null)
            {
                var fullBook = hydrated.Books.Value.SingleOrDefault(x => x.ForeignBookId == local.ForeignBookId);
                if (fullBook != null)
                {
                    // Preserve the parent author identity from the per-author
                    // refresh context. OL's per-work payload picks a "primary
                    // author" from authors[] which may not match the author
                    // the user actually added (co-authored works, Sparknotes
                    // shared imprints, pen names like Robert Galbraith ⇄
                    // Rowling). Without this guard, EnsureNewParent treats
                    // the per-work primary as a missing parent and calls
                    // _addAuthorService.AddAuthor on it — cascade-adds that
                    // co-author's full works list (often 100s of books),
                    // each of which refreshes, each finds more co-authors,
                    // each cascades — the refresh queue explodes
                    // exponentially. Verified empirically: 4 user-added
                    // authors → 1012 books + 44 queued RefreshAuthors within
                    // a few minutes. Only the editions need GetSkyhookData
                    // hydration; the author stays whoever owns the local row.
                    if (book != null && book.AuthorMetadata != null)
                    {
                        fullBook.AuthorMetadata = book.AuthorMetadata;
                        fullBook.Author = book.Author;
                    }
                    else if (data?.Metadata?.Value != null)
                    {
                        fullBook.AuthorMetadata = data.Metadata.Value;
                        fullBook.Author = data;
                    }

                    book = fullBook;
                }
            }

            result.Entity = book;
            if (result.Entity != null)
            {
                result.Entity.Id = local.Id;
            }

            return result;
        }

        protected override void EnsureNewParent(Book local, Book remote)
        {
            // Make sure the appropriate author exists (it could be that an book changes parent)
            // The authorMetadata entry will be in the db but make sure a corresponding author is too
            // so that the book doesn't just disappear.

            // TODO filter by metadata id before hitting database
            _logger.Trace($"Ensuring parent author exists [{remote.AuthorMetadata.Value.ForeignAuthorId}]");

            var newAuthor = _authorService.FindById(remote.AuthorMetadata.Value.ForeignAuthorId);

            if (newAuthor == null)
            {
                var oldAuthor = local.Author.Value;
                var addAuthor = new Author
                {
                    Metadata = remote.AuthorMetadata.Value,
                    MetadataProfileId = oldAuthor.MetadataProfileId,
                    QualityProfileId = oldAuthor.QualityProfileId,
                    RootFolderPath = _rootFolderService.GetBestRootFolderPath(oldAuthor.Path),
                    Monitored = oldAuthor.Monitored,
                    Tags = oldAuthor.Tags
                };
                _logger.Debug($"Adding missing parent author {addAuthor}");
                _addAuthorService.AddAuthor(addAuthor);
            }
        }

        protected override bool ShouldDelete(Book local)
        {
            // A book imported from Readarr that reidentify has not mapped yet
            // is never in the remote list. That is "we could not look it up",
            // not "the source dropped it", so it must not be deleted for it.
            if (_legacyBooks.IsUnmappedLegacyBook(local.ForeignBookId))
            {
                return false;
            }

            // not manually added and has no files
            return local.AddOptions.AddType != BookAddType.Manual &&
                !_mediaFileService.GetFilesByBook(local.Id).Any();
        }

        protected override void LogProgress(Book local)
        {
            _logger.ProgressInfo("Updating Info for {0}", local.Title);
        }

        protected override bool IsMerge(Book local, Book remote)
        {
            return local.ForeignBookId != remote.ForeignBookId;
        }

        protected override UpdateResult UpdateEntity(Book local, Book remote)
        {
            UpdateResult result;

            remote.UseDbFieldsFrom(local);

            if (local.Title != (remote.Title ?? "Unknown") ||
                local.ForeignBookId != remote.ForeignBookId ||
                local.AuthorMetadata.Value.ForeignAuthorId != remote.AuthorMetadata.Value.ForeignAuthorId)
            {
                result = UpdateResult.UpdateTags;
            }
            else if (!local.Equals(remote))
            {
                result = UpdateResult.Standard;
            }
            else
            {
                result = UpdateResult.None;
            }

            // Force update and fetch covers if images have changed so that we can write them into tags
            // if (remote.Images.Any() && !local.Images.SequenceEqual(remote.Images))
            // {
            //     _mediaCoverService.EnsureBookCovers(remote);
            //     result = UpdateResult.UpdateTags;
            // }
            local.UseMetadataFrom(remote);

            local.AuthorMetadataId = remote.AuthorMetadata.Value.Id;
            local.LastInfoSync = DateTime.UtcNow;

            return result;
        }

        protected override UpdateResult MergeEntity(Book local, Book target, Book remote)
        {
            _logger.Warn($"Book {local} was merged with {remote} because the original was a duplicate.");

            // Update book ids for trackfiles
            var files = _mediaFileService.GetFilesByBook(local.Id);
            files.ForEach(x => x.EditionId = target.Editions.Value.Single(e => e.Monitored).Id);
            _mediaFileService.Update(files);

            // Update book ids for history
            var items = _historyService.GetByBook(local.Id, null);
            items.ForEach(x => x.BookId = target.Id);
            _historyService.UpdateMany(items);

            // Finally delete the old book
            _bookService.DeleteMany(new List<Book> { local });

            return UpdateResult.UpdateTags;
        }

        protected override Book GetEntityByForeignId(Book local)
        {
            return _bookService.FindById(local.ForeignBookId);
        }

        protected override void SaveEntity(Book local)
        {
            // Use UpdateMany to avoid firing the book edited event
            _bookService.UpdateMany(new List<Book> { local });
        }

        protected override void DeleteEntity(Book local, bool deleteFiles)
        {
            _bookService.DeleteBook(local.Id, deleteFiles);
        }

        protected override List<Edition> GetRemoteChildren(Book local, Book remote)
        {
            return remote.Editions.Value.DistinctBy(m => m.ForeignEditionId).ToList();
        }

        protected override List<Edition> GetLocalChildren(Book entity, List<Edition> remoteChildren)
        {
            return _editionService.GetEditionsForRefresh(entity.Id, remoteChildren.Select(x => x.ForeignEditionId).ToList());
        }

        protected override Tuple<Edition, List<Edition>> GetMatchingExistingChildren(List<Edition> existingChildren, Edition remote)
        {
            var existingChild = existingChildren.SingleOrDefault(x => x.ForeignEditionId == remote.ForeignEditionId);
            return Tuple.Create(existingChild, new List<Edition>());
        }

        protected override void PrepareNewChild(Edition child, Book entity)
        {
            child.BookId = entity.Id;
            child.Book = entity;
        }

        protected override void PrepareExistingChild(Edition local, Edition remote, Book entity)
        {
            local.BookId = entity.Id;
            local.Book = entity;

            // Re-apply the mapper's current monitored pick onto the local
            // edition (which is what flows into MonitorSingleEdition and is
            // saved to the DB), unless the user has manually pinned this
            // edition via ManualAdd. Without this, the first auto-pick is
            // permanent because remote.UseDbFieldsFrom(local) below would
            // then copy local.Monitored back onto remote — meaning
            // improvements to SelectPrimaryEdition never reach existing rows.
            if (!local.ManualAdd)
            {
                local.Monitored = remote.Monitored;
            }

            remote.UseDbFieldsFrom(local);
        }

        protected override void AddChildren(List<Edition> children)
        {
            // hack - add the chilren in refresh children so we can control monitored status
        }

        private void MonitorSingleEdition(SortedChildren children)
        {
            children.Old.ForEach(x => x.Monitored = false);
            var monitored = children.Future.Where(x => x.Monitored).ToList();

            if (monitored.Count == 1)
            {
                return;
            }

            if (monitored.Count == 0)
            {
                monitored = children.Future;
            }

            if (monitored.Count == 0)
            {
                // there are no future children so nothing to do
                return;
            }

            // PrepareExistingChild leaves a user-chosen edition monitored and
            // also marks the source's pick, so both arrive here. The user's
            // wins; the ordering below only settles everything else.
            var chosenByUser = monitored.Where(x => x.Monitored && x.ManualAdd).ToList();
            var candidates = chosenByUser.Any() ? chosenByUser : monitored;

            var toMonitor = candidates.OrderByDescending(x => x.Id > 0 ? _mediaFileService.GetFilesByEdition(x.Id).Count : 0)
                .ThenByDescending(x => x.Ratings.Popularity).First();

            monitored.ForEach(x => x.Monitored = false);
            toMonitor.Monitored = true;

            // force update of anything we've messed with
            var extraToUpdate = children.UpToDate.Where(x => monitored.Contains(x));
            children.UpToDate = children.UpToDate.Except(extraToUpdate).ToList();
            children.Updated.AddRange(extraToUpdate);

            Debug.Assert(!children.Future.Any() || children.Future.Count(x => x.Monitored) == 1, "one edition monitored");
        }

        // The remote edition list is capped (200 per work on OpenLibrary), so
        // an edition can be missing from it and still exist. Deleting one
        // that holds a file leaves the file pointing at nothing and the
        // book showing as having none.
        private void KeepEditionsWithFiles(SortedChildren children, HashSet<int> editionsWithFiles)
        {
            var keep = children.Deleted.Where(x => editionsWithFiles.Contains(x.Id)).ToList();

            foreach (var edition in keep)
            {
                _logger.Debug("Keeping edition {0}: not in the metadata source's list but it has files", edition);
                children.Deleted.Remove(edition);
                children.UpToDate.Add(edition);
            }
        }

        // Wanted/missing only looks at the monitored edition, so a book whose
        // file sits on another edition would be reported missing and grabbed
        // again. Leave the pick alone when it already has a file, or when
        // the user chose it: they picked that edition knowing what they have.
        private void MonitorEditionWithFiles(SortedChildren children, HashSet<int> editionsWithFiles)
        {
            var future = children.Future;
            var monitored = future.SingleOrDefault(x => x.Monitored);

            if (monitored == null || monitored.ManualAdd || editionsWithFiles.Contains(monitored.Id))
            {
                return;
            }

            var withFiles = future.FirstOrDefault(x => editionsWithFiles.Contains(x.Id));

            if (withFiles == null)
            {
                return;
            }

            monitored.Monitored = false;
            withFiles.Monitored = true;
        }

        // PrepareExistingChild and the two methods above change Monitored on
        // editions that are otherwise up to date. Up-to-date editions are not
        // written back, so without this the change is lost for some editions
        // and saved for others, which can leave a book with no monitored
        // edition at all.
        private void SaveMonitoredChanges(SortedChildren children, int bookId)
        {
            if (bookId <= 0)
            {
                return;
            }

            var stored = _editionService.GetEditionsByBook(bookId).ToDictionary(x => x.Id, x => x.Monitored);

            var changed = children.UpToDate
                .Where(x => stored.TryGetValue(x.Id, out var wasMonitored) && wasMonitored != x.Monitored)
                .ToList();

            children.UpToDate = children.UpToDate.Except(changed).ToList();
            children.Updated.AddRange(changed);
        }

        protected override bool RefreshChildren(SortedChildren localChildren, List<Edition> remoteChildren, Author remoteData, bool forceChildRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            var bookId = localChildren.All.Select(x => x.BookId).FirstOrDefault(x => x > 0);
            var editionsWithFiles = bookId > 0
                ? _mediaFileService.GetFilesByBook(bookId).Select(x => x.EditionId).ToHashSet()
                : new HashSet<int>();

            KeepEditionsWithFiles(localChildren, editionsWithFiles);

            // make sure only one of the releases ends up monitored
            MonitorSingleEdition(localChildren);
            MonitorEditionWithFiles(localChildren, editionsWithFiles);
            SaveMonitoredChanges(localChildren, bookId);

            localChildren.All.ForEach(x => _logger.Trace($"release: {x} monitored: {x.Monitored}"));

            _editionService.InsertMany(localChildren.Added);

            return _refreshEditionService.RefreshEditionInfo(localChildren.Added, localChildren.Updated, localChildren.Merged, localChildren.Deleted, localChildren.UpToDate, remoteChildren, forceUpdateFileTags);
        }

        protected override void PublishEntityUpdatedEvent(Book entity)
        {
            // Fetch fresh from DB so all lazy loads are available
            _eventAggregator.PublishEvent(new BookUpdatedEvent(_bookService.GetBook(entity.Id)));
        }

        public bool RefreshBookInfo(List<Book> books, List<Book> remoteBooks, Author remoteData, bool forceBookRefresh, bool forceUpdateFileTags, DateTime? lastUpdate)
        {
            var updated = false;

            foreach (var book in books)
            {
                if (forceBookRefresh || _checkIfBookShouldBeRefreshed.ShouldRefresh(book))
                {
                    updated |= RefreshBookInfo(book, remoteBooks, remoteData, forceUpdateFileTags);
                }
                else
                {
                    _logger.Debug("Skipping refresh of book: {0}", book.Title);
                }
            }

            return updated;
        }

        public bool RefreshBookInfo(Book book, List<Book> remoteBooks, Author remoteData, bool forceUpdateFileTags)
        {
            return RefreshEntityInfo(book, remoteBooks, remoteData, true, forceUpdateFileTags, null);
        }

        public bool RefreshBookInfo(Book book)
        {
            var data = GetSkyhookData(book);

            return RefreshBookInfo(book, data.Books, data, false);
        }

        public void Execute(BulkRefreshBookCommand message)
        {
            var books = _bookService.GetBooks(message.BookIds);

            foreach (var book in books)
            {
                RefreshBookInfo(book);
            }
        }

        public void Execute(RefreshBookCommand message)
        {
            if (message.BookId.HasValue)
            {
                var book = _bookService.GetBook(message.BookId.Value);

                RefreshBookInfo(book);
            }
        }
    }
}
