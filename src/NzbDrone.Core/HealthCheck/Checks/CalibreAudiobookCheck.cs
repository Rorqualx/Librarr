using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.Localization;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Calibre roots hold ebooks only (see CalibreFormats.IsAudio), so an
    // audiobook quality profile there asks for something that will be
    // downloaded and then refused at import. The API stops a root folder being
    // saved that way; this covers the ones saved before it did, and authors,
    // whose profile is set somewhere else entirely.
    [CheckOn(typeof(ModelEvent<RootFolder>))]
    [CheckOn(typeof(AuthorAddedEvent))]
    [CheckOn(typeof(AuthorEditedEvent))]
    [CheckOn(typeof(AuthorMovedEvent))]
    [CheckOn(typeof(AuthorDeletedEvent))]
    public class CalibreAudiobookCheck : HealthCheckBase
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IAuthorService _authorService;

        public CalibreAudiobookCheck(IRootFolderService rootFolderService,
                                     IAuthorService authorService,
                                     ILocalizationService localizationService)
            : base(localizationService)
        {
            _rootFolderService = rootFolderService;
            _authorService = authorService;
        }

        public override HealthCheck Check()
        {
            var calibreRoots = _rootFolderService.All().Where(x => x.IsCalibreLibrary).ToList();

            if (calibreRoots.Empty())
            {
                return new HealthCheck(GetType());
            }

            var roots = calibreRoots.Where(x => x.DefaultAudiobookQualityProfileId > 0).Select(x => x.Name).ToList();

            if (roots.Any())
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning, $"Calibre libraries can only hold ebooks, but an audiobook quality profile is set on Calibre root folder(s): {string.Join(", ", roots)}.  Audiobooks will not be imported there; clear the audiobook quality profile.", "#calibre-audiobooks");
            }

            var authors = _authorService.GetAllAuthors()
                .Where(a => a.AudiobookQualityProfileId > 0 &&
                            a.Path.IsNotNullOrWhiteSpace() &&
                            calibreRoots.Any(r => r.Path.IsParentPath(a.Path)))
                .Select(a => a.Name)
                .OrderBy(x => x)
                .ToList();

            if (authors.Any())
            {
                var names = string.Join(", ", authors.Take(5)) + (authors.Count > 5 ? $" and {authors.Count - 5} more" : string.Empty);

                return new HealthCheck(GetType(), HealthCheckResult.Warning, $"Calibre libraries can only hold ebooks, but an audiobook quality profile is set on author(s) in a Calibre root folder: {names}.  Audiobooks will not be imported for them; clear the audiobook quality profile or move the author to an ordinary root folder.", "#calibre-audiobooks");
            }

            return new HealthCheck(GetType());
        }
    }
}
