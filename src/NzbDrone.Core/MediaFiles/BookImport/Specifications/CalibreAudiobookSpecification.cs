using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.BookImport.Specifications
{
    // Calibre roots hold ebooks only; see CalibreFormats.IsAudio for why.
    //
    // UpgradeMediaFileService refuses the same thing, but by throwing, which
    // reaches the user as "Failed to import book". Rejecting here says what is
    // actually wrong, and leaves the download where it is.
    public class CalibreAudiobookSpecification : IImportDecisionEngineSpecification<LocalBook>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly Logger _logger;

        public CalibreAudiobookSpecification(IRootFolderService rootFolderService,
                                             Logger logger)
        {
            _rootFolderService = rootFolderService;
            _logger = logger;
        }

        public Decision IsSatisfiedBy(LocalBook item, DownloadClientItem downloadClientItem)
        {
            // A file already in the library is not being sent anywhere.
            if (item.ExistingFile)
            {
                return Decision.Accept();
            }

            var isAudio = item.Quality.Format() == QualityFormat.Audio || CalibreFormats.IsAudio(CalibreFormats.Format(item.Path));

            // A new author has no path yet and so no root folder to check.
            if (!isAudio || item.Author == null || item.Author.Path.IsNullOrWhiteSpace())
            {
                return Decision.Accept();
            }

            var rootFolder = _rootFolderService.GetBestRootFolder(item.Author.Path);

            if (rootFolder != null && rootFolder.IsCalibreLibrary && rootFolder.CalibreSettings != null)
            {
                _logger.Debug("{0} is an audiobook and root folder {1} is a Calibre library. Skipping", item.Path, rootFolder.Path);
                return Decision.Reject($"Audiobooks cannot be imported into Calibre library root folder '{rootFolder.Name}'. Move the author to an ordinary root folder to hold audiobooks");
            }

            return Decision.Accept();
        }
    }
}
