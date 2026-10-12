using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IDeleteMediaFiles
    {
        void DeleteTrackFile(Author author, BookFile bookFile);
        void DeleteTrackFile(BookFile bookFile, string subfolder = "");
    }

    public class MediaFileDeletionService : IDeleteMediaFiles,
                                            IHandle<AuthorDeletedEvent>,
                                            IHandleAsync<AuthorDeletedEvent>,
                                            IHandleAsync<BookDeletedEvent>,
                                            IHandle<BookFileDeletedEvent>
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAuthorService _authorService;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;

        public MediaFileDeletionService(IDiskProvider diskProvider,
                                        IRecycleBinProvider recycleBinProvider,
                                        IMediaFileService mediaFileService,
                                        IAuthorService authorService,
                                        IConfigService configService,
                                        IEventAggregator eventAggregator,
                                        IRootFolderService rootFolderService,
                                        ICalibreProxy calibre,
                                        Logger logger)
        {
            _diskProvider = diskProvider;
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _authorService = authorService;
            _configService = configService;
            _eventAggregator = eventAggregator;
            _rootFolderService = rootFolderService;
            _calibre = calibre;
            _logger = logger;
        }

        public void DeleteTrackFile(Author author, BookFile bookFile)
        {
            var fullPath = bookFile.Path;
            var rootFolder = _diskProvider.GetParentFolder(author.Path);

            if (!_diskProvider.FolderExists(rootFolder))
            {
                _logger.Warn("Author's root folder ({0}) doesn't exist.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Author's root folder ({0}) doesn't exist.", rootFolder);
            }

            if (_diskProvider.GetDirectories(rootFolder).Empty())
            {
                _logger.Warn("Author's root folder ({0}) is empty.", rootFolder);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Author's root folder ({0}) is empty.", rootFolder);
            }

            if (_diskProvider.FolderExists(author.Path))
            {
                var subfolder = _diskProvider.GetParentFolder(author.Path).GetRelativePath(_diskProvider.GetParentFolder(fullPath));
                DeleteTrackFile(bookFile, subfolder);
            }
            else
            {
                // delete from db even if the author folder is missing
                _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);
            }
        }

        public void DeleteTrackFile(BookFile bookFile, string subfolder = "")
        {
            var fullPath = bookFile.Path;

            if (_diskProvider.FileExists(fullPath))
            {
                _logger.Info("Deleting book file: {0}", fullPath);
                DeleteFile(bookFile, subfolder);
            }

            // Delete the track file from the database to clean it up even if the file was already deleted
            _mediaFileService.Delete(bookFile, DeleteMediaFileReason.Manual);

            _eventAggregator.PublishEvent(new DeleteCompletedEvent());
        }

        private void DeleteFile(BookFile bookFile, string subfolder = "")
        {
            var rootFolder = _rootFolderService.GetBestRootFolder(bookFile.Path);
            var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

            try
            {
                // A file Calibre does not manage is an ordinary file, even
                // inside a Calibre root: audio never goes to Calibre (see
                // CalibreFormats.IsAudio), and without an id there is no
                // record to ask about.
                if (!isCalibre || bookFile.CalibreId == 0 || CalibreFormats.IsAudio(bookFile))
                {
                    _recycleBinProvider.DeleteFile(bookFile.Path, subfolder);
                }
                else
                {
                    DeleteFromCalibre(bookFile, rootFolder.CalibreSettings);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Unable to delete book file");
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, "Unable to delete book file");
            }
        }

        // Removes this file's formats from its Calibre record, and the record
        // itself only once nothing is left on it.
        //
        // This used to delete the record outright. A record holds every format
        // of the book, so deleting one file took the rest with it — including
        // formats the user had added in Calibre and Librarr never tracked.
        private void DeleteFromCalibre(BookFile bookFile, CalibreSettings settings)
        {
            var edition = bookFile.Edition?.Value;
            var related = edition != null
                ? _mediaFileService.GetFilesByBook(edition.BookId)
                : _mediaFileService.GetFilesByEdition(bookFile.EditionId);

            var siblings = (related ?? new List<BookFile>())
                .Where(x => x.Id != bookFile.Id && x.CalibreId == bookFile.CalibreId)
                .ToList();

            var existing = _calibre.GetBook(bookFile.CalibreId, settings);
            var formats = CalibreFormats.OwnedBy(bookFile, existing, settings, siblings);
            var remaining = existing.Formats.Keys.Except(formats).ToList();

            if (remaining.Empty() && siblings.Empty())
            {
                _logger.Debug("No formats left on calibre book {0}, deleting it", bookFile.CalibreId);
                _calibre.DeleteBook(bookFile, settings);
            }
            else if (formats.Any())
            {
                _logger.Debug("Removing formats {0} from calibre book {1}, keeping {2}", formats.ConcatToString(), bookFile.CalibreId, remaining.ConcatToString());
                _calibre.RemoveFormats(bookFile.CalibreId, formats, settings);
            }
        }

        [EventHandleOrder(EventHandleOrder.First)]
        public void Handle(AuthorDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var author = message.Author;

                var rootFolder = _rootFolderService.GetBestRootFolder(message.Author.Path);
                var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

                if (isCalibre)
                {
                    // use metadataId instead of authorId so that query works even after author deleted
                    var books = _mediaFileService.GetFilesByAuthorMetadataId(author.AuthorMetadataId);
                    _calibre.DeleteBooks(books, rootFolder.CalibreSettings);
                }
            }
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var author = message.Author;

                var rootFolder = _rootFolderService.GetBestRootFolder(message.Author.Path);
                var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

                if (!isCalibre)
                {
                    var allAuthors = _authorService.AllAuthorPaths();

                    foreach (var s in allAuthors)
                    {
                        if (s.Key == author.Id)
                        {
                            continue;
                        }

                        if (author.Path.IsParentPath(s.Value))
                        {
                            _logger.Error("Author path: '{0}' is a parent of another author, not deleting files.", author.Path);
                            return;
                        }

                        if (author.Path.PathEquals(s.Value))
                        {
                            _logger.Error("Author path: '{0}' is the same as another author, not deleting files.", author.Path);
                            return;
                        }
                    }

                    if (_diskProvider.FolderExists(message.Author.Path))
                    {
                        _recycleBinProvider.DeleteFolder(message.Author.Path);
                    }

                    _eventAggregator.PublishEvent(new DeleteCompletedEvent());
                }
            }
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            if (message.DeleteFiles)
            {
                var files = _mediaFileService.GetFilesByBook(message.Book.Id);
                foreach (var file in files)
                {
                    DeleteFile(file);
                }
            }
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.Upgrade)
            {
                return;
            }

            if (_configService.DeleteEmptyFolders)
            {
                var author = message.BookFile.Author.Value;
                var bookFolder = message.BookFile.Path.GetParentPath();

                if (_diskProvider.GetFiles(author.Path, true).Empty())
                {
                    _diskProvider.DeleteFolder(author.Path, true);
                }
                else if (_diskProvider.GetFiles(bookFolder, true).Empty())
                {
                    _diskProvider.RemoveEmptySubfolders(bookFolder);
                }
            }
        }
    }
}
