using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // ISBN selection in OpenLibraryEditionMapper (issue #10). Two of the five
    // captured /isbn/ payloads — Dune and Sapiens — carry no `isbn_13` at all:
    // OL answered with `isbn_10` only, in both cases the checksum-equivalent of
    // the ISBN-13 that was queried. Reading `isbn_13` alone sent those
    // candidates to the matcher with no ISBN, dropping the 10.0-weight isbn
    // bucket from DistanceCalculator's denominator (0.2857 vs the 0.20 gate,
    // measured in PR #4).
    //
    // Deriving ISBN-13 from ISBN-10 is a checksum, not a round trip: prefix
    // 978, drop the ISBN-10 check digit, recompute mod-10. The one subtlety is
    // *which* entry: Dune's record lists two ISBN-10s for the same edition
    // record (different printings), and only the second is the ISBN that was
    // queried. Blindly taking the first would fabricate a *different* ISBN-13
    // than the one the caller looked up — worse than none, because a present
    // and mismatched ISBN scores the full 10.0-weight bucket at distance 1.
    // So the queried ISBN, when the caller knows it, wins over list order.
    [TestFixture]
    public class OpenLibraryEditionMapperFixture
    {
        [Test]
        public void ToEdition_should_use_isbn13_when_present()
        {
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn13: new List<string> { "9780553293357" },
                isbn10: new List<string> { "0553293354" }));

            edition.Isbn13.Should().Be("9780553293357");
        }

        [Test]
        public void ToEdition_should_prefer_the_queried_isbn13_over_list_order()
        {
            var edition = OpenLibraryEditionMapper.ToEdition(
                Resource(isbn13: new List<string> { "9780441172665", "9780441172719" }),
                "9780441172719");

            edition.Isbn13.Should().Be("9780441172719");
        }

        [Test]
        public void ToEdition_should_derive_isbn13_from_a_lone_isbn10()
        {
            // Sapiens, as OL actually served it: isbn_10 only.
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "0062316095" }));

            edition.Isbn13.Should().Be("9780062316097");
        }

        [Test]
        public void ToEdition_should_derive_the_queried_isbn13_when_isbn10_lists_several()
        {
            // Dune, as OL actually served it: two printings on one edition
            // record, and the queried ISBN is the *second*.
            var edition = OpenLibraryEditionMapper.ToEdition(
                Resource(isbn10: new List<string> { "0441172660", "0441172717" }),
                "9780441172719");

            edition.Isbn13.Should().Be("9780441172719");
        }

        [Test]
        public void ToEdition_should_leave_isbn13_null_for_several_isbn10_without_a_queried_isbn()
        {
            // #13 review, Finding 2. On the work-list mapping path there is no
            // queried ISBN to pick a printing by. Committing to the first would
            // fabricate a *different* edition's ISBN-13 (0441172660 derives to
            // 9780441172665, not the 9780441172719 a file might carry), which
            // then scores the full 10.0-weight mismatch — worse than the 0.1
            // isbn_missing that null yields. With nothing to disambiguate,
            // stay null rather than guess.
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "0441172660", "0441172717" }));

            edition.Isbn13.Should().BeNull();
        }

        [Test]
        public void ToEdition_should_still_derive_a_lone_isbn10_without_a_queried_isbn()
        {
            // The single-printing case (Sapiens) is unambiguous, so the
            // work-list path still derives it — only several-with-no-query
            // stays null.
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "0062316095" }));

            edition.Isbn13.Should().Be("9780062316097");
        }

        [Test]
        public void ToEdition_should_accept_the_queried_isbn_in_isbn10_form()
        {
            // ReidentifyService passes whatever the file's tags carry, and
            // older or Calibre-tagged files commonly hold an ISBN-10. The
            // query is compared in ISBN-13 space, so the second printing
            // still wins over list order.
            var edition = OpenLibraryEditionMapper.ToEdition(
                Resource(isbn10: new List<string> { "0441172660", "0441172717" }),
                "0441172717");

            edition.Isbn13.Should().Be("9780441172719");
        }

        [Test]
        public void ToEdition_should_match_an_isbn10_query_against_listed_isbn13_entries()
        {
            var edition = OpenLibraryEditionMapper.ToEdition(
                Resource(isbn13: new List<string> { "9780441172665", "9780441172719" }),
                "0441172717");

            edition.Isbn13.Should().Be("9780441172719");
        }

        [Test]
        public void ToEdition_should_convert_an_x_check_digit_isbn10()
        {
            // X = 10 in the ISBN-10 checksum; the digit is dropped in
            // conversion, so the derived ISBN-13 is all-numeric.
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "043942089X" }));

            edition.Isbn13.Should().Be("9780439420891");
        }

        [Test]
        public void ToEdition_should_skip_isbn10_entries_that_fail_their_own_checksum()
        {
            // A corrupt entry must not be laundered into a plausible-looking
            // ISBN-13 — deriving validates before it converts.
            var edition = OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "0441172661", "0062316095" }));

            edition.Isbn13.Should().Be("9780062316097");
        }

        [Test]
        public void ToEdition_should_leave_isbn13_null_when_no_usable_isbn_exists()
        {
            OpenLibraryEditionMapper.ToEdition(Resource())
                .Isbn13.Should().BeNull();

            OpenLibraryEditionMapper.ToEdition(Resource(
                isbn10: new List<string> { "not-an-isbn", "0441172661" }))
                .Isbn13.Should().BeNull();
        }

        [Test]
        public void ToBook_should_thread_the_queried_isbn_to_the_edition()
        {
            var book = OpenLibraryEditionMapper.ToBook(
                Resource(isbn10: new List<string> { "0441172660", "0441172717" }),
                "9780441172719");

            book.Editions.Value.Should().ContainSingle()
                .Which.Isbn13.Should().Be("9780441172719");
        }

        private static OpenLibraryEditionResource Resource(List<string> isbn13 = null, List<string> isbn10 = null)
        {
            return new OpenLibraryEditionResource
            {
                Key = "/books/OL22597282M",
                Title = "Dune",
                Isbn13 = isbn13,
                Isbn10 = isbn10
            };
        }
    }
}
