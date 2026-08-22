using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    // ISBN normalisation and ISBN-10 -> ISBN-13 conversion, shared by the
    // OpenLibrary edition mapper (which derives an edition's ISBN-13 when OL
    // lists only an isbn_10 — issue #10) and by DistanceCalculator (which must
    // compare a file's ISBN against that edition's ISBN in the *same* space —
    // a file tagged with an ISBN-10 would otherwise never match a derived or
    // listed ISBN-13, scoring the full 10.0-weight mismatch instead of the
    // benign 0.1 isbn_missing bucket). The checksum maths also lived, in part,
    // as private helpers in both places and in EbookTagService; this is the
    // single home the PR #13 review asked for.
    public static class IsbnUtils
    {
        // Bare comparison form: digits plus the ISBN-10 'X' check digit,
        // upper-cased, hyphens and spaces removed. Null for anything with no
        // usable characters.
        public static string NormalizeIsbn(string isbn)
        {
            if (isbn.IsNullOrWhiteSpace())
            {
                return null;
            }

            var chars = isbn.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray();
            return chars.Length == 0 ? null : new string(chars).ToUpperInvariant();
        }

        // Everything the matcher compares is ISBN-13: listed isbn_13 entries
        // are 13 digits, derived ones by construction, and a file tag can be
        // either. Convert an ISBN-10 up, pass a normalised ISBN-13 through,
        // and return null for anything that is neither a valid ISBN-10 nor a
        // 13-digit value. Deriving ISBN-13 from ISBN-10 is a checksum, not a
        // round trip, so it is safe to do on both sides of the comparison.
        public static string ToIsbn13(string isbn)
        {
            var normalized = NormalizeIsbn(isbn);
            if (normalized == null)
            {
                return null;
            }

            if (normalized.Length == 13)
            {
                return normalized;
            }

            return Isbn10ToIsbn13(normalized);
        }

        // Prefix 978, drop the ISBN-10 check digit, recompute mod-10. The
        // entry's own checksum is validated first, so a corrupt value is
        // skipped rather than laundered into a plausible-looking ISBN-13.
        public static string Isbn10ToIsbn13(string isbn10)
        {
            var normalized = NormalizeIsbn(isbn10);
            if (normalized == null || normalized.Length != 10 || !IsValidIsbn10(normalized))
            {
                return null;
            }

            var stem = "978" + normalized.Substring(0, 9);
            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                sum += (stem[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }

            return stem + (char)('0' + ((10 - (sum % 10)) % 10));
        }

        // Weights 10..2 over the first nine digits, check digit weight 1, with
        // 'X' standing for 10 in the final position only. Expects a normalised
        // (upper-cased, punctuation-free) string.
        public static bool IsValidIsbn10(string isbn)
        {
            if (isbn == null || isbn.Length != 10)
            {
                return false;
            }

            var sum = 0;
            for (var i = 0; i < 9; i++)
            {
                if (!char.IsDigit(isbn[i]))
                {
                    return false;
                }

                sum += (isbn[i] - '0') * (10 - i);
            }

            if (isbn[9] != 'X' && !char.IsDigit(isbn[9]))
            {
                return false;
            }

            var check = isbn[9] == 'X' ? 10 : isbn[9] - '0';
            return (sum + check) % 11 == 0;
        }
    }
}
