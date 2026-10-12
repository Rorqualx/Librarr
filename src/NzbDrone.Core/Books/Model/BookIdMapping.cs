using System;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.Books
{
    public class BookIdMapping : ModelBase
    {
        // Below this a row is a guess: stored, shown on the review page,
        // and not used (see IsTrusted). Same line as the review page's
        // default threshold, so what the page lists is exactly what is
        // not in use.
        public const double MinimumTrustedConfidence = 0.70;

        public string GoodreadsId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string OpenLibraryEditionId { get; set; }
        public double Confidence { get; set; }
        public string Source { get; set; }
        public DateTime CreatedUtc { get; set; }

        // One table holds two kinds of row, told apart only by which
        // OpenLibrary columns are filled in:
        //
        //   book   - OpenLibraryWorkId holds the work, OpenLibraryEditionId
        //            the edition (may be empty).
        //   author - OpenLibraryWorkId is empty and OpenLibraryEditionId
        //            holds the author's OLID.
        //
        // Goodreads numbered authors and works separately, so author 3389
        // and work 3389 are different things that both land in GoodreadsId.
        // Every lookup therefore has to say which kind it wants; a lookup
        // by GoodreadsId alone answers a question nobody is asking.
        //
        // "Empty" rather than "null" because the review page saves an
        // author row back with the work id as an empty string.
        //
        // These are methods, not properties, so the table mapper does not
        // take them for columns.
        public bool IsBookMapping()
        {
            return OpenLibraryWorkId.IsNotNullOrWhiteSpace();
        }

        public bool IsAuthorMapping()
        {
            return OpenLibraryWorkId.IsNullOrWhiteSpace() && OpenLibraryIdHelper.IsAuthorId(OpenLibraryEditionId);
        }

        // Whether ids may be translated through this row.
        //
        // Reidentify records its guesses as well as its matches: the first
        // search result for an author whose name did not match is written
        // at 0.50, a title search whose title differs at 0.55. Translating
        // through a guess refreshes the author against somebody else's
        // works list, or the book against another work, and the refresh
        // then treats everything the stranger does not have as removed. So
        // a guess is stored for the review page and otherwise ignored until
        // the user confirms it, which marks it Manual.
        public bool IsTrusted()
        {
            return Source == BookIdMappingSource.Manual || Confidence >= MinimumTrustedConfidence;
        }
    }

    // Source values used by ReidentifyService when recording mappings.
    // Stored as plain strings (not an enum) to keep the bridge table
    // schema-stable as new strategies are added without migrations.
    public static class BookIdMappingSource
    {
        public const string Isbn = "ISBN";
        public const string Asin = "ASIN";
        public const string TitleAuthor = "TitleAuthor";
        public const string FileTag = "FileTag";
        public const string Manual = "Manual";
    }
}
