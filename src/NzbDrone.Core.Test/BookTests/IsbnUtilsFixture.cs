using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class IsbnUtilsFixture : TestBase
    {
        [TestCase("0441172717", "9780441172719")]
        [TestCase("0062316095", "9780062316097")]
        [TestCase("043942089X", "9780439420891")] // X check digit = 10, dropped in conversion
        [TestCase("0-441-17271-7", "9780441172719")] // hyphens tolerated
        public void Isbn10ToIsbn13_converts_valid_isbn10(string isbn10, string expected)
        {
            IsbnUtils.Isbn10ToIsbn13(isbn10).Should().Be(expected);
        }

        [TestCase("0441172661")] // fails its own ISBN-10 checksum
        [TestCase("not-an-isbn")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("9780441172719")] // already 13 digits, not an ISBN-10
        public void Isbn10ToIsbn13_returns_null_for_anything_that_is_not_a_valid_isbn10(string input)
        {
            IsbnUtils.Isbn10ToIsbn13(input).Should().BeNull();
        }

        [TestCase("0441172717", "9780441172719")] // 10 -> 13
        [TestCase("9780441172719", "9780441172719")] // 13 passes through
        [TestCase("978-0-441-17271-9", "9780441172719")] // normalised then passed through
        public void ToIsbn13_puts_both_forms_into_isbn13_space(string input, string expected)
        {
            IsbnUtils.ToIsbn13(input).Should().Be(expected);
        }

        [TestCase("0441172661")] // invalid ISBN-10 checksum -> not convertible
        [TestCase("12345")] // neither length
        [TestCase(null)]
        public void ToIsbn13_returns_null_for_unconvertible_input(string input)
        {
            IsbnUtils.ToIsbn13(input).Should().BeNull();
        }

        [TestCase("0441172717", true)]
        [TestCase("043942089X", true)]
        [TestCase("0441172661", false)]
        [TestCase("044117271", false)] // nine digits
        public void IsValidIsbn10_checks_the_checksum(string isbn, bool expected)
        {
            IsbnUtils.IsValidIsbn10(isbn).Should().Be(expected);
        }
    }
}
