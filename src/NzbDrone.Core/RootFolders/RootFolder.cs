using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.RootFolders
{
    public class RootFolder : ModelBase
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int DefaultMetadataProfileId { get; set; }
        public int DefaultQualityProfileId { get; set; }

        // 0 means authors added under this root folder stay single-format and
        // audiobooks are ranked by DefaultQualityProfileId, matching
        // Author.AudiobookQualityProfileId. Unlike the field above, 0 is a
        // valid setting rather than a missing one.
        public int DefaultAudiobookQualityProfileId { get; set; }
        public MonitorTypes DefaultMonitorOption { get; set; }
        public NewItemMonitorTypes DefaultNewItemMonitorOption { get; set; }
        public HashSet<int> DefaultTags { get; set; } = new();
        public bool IsCalibreLibrary { get; set; }
        public CalibreSettings CalibreSettings { get; set; }

        public bool Accessible { get; set; }
        public long? FreeSpace { get; set; }
        public long? TotalSpace { get; set; }

        // Not persisted — recomputed from disk on each read. See
        // RootFolderService.GetUnmappedFolders.
        public List<UnmappedFolder> UnmappedFolders { get; set; } = new();
    }
}
