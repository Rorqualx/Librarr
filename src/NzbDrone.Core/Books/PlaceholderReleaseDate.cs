using System;

namespace NzbDrone.Core.Books
{
    // The date a book carries when its release date is not known *yet*.
    //
    // An author's book list comes from OpenLibrary's works payload, which
    // almost never has a publication date (192 of 193 works lacked one for the
    // author it was measured on). The slim books built from it cannot be given
    // a null date: MetadataProfileService.FilterBooks drops undated books when
    // the profile says SkipMissingDate, and the Standard profile does, so every
    // book would be filtered out before the per-book fetch that finds the real
    // date ever ran. So they get this, and the per-book refresh overwrites it.
    //
    // It is a stand-in for "ask again later", and it has to be recognisable as
    // one, because it is also a perfectly ordinary DateTime. Left anonymous it
    // was read as "published in 1900" by MonitorNewBookService, which decides
    // whether a newly found book is new by comparing release dates -- and
    // against any real date at all, 1900 is never new.
    //
    // Why the value itself was not changed to null, which would be the honest
    // representation. A null Book.ReleaseDate is not inert:
    //   - MetadataProfileService: filtered out under SkipMissingDate (above);
    //   - BookRepository.BooksWithoutFiles: `ReleaseDate <= now`, so the book
    //     leaves Wanted > Missing;
    //   - BookMonitoredService: the same test decides "books without files",
    //     so the Missing / Existing / Future monitor options skip it;
    //   - BookAddedService: an undated new book is not searched for.
    // A row keeps the placeholder for as long as its per-book refresh has not
    // succeeded, so switching to null would take such books out of Wanted and
    // out of the monitoring options until it does. That is a change in what
    // users see, to be made on purpose or not at all; this type only stops the
    // placeholder being mistaken for information.
    //
    // A work genuinely first published on 1 January 1900 -- or dated only
    // "1900", which parses to the same instant -- is indistinguishable from
    // the placeholder and is treated as undated by whoever asks IsKnown. The
    // value is kept because rows already stored carry it.
    public static class PlaceholderReleaseDate
    {
        public static readonly DateTime Value = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // True when the date is real information about the book: present, and
        // not the placeholder.
        public static bool IsKnown(DateTime? releaseDate)
        {
            return releaseDate.HasValue && releaseDate.Value != Value;
        }
    }
}
