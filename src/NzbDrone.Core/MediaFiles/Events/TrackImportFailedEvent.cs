using System;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.Events
{
    public class TrackImportFailedEvent : IEvent
    {
        public Exception Exception { get; set; }
        public LocalBook BookInfo { get; }
        public bool NewDownload { get; }
        public DownloadClientItemClientInfo DownloadClientInfo { get;  }
        public string DownloadId { get; }

        // True when the file was never attempted: an import specification
        // turned it down ("not an upgrade", no match, ...). Nothing went
        // wrong on disk, so consumers that diagnose I/O problems from this
        // event must not read a rejection as one.
        public bool Rejected { get; }

        public TrackImportFailedEvent(Exception exception, LocalBook bookInfo, bool newDownload, DownloadClientItem downloadClientItem, bool rejected = false)
        {
            Exception = exception;
            BookInfo = bookInfo;
            NewDownload = newDownload;
            Rejected = rejected;

            if (downloadClientItem != null)
            {
                DownloadClientInfo = downloadClientItem.DownloadClientInfo;
                DownloadId = downloadClientItem.DownloadId;
            }
        }
    }
}
