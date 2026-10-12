using System;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.MediaCover
{
    // A cover pinned from the cover-picker modal (Book.PreferredCoverUrl) is
    // fetched by the server and saved under an extension taken from the URL, so
    // it is held to what the picker can actually offer: an https image on the
    // Open Library covers host (see OpenLibraryProxy.GetCoverCandidates).
    //
    // The shape is matched on the raw string rather than on a parsed Uri so
    // there is nothing for two URL parsers to disagree about: no credentials,
    // port, query, fragment, escapes or backslashes can get through.
    public static class PinnedCoverUrl
    {
        public const string AllowedHost = "covers.openlibrary.org";

        // The extensions MediaCoverMapper serves (and resizes) as images.
        private static readonly Regex AllowedUrlRegex = new Regex(@"^https://covers\.openlibrary\.org(?:/[A-Za-z0-9_~.-]+)+\.(?:jpg|png|gif)\z",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool IsAllowed(string url)
        {
            if (url.IsNullOrWhiteSpace())
            {
                return false;
            }

            return AllowedUrlRegex.IsMatch(url) &&
                   !url.Contains("..", StringComparison.Ordinal);
        }

        // No pin at all is always fine: it means "use the default cover".
        public static bool IsAllowedOrEmpty(string url)
        {
            return url.IsNullOrWhiteSpace() || IsAllowed(url);
        }
    }
}
