using System.Text.RegularExpressions;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    // Shape-detectors for Open Library identifiers. Hoisted out of
    // MetadataSourceFactory so LegacyMigrationService (and any future
    // consumer) can ask the same question without taking a dependency on
    // the factory.
    //
    // OL author ids are `OL\d+A` (e.g. OL26320A); work ids are `OL\d+W`
    // (e.g. OL1234W). GoodReads-shaped ids that came in from a pre-cutover
    // BookInfo DB are bare numeric strings ("3345" for Joseph Conrad), so
    // "is it shaped like an OL id?" is a strong signal that a foreign id
    // is already on the new system.
    public static class OpenLibraryIdHelper
    {
        // Anchored on the digits, not just the OL prefix and the type
        // suffix. The old StartsWith("OL") && EndsWith("A") pair called
        // "Olivia" an author id -- harmless while every caller passed a
        // stored foreign id, but SearchForNewAuthor now asks this question
        // about whatever a user typed into a search box, where it is not
        // harmless at all.
        private static readonly Regex AuthorIdRegex =
            new Regex(@"^OL\d+A$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WorkIdRegex =
            new Regex(@"^OL\d+W$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsAuthorId(string id)
            => id != null && AuthorIdRegex.IsMatch(id);

        public static bool IsWorkId(string id)
            => id != null && WorkIdRegex.IsMatch(id);

        // The Try* methods below take whatever a person pasted into a form
        // and reduce it to the canonical id, or say it is not one. Is*Id
        // above answers "is this stored value already canonical?" and is
        // deliberately strict; these are deliberately forgiving about the
        // *wrapping* and just as strict about the id inside it.
        //
        // Accepted: the bare id, the Open Library key ("/works/OL45804W"),
        // and a full openlibrary.org URL with whatever slug or query string
        // came along with it -- in any case, with stray whitespace. The key
        // segment has to agree with the id type, so an author URL is never
        // mistaken for a work.
        private static readonly Regex WorkIdInputRegex = BuildInputRegex("works", "W");
        private static readonly Regex AuthorIdInputRegex = BuildInputRegex("authors", "A");
        private static readonly Regex EditionIdInputRegex = BuildInputRegex("books", "M");

        public static bool TryNormalizeWorkId(string input, out string workId)
            => TryNormalize(WorkIdInputRegex, input, out workId);

        public static bool TryNormalizeAuthorId(string input, out string authorId)
            => TryNormalize(AuthorIdInputRegex, input, out authorId);

        public static bool TryNormalizeEditionId(string input, out string editionId)
            => TryNormalize(EditionIdInputRegex, input, out editionId);

        private static Regex BuildInputRegex(string segment, string suffix)
        {
            var id = @"(?<id>OL\d+" + suffix + ")";

            return new Regex(
                "^(?:" + id + @"|(?:(?:https?://)?(?:www\.)?openlibrary\.org)?/?" + segment + "/" + id + @"(?:\.json)?(?:[/?#].*)?)$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool TryNormalize(Regex regex, string input, out string id)
        {
            id = null;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            var match = regex.Match(input.Trim());
            if (!match.Success)
            {
                return false;
            }

            id = match.Groups["id"].Value.ToUpperInvariant();
            return true;
        }
    }
}
