using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Mappers
{
    internal static class OpenLibraryDateParser
    {
        private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;

        // OL dates are free text, and come in many shapes. Counted over the
        // 362 date strings in the captured payloads (Files/OpenLibrary/):
        //   "1976"               year only                           216
        //   "December 31, 1998"  US long, also "Jun 4, 1965"          74
        //   "11 February 1986"   EU long                              29
        //   "1966-01-01"         ISO, also "1971-11"                  19
        //   "June 1985"          month and year                        9
        //   "19/08/2017"         slashed, day first                    4
        //   "9/27/1995"          slashed, month first                  1
        //   "2001?"              uncertain year                        4
        //   "2017 March"         year then month, also "2001 June 25"  3
        //   "/1993"              a slash date with nothing before it   1
        //   "?" / "xxxx"         nothing usable                        2
        // plus "ca. 1900" and the like, which the catalogue is full of.
        //
        // Strategy: the explicit formats; then slash dates, which need a
        // decision the format list cannot make; then whatever year can be
        // found. Only the first four rows used to parse: everything from
        // "June 1985" down came back null, and a book with a null date is
        // dropped by a metadata profile that skips undated books and can never
        // be "new" to MonitorNewBookService. A year is a usable date.
        //
        // What is *not* attempted is guessing. A date this cannot read fully
        // is reduced to the part it can -- the first of the month, or the
        // first of January -- which is the convention the year-only form has
        // always used, rather than resolved to a specific day that may be
        // wrong.
        private static readonly string[] Formats =
        {
            "yyyy-MM-dd",
            "yyyy-MM",
            "MMMM d, yyyy",
            "MMMM dd, yyyy",
            "d MMMM yyyy",
            "dd MMMM yyyy",
            "MMM d, yyyy",
            "MMM dd, yyyy",
            "d MMM yyyy",

            // Month and year in either order, and year-first long dates.
            // English month names only: InvariantCulture knows no others, so
            // "Mars 2021" (captured) falls through to the year.
            "MMMM yyyy",
            "MMM yyyy",
            "yyyy MMMM",
            "yyyy MMM",
            "yyyy MMMM d",
            "yyyy MMM d",
            "MMMM, yyyy",
            "MMM, yyyy"
        };

        private static readonly Regex SlashDate = new Regex(@"^(?<a>\d{1,2})/(?<b>\d{1,2})/(?<year>\d{4})$", RegexOptions.Compiled);

        // Four digits standing alone: "1998?", "ca. 1900", "c1987", "[1987]",
        // "/1993", "1987-1990" (the first). Not part of a longer number, and
        // from 1000 on -- a bare three-digit year still parses when it is the
        // whole string, but is too easily something else to go looking for
        // inside other text.
        private static readonly Regex YearAnywhere = new Regex(@"(?<!\d)(?<year>[1-9]\d{3})(?!\d)", RegexOptions.Compiled);

        // Author birth and death dates use this parser too. "1000 BC" has a
        // four-digit run in it that is not a year DateTime can hold.
        private static readonly Regex BeforeCommonEra = new Regex(@"\bB\.?\s?C\.?(\s?E\.?)?(?![A-Za-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static DateTime? Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            raw = raw.Trim();

            if (DateTime.TryParseExact(raw, Formats, CultureInfo.InvariantCulture, Styles, out var exact))
            {
                return exact;
            }

            if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year >= 1 && year <= 9999)
            {
                return Year(year);
            }

            var slashed = SlashDate.Match(raw);
            if (slashed.Success)
            {
                return ParseSlashDate(slashed);
            }

            if (BeforeCommonEra.IsMatch(raw))
            {
                return null;
            }

            var anywhere = YearAnywhere.Match(raw);
            if (anywhere.Success)
            {
                return Year(int.Parse(anywhere.Groups["year"].Value, CultureInfo.InvariantCulture));
            }

            return null;
        }

        // "12/01/1999" is the first of December in the US and the twelfth of
        // January nearly everywhere else, and OL holds both conventions
        // side by side: the captured editions include 9/27/1995 and
        // 19/08/2017. When one of the two numbers is above twelve it can only
        // be the day and the date is unambiguous. When both could be a month
        // there is nothing to decide by, and picking a convention would be
        // wrong for a good share of records with no way to tell which -- so
        // the year is kept, which is certain, and the rest is not invented.
        private static DateTime? ParseSlashDate(Match match)
        {
            var a = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);

            if (year < 1)
            {
                return null;
            }

            if (a > 12 && b >= 1 && b <= 12)
            {
                return Date(year, b, a);
            }

            if (b > 12 && a >= 1 && a <= 12)
            {
                return Date(year, a, b);
            }

            // Ambiguous, or not a date at all ("13/13/1999", "0/5/1999").
            return Year(year);
        }

        // A day the month does not have ("31/02/1999") falls back to the year,
        // like every other date that cannot be read in full.
        private static DateTime? Date(int year, int month, int day)
        {
            return day <= DateTime.DaysInMonth(year, month)
                ? new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc)
                : Year(year);
        }

        private static DateTime Year(int year)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }
}
