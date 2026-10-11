using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Books
{
    public interface IRefreshEditionService
    {
        bool RefreshEditionInfo(List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remoteEditions, bool forceUpdateFileTags);
    }

    public class RefreshEditionService : IRefreshEditionService
    {
        private readonly IEditionService _editionService;
        private readonly IMetadataTagService _metadataTagService;
        private readonly INarratorService _narratorService;
        private readonly Logger _logger;

        public RefreshEditionService(IEditionService editionService,
            IMetadataTagService metadataTagService,
            INarratorService narratorService,
            Logger logger)
        {
            _editionService = editionService;
            _metadataTagService = metadataTagService;
            _narratorService = narratorService;
            _logger = logger;
        }

        public bool RefreshEditionInfo(List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remoteEditions, bool forceUpdateFileTags)
        {
            var updateList = new List<Edition>();

            // for editions that need updating, just grab the remote edition and set db ids
            foreach (var edition in update)
            {
                // An edition kept because it has files may have no remote
                // counterpart; it is here only to have its own fields saved.
                var remoteEdition = remoteEditions.SingleOrDefault(e => e.ForeignEditionId == edition.ForeignEditionId);

                if (remoteEdition != null)
                {
                    edition.UseMetadataFrom(remoteEdition);
                }

                // make sure title is not null
                edition.Title = edition.Title ?? "Unknown";
                updateList.Add(edition);
            }

            _editionService.DeleteMany(delete.Concat(merge.Select(x => x.Item1)).ToList());
            _editionService.UpdateMany(updateList);

            // Materialize the augmenter's in-memory NarratorList into the
            // normalized Narrators / EditionNarrators schema (migration 043).
            // Only editions whose NarratorList is IsLoaded sync — that's the
            // signal that AudnexProxy.Augment (or another future augmenter)
            // produced fresh data this round. Editions whose NarratorList
            // would lazy-load from DB are left alone to preserve their
            // existing join rows. Both `add` and `updateList` carry valid
            // edition Ids by the time this loop runs.
            foreach (var edition in add.Concat(updateList))
            {
                if (edition.NarratorList?.IsLoaded == true)
                {
                    var names = (edition.NarratorList.Value ?? new List<Narrator>())
                        .Select(n => n.Name);
                    _narratorService.SetNarratorsForEdition(edition.Id, names);
                }
            }

            var tagsToUpdate = updateList;
            if (forceUpdateFileTags)
            {
                _logger.Debug("Forcing tag update due to Author/Book/Edition updates");
                tagsToUpdate = updateList.Concat(upToDate).ToList();
            }

            _metadataTagService.SyncTags(tagsToUpdate);

            return add.Any() || delete.Any() || updateList.Any() || merge.Any();
        }
    }
}
