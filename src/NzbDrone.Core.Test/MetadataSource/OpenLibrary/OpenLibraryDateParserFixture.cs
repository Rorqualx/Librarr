using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // OpenLibrary dates are free text. Every form below marked "captured" is
    // one that occurs in the payloads under Files/OpenLibrary/, where 24 of
    // 362 date strings were not parseable and came back null -- which for a
    // book means no release date at all: filtered out by a metadata profile
    // that skips undated books, and never "new" to MonitorNewBookService.
    [TestFixture]
    public class OpenLibraryDateParserFixture
    {
        private static DateTime Utc(int year, int month = 1, int day = 1)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        // The forms that already parsed. Pinned so the additions cannot
        // quietly change what they mean.
        [TestCase("1965-06-04", 1965, 6, 4)]
        [TestCase("1971-11", 1971, 11, 1)]
        [TestCase("June 4, 1965", 1965, 6, 4)]
        [TestCase("December 31, 1998", 1998, 12, 31)]
        [TestCase("4 June 1965", 1965, 6, 4)]
        [TestCase("11 February 1986", 1986, 2, 11)]
        [TestCase("Jun 4, 1965", 1965, 6, 4)]
        [TestCase("1976", 1976, 1, 1)]
        public void should_keep_parsing_the_forms_it_already_did(string raw, int year, int month, int day)
        {
            OpenLibraryDateParser.Parse(raw).Should().Be(Utc(year, month, day));
        }

        // Month and year, either way round: the first of the month.
        // "June 1987" is the reported form; the rest are captured.
        [TestCase("June 1987", 1987, 6)]
        [TestCase("June 1985", 1985, 6)]
        [TestCase("December 1967", 1967, 12)]
        [TestCase("August 2010", 2010, 8)]
        [TestCase("Jun 1987", 1987, 6)]
        [TestCase("2017 March", 2017, 3)]
        [TestCase("1999 November", 1999, 11)]
        public void month_and_year_should_be_the_first_of_that_month(string raw, int year, int month)
        {
            OpenLibraryDateParser.Parse(raw).Should().Be(Utc(year, month));
        }

        // All captured. In each slash date one of the first two numbers is
        // above twelve, so it can only be the day.
        [TestCase("2001 June 25", 2001, 6, 25)]
        [TestCase("9/27/1995", 1995, 9, 27)]
        [TestCase("19/08/2017", 2017, 8, 19)]
        [TestCase("13/04/2022", 2022, 4, 13)]
        [TestCase("15/12/2025", 2025, 12, 15)]
        public void full_dates_in_other_orders_should_parse(string raw, int year, int month, int day)
        {
            OpenLibraryDateParser.Parse(raw).Should().Be(Utc(year, month, day));
        }

        // "12/01/1999" (the reported form) is the first of December to an
        // American and the twelfth of January to nearly everyone else, and the
        // captured payloads hold both conventions (9/27/1995 next to
        // 19/08/2017), so neither can be assumed. The year is not in doubt.
        // Keep that and do not invent the rest.
        [TestCase("12/01/1999", 1999)]
        [TestCase("03/04/2021", 2021)]
        [TestCase("1/2/1987", 1987)]
        public void slash_date_that_reads_either_way_should_keep_only_the_year(string raw, int year)
        {
            OpenLibraryDateParser.Parse(raw).Should().Be(Utc(year));
        }

        // The year is the part worth having, and it is nearly always there.
        // "1998?" is the reported form; "2001?", "/1993", "Mars 2021" and
        // "Septiembre 1986" are captured -- the last two are French and
        // Spanish, so the month is not understood and only the year survives.
        // "ca. 1900" is listed in the parser's own header and was never
        // handled.
        [TestCase("1998?", 1998)]
        [TestCase("2001?", 2001)]
        [TestCase("/1993", 1993)]
        [TestCase("ca. 1900", 1900)]
        [TestCase("c1987", 1987)]
        [TestCase("[1987]", 1987)]
        [TestCase("1987-1990", 1987)]
        [TestCase(" 1976 ", 1976)]
        [TestCase("Mars 2021", 2021)]
        [TestCase("Septiembre 1986", 1986)]
        public void should_fall_back_to_the_year_when_that_is_all_it_can_read(string raw, int year)
        {
            OpenLibraryDateParser.Parse(raw).Should().Be(Utc(year));
        }

        // "?" and "xxxx" are captured.
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("?")]
        [TestCase("xxxx")]
        [TestCase("19xx")]
        [TestCase("n.d.")]
        public void should_return_null_when_there_is_no_year_to_read(string raw)
        {
            OpenLibraryDateParser.Parse(raw).Should().BeNull();
        }

        // Author birth dates go through the same parser. "1000 BC" contains a
        // four-digit run that is not a year AD.
        [TestCase("384 BC")]
        [TestCase("1000 BC")]
        [TestCase("c. 1200 B.C.")]
        [TestCase("1050 BCE")]
        public void should_not_read_a_bc_date_as_ad(string raw)
        {
            OpenLibraryDateParser.Parse(raw).Should().BeNull();
        }

        [Test]
        public void parsed_dates_should_be_utc()
        {
            OpenLibraryDateParser.Parse("June 1987").Value.Kind.Should().Be(DateTimeKind.Utc);
            OpenLibraryDateParser.Parse("1998?").Value.Kind.Should().Be(DateTimeKind.Utc);
            OpenLibraryDateParser.Parse("9/27/1995").Value.Kind.Should().Be(DateTimeKind.Utc);
        }
    }
}
