using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        BookFileMoveResult UpgradeBookFile(BookFile bookFile, LocalBook localBook, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly IMoveBookFiles _bookFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;
        private readonly ICalibreProxy _calibre;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IMetadataTagService metadataTagService,
                                       IMoveBookFiles bookFileMover,
                                       IDiskProvider diskProvider,
                                       IRootFolderService rootFolderService,
                                       ICalibreProxy calibre,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _metadataTagService = metadataTagService;
            _bookFileMover = bookFileMover;
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
            _calibre = calibre;
            _logger = logger;
        }

        public BookFileMoveResult UpgradeBookFile(BookFile bookFile, LocalBook localBook, bool copyOnly = false)
        {
            var moveFileResult = new BookFileMoveResult();

            // Only files of the same format are replaced by this import.
            //
            // This loop deletes what it finds — recycle bin on disk and a row
            // out of the database — so before per-format profiles existed,
            // importing an audiobook for a book you already had as an ebook
            // deleted the ebook. Not "ranked it lower": removed the file.
            // That is the concrete thing standing between one instance and
            // holding both formats, and it is why the filter belongs here
            // rather than only in the decision engine.
            var existingFiles = localBook.Book.BookFiles.Value
                                         .MatchingFormat(bookFile.Quality)
                                         .ToList();

            var rootFolderPath = _diskProvider.GetParentFolder(localBook.Author.Path);
            var rootFolder = _rootFolderService.GetBestRootFolder(rootFolderPath);
            var isCalibre = rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null;

            var settings = rootFolder.CalibreSettings;

            // Calibre roots hold ebooks only (see CalibreFormats.IsAudio), and
            // this is checked before the loop below because that loop deletes.
            // The import specification turns the same condition into a
            // readable rejection first; this is the backstop for any caller
            // that gets here without going through it.
            if (isCalibre && CalibreFormats.IsAudio(bookFile))
            {
                throw new CalibreException($"'{bookFile.Path}' is an audiobook and root folder '{rootFolder.Path}' is a Calibre library, which can only hold ebooks.");
            }

            // If there are existing book files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFiles.Any() && !_diskProvider.FolderExists(rootFolderPath))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolderPath}' was not found.");
            }

            foreach (var file in existingFiles)
            {
                var bookFilePath = file.Path;
                var subfolder = rootFolderPath.GetRelativePath(_diskProvider.GetParentFolder(bookFilePath));

                bookFile.CalibreId = file.CalibreId;

                if (_diskProvider.FileExists(bookFilePath))
                {
                    _logger.Debug("Removing existing book file: {0} CalibreId: {1}", file, file.CalibreId);

                    if (!isCalibre)
                    {
                        _recycleBinProvider.DeleteFile(bookFilePath, subfolder);
                    }
                    else if (file.CalibreId > 0)
                    {
                        // Only what this file answers for, not the record's
                        // whole format list: the filter above decides which
                        // BookFile rows are replaced, and says nothing about
                        // what else the Calibre record is holding.
                        var existing = _calibre.GetBook(file.CalibreId, settings);
                        var replacedFormats = CalibreFormats.OwnedBy(file, existing, settings);

                        if (replacedFormats.Any())
                        {
                            _logger.Debug($"Removing existing formats {replacedFormats.ConcatToString()} from calibre");
                            _calibre.RemoveFormats(file.CalibreId, replacedFormats, settings);
                        }
                    }
                }

                moveFileResult.OldFiles.Add(file);
                _mediaFileService.Delete(file, DeleteMediaFileReason.Upgrade);
            }

            if (!isCalibre)
            {
                if (copyOnly)
                {
                    moveFileResult.BookFile = _bookFileMover.CopyBookFile(bookFile, localBook);
                }
                else
                {
                    moveFileResult.BookFile = _bookFileMover.MoveBookFile(bookFile, localBook);
                }

                _metadataTagService.WriteTags(bookFile, true);
            }
            else
            {
                var source = bookFile.Path;

                moveFileResult.BookFile = _calibre.AddAndConvert(bookFile, settings);

                if (!copyOnly)
                {
                    _diskProvider.DeleteFile(source);
                }
            }

            return moveFileResult;
        }
    }
}
