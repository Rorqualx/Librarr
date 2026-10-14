using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.BookImport.Identification
{
    [TestFixture]
    public class DistanceCalculatorFixture : TestBase
    {
        [Test]
        public void should_reverse_single_reversed_author()
        {
            var input = new List<string> { "Last, First" };
            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().Contain("First Last");
        }

        [Test]
        public void should_reverse_two_reversed_author()
        {
            var input = new List<string>
            {
                "Last, First",
                "Last2, First2"
            };

            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().HaveCount(4);
            authors.Should().Contain("First Last");
            authors.Should().Contain("First2 Last2");
            authors.Should().Contain("Last, First");
            authors.Should().Contain("Last2, First2");
        }

        [Test]
        public void should_not_reverse_single_author()
        {
            var input = new List<string> { "First Last" };
            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().HaveCount(1);
            authors.Should().Contain("First Last");
        }

        [TestCase("First1 Last1, First2 Last2", "First1 Last1", "First2 Last2")]
        [TestCase("First1 Last1; First2 Last2", "First1 Last1", "First2 Last2")]
        [TestCase("First1 Last1 & First2 Last2", "First1 Last1", "First2 Last2")]
        [TestCase("First1 Last1 / First2 Last2", "First1 Last1", "First2 Last2")]
        [TestCase("First1 Last1 and First2 Last2", "First1 Last1", "First2 Last2")]
        public void should_split_concatenated_author(string inputString, string first, string second)
        {
            var input = new List<string> { inputString };
            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().Contain(inputString);
            authors.Should().Contain(first);
            authors.Should().Contain(second);
            authors.Should().HaveCount(3);
        }

        [Test]
        public void should_split_concatenated_with_trailing_and()
        {
            var inputString = "First Last, First2 Last2 & First3 Last3";
            var input = new List<string> { inputString };
            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().Contain(inputString);
            authors.Should().Contain("First Last");
            authors.Should().Contain("First2 Last2");
            authors.Should().Contain("First3 Last3");
            authors.Should().HaveCount(4);
        }

        [Test]
        public void should_not_split_if_multiple_input()
        {
            var input = new List<string>
            {
                "First Last",
                "Second Third, Fourth Fifth"
            };

            var authors = DistanceCalculator.GetAuthorVariants(input);

            authors.Should().HaveCount(2);
            authors.Should().Contain("First Last");
            authors.Should().Contain("Second Third, Fourth Fifth");
        }

        // Issue #7. An absent author name is scored as maximum author-distance
        // rather than "unknown", so on an otherwise-perfect ISBN match it costs
        // 0.1875 of the 0.20 budget in CloseAlbumMatchSpecification — leaving no
        // room for any other imperfection. OpenLibraryProxy now resolves the
        // name for ISBN-sourced candidates; this pins what that is worth, so the
        // margin cannot quietly come back.
        [Test]
        public void absent_author_name_should_cost_most_of_the_accept_budget()
        {
            const string Isbn = "9780345391803";

            double Score(string editionAuthorName)
            {
                var edition = new Edition
                {
                    Title = "Neuromancer",
                    Isbn13 = Isbn,
                    Book = new Book
                    {
                        Title = "Neuromancer",
                        AuthorMetadata = new AuthorMetadata { Name = editionAuthorName }
                    }
                };

                var localBooks = new List<LocalBook>
                {
                    new LocalBook
                    {
                        Path = "/books/neuromancer.epub",
                        FileTrackInfo = new ParsedTrackInfo
                        {
                            Authors = new List<string> { "William Gibson" },
                            BookTitle = "Neuromancer",
                            Isbn = Isbn
                        }
                    }
                };

                return DistanceCalculator.BookDistance(localBooks, edition).NormalizedDistance();
            }

            Score("William Gibson").Should().Be(0.0);
            Score(string.Empty).Should().BeApproximately(0.1875, 0.0001);
        }

        // #13 review, Finding 1. StripIsbn keeps a valid ISBN-10 file tag at
        // ten digits, while an edition's Isbn13 is thirteen (listed, or derived
        // from an isbn_10-only record per #10). A raw string compare reads that
        // as a full 10.0-weight mismatch against the very edition the file was
        // looked up by — worse than the 0.1 isbn_missing it used to fall into.
        // Both sides are converted to ISBN-13 before comparison, so the ten-
        // digit tag is not penalised for its format.
        [Test]
        public void isbn10_file_tag_should_match_the_editions_isbn13()
        {
            double Score(string fileIsbn)
            {
                var edition = new Edition
                {
                    Title = "Dune",
                    Isbn13 = "9780441172719",
                    Book = new Book
                    {
                        Title = "Dune",
                        AuthorMetadata = new AuthorMetadata { Name = "Frank Herbert" }
                    }
                };

                var localBooks = new List<LocalBook>
                {
                    new LocalBook
                    {
                        Path = "/books/dune.epub",
                        FileTrackInfo = new ParsedTrackInfo
                        {
                            Authors = new List<string> { "Frank Herbert" },
                            BookTitle = "Dune",
                            Isbn = fileIsbn
                        }
                    }
                };

                return DistanceCalculator.BookDistance(localBooks, edition).NormalizedDistance();
            }

            // 0441172717 is the ISBN-10 form of 9780441172719: same edition,
            // so a perfect match, and identical to the ISBN-13-tagged form.
            Score("0441172717").Should().Be(0.0);
            Score("0441172717").Should().Be(Score("9780441172719"));

            // A genuinely different ISBN (0441172660 -> 9780441172665) is still
            // a real mismatch and must not be laundered into a match.
            Score("0441172660").Should().BeGreaterThan(0.0);

            // Not every file tag arrives through StripIsbn. AggregateCalibreData
            // assigns FileTrackInfo.Isbn straight from Calibre's identifiers
            // dictionary with no validation or normalisation at all, so a
            // hyphenated identifier reaches here verbatim — and under the raw
            // compare it never matched anything, whatever its format. That is a
            // defect older than the ISBN-13 derivation, and NormalizeIsbn fixes
            // it as a side effect: both ISBN forms match with hyphens in place.
            Score("978-0-441-17271-9").Should().Be(0.0);
            Score("0-441-17271-7").Should().Be(0.0);
        }

        // The same unvalidated Calibre path can also deliver a value that is
        // neither a valid ISBN-10 nor thirteen digits. ToIsbn13 maps it to null,
        // which moves it out of the 10.0-weight isbn bucket and into the 0.1
        // isbn_missing one — it used to compare unequal and take the full
        // penalty. Pinning the shift rather than blessing it: a value that
        // cannot be put in ISBN-13 space is not evidence of a *different* book,
        // so the lighter bucket is the defensible reading, but it is a change
        // in behaviour that the review describing this fix did not mention.
        [Test]
        public void unconvertible_file_tag_should_score_as_missing_rather_than_mismatched()
        {
            double Score(string fileIsbn)
            {
                var edition = new Edition
                {
                    Title = "Dune",
                    Isbn13 = "9780441172719",
                    Book = new Book
                    {
                        Title = "Dune",
                        AuthorMetadata = new AuthorMetadata { Name = "Frank Herbert" }
                    }
                };

                var localBooks = new List<LocalBook>
                {
                    new LocalBook
                    {
                        Path = "/books/dune.epub",
                        FileTrackInfo = new ParsedTrackInfo
                        {
                            Authors = new List<string> { "Frank Herbert" },
                            BookTitle = "Dune",
                            Isbn = fileIsbn
                        }
                    }
                };

                return DistanceCalculator.BookDistance(localBooks, edition).NormalizedDistance();
            }

            // "0441172661" fails its own ISBN-10 checksum; twelve digits is
            // neither length. Both now score as the edition having an ISBN the
            // file does not, which is what an absent tag scores.
            var missing = Score(null);

            Score("0441172661").Should().Be(missing);
            Score("978044117271").Should().Be(missing);

            // And that is strictly lighter than a real mismatch.
            missing.Should().BeLessThan(Score("0441172660"));
        }

        // A search-result candidate stands for a work and carries the ISBNs of
        // all its editions in WorkIsbns rather than one of them in Isbn13.
        // The list can confirm a match but never contradict one; the
        // end-to-end numbers are in SearchCandidateIsbnFixture.
        [Test]
        public void work_isbn_list_should_confirm_but_never_contradict()
        {
            Distance Score(string fileIsbn, string editionIsbn, List<string> workIsbns)
            {
                var edition = new Edition
                {
                    Title = "Dune",
                    Isbn13 = editionIsbn,
                    WorkIsbns = workIsbns,
                    Book = new Book
                    {
                        Title = "Dune",
                        AuthorMetadata = new AuthorMetadata { Name = "Frank Herbert" }
                    }
                };

                var localBooks = new List<LocalBook>
                {
                    new LocalBook
                    {
                        Path = "/books/dune.epub",
                        FileTrackInfo = new ParsedTrackInfo
                        {
                            Authors = new List<string> { "Frank Herbert" },
                            BookTitle = "Dune",
                            Isbn = fileIsbn
                        }
                    }
                };

                return DistanceCalculator.BookDistance(localBooks, edition);
            }

            var work = new List<string> { "9780143111580", "9780441172719" };

            // In the list, in either format: the isbn bucket, at distance 0.
            var listed = Score("9780441172719", null, work);
            listed.Penalties.Should().ContainKey("isbn").WhoseValue.Should().Equal(0.0);
            listed.Penalties.Should().NotContainKey("isbn_missing");
            Score("0441172717", null, work).Penalties.Should().ContainKey("isbn");

            // Not in the list: missing, never the isbn bucket at distance 1.
            var unlisted = Score("9780306406157", null, work);
            unlisted.Penalties.Should().ContainKey("isbn_missing");
            unlisted.Penalties.Should().NotContainKey("isbn");

            // File has none, the work has some: missing, as for an edition.
            Score(null, null, work).Penalties.Should().ContainKey("isbn_missing");

            // Neither side has one: nothing to say.
            Score(null, null, new List<string>()).Penalties.Should().NotContainKey("isbn_missing");
            Score(null, null, null).Penalties.Should().NotContainKey("isbn_missing");

            // A real edition's own ISBN is still an assertion and still
            // decides, whatever list happens to be attached.
            Score("9780441172719", "9780143111580", work).Penalties["isbn"].Should().Equal(1.0);
        }

        // A candidate mapped straight from a metadata source has not been
        // through a DB lazy load, so Book.AuthorMetadata can be null. Scoring
        // one must not take the whole import run down.
        [Test]
        public void should_score_candidate_with_no_author_metadata()
        {
            var edition = new Edition
            {
                Title = "Neuromancer",
                Book = new Book
                {
                    Title = "Neuromancer",
                    AuthorMetadata = null
                }
            };

            var localBooks = new List<LocalBook>
            {
                new LocalBook
                {
                    Path = "/books/neuromancer.epub",
                    FileTrackInfo = new ParsedTrackInfo
                    {
                        Authors = new List<string> { "William Gibson" },
                        BookTitle = "Neuromancer"
                    }
                }
            };

            var distance = DistanceCalculator.BookDistance(localBooks, edition);

            // An absent author is maximum author-distance, not a crash.
            distance.NormalizedDistance().Should().BeGreaterThan(0.0);
        }

        [Test]
        public void should_score_candidate_whose_book_is_not_attached()
        {
            var edition = new Edition
            {
                Title = "Neuromancer",
                Book = null
            };

            var localBooks = new List<LocalBook>
            {
                new LocalBook
                {
                    Path = "/books/neuromancer.epub",
                    FileTrackInfo = new ParsedTrackInfo
                    {
                        Authors = new List<string> { "William Gibson" },
                        BookTitle = "Neuromancer"
                    }
                }
            };

            var distance = DistanceCalculator.BookDistance(localBooks, edition);

            distance.NormalizedDistance().Should().BeGreaterThan(0.0);
        }
    }
}
