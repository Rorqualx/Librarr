using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.MetadataSource.OpenLibrary.Fixtures;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.BookImport.Identification
{
    // What a file with an ISBN tag scores against the candidates a title/author
    // search produces, which is the path identification takes when the direct
    // /isbn/ lookup misses or fails (CandidateService swallows both).
    //
    // Driven end to end -- the captured search.json payload, the real mapper,
    // CandidateService's fallback and DistanceCalculator -- because the defect
    // was in how those fit together, not in any one of them. A search document
    // is a *work*, and its `isbn` array lists the ISBNs of every edition of
    // that work: 187 of them for Dune in Files/OpenLibrary/search_dune_herbert.json.
    // The mapper used to stamp the first 13-character entry (9780143111580, a
    // Penguin edition) on the candidate as though it were the candidate's own.
    // A file tagged with any of the other 186 then read as a full mismatch in
    // the 10.0-weight `isbn` bucket and the right book was thrown out.
    //
    // Measured on the captured payload, file tagged Frank Herbert / Dune /
    // 9780441172719 (the Ace paperback, the ISBN the isbn_dune cassette was
    // captured with):
    //
    //                                              before    after
    //   Dune, exact title                          0.6250    0.0000
    //   Dune, file titled "Dune (Dune Chronicles)" 0.7708    0.1458   (gate 0.20)
    //   Dune Messiah, the wrong work               0.7443    0.3294
    //
    // and with a file ISBN that is not in OpenLibrary's list for the work at
    // all, which is the same as the file having no ISBN:
    //
    //   Dune, exact title                          0.6250    0.0164
    //   Dune, file titled "Dune (Dune Chronicles)" 0.7708    0.3989
    //
    // The "before" column is the 10.0-weight isbn bucket at distance 1 in
    // every row: (3 x title distance + 10) / 16. Nothing could be accepted.
    [TestFixture]
    public class SearchCandidateIsbnFixture : CoreTest<CandidateService>
    {
        // CloseAlbumMatchSpecification's accept threshold.
        private const double Gate = 0.20;

        private const string DuneWork = "OL893415W";
        private const string DuneMessiahWork = "OL893526W";

        // In Dune's work-level list, and not the entry the mapper used to pick.
        private const string AcePaperbackIsbn = "9780441172719";

        [SetUp]
        public void Setup()
        {
            var search = OpenLibraryFixtureLoader.Load<OpenLibrarySearchResource>("search_dune_herbert.json");

            // The direct lookup comes back empty-handed, as it does for an
            // edition OpenLibrary has no record of under that ISBN.
            Mocker.GetMock<ISearchForNewBook>()
                .Setup(s => s.SearchByIsbn(It.IsAny<string>()))
                .Throws(new HttpException(new HttpResponse(
                    new HttpRequest("https://openlibrary.org/isbn/0.json"),
                    new HttpHeader(),
                    Array.Empty<byte>(),
                    HttpStatusCode.NotFound)));

            Mocker.GetMock<ISearchForNewBook>()
                .Setup(s => s.SearchForNewBook(It.IsAny<string>(), It.IsAny<string>(), true))
                .Returns((string title, string author, bool all) => OpenLibrarySearchMapper.ReRankAndMap(search, title, author));
        }

        private double Score(string workId, string fileIsbn, string fileTitle = "Dune")
        {
            var localBooks = new List<LocalBook>
            {
                new LocalBook
                {
                    Path = "/books/dune.epub",
                    FileTrackInfo = new ParsedTrackInfo
                    {
                        Authors = new List<string> { "Frank Herbert" },
                        BookTitle = fileTitle,
                        Isbn = fileIsbn
                    }
                }
            };

            var candidates = Subject.GetRemoteCandidates(new LocalEdition { LocalBooks = localBooks }, null).ToList();

            // The swallowed ISBN miss.
            if (fileIsbn != null)
            {
                ExceptionVerification.ExpectedWarns(1);
            }

            var candidate = candidates.Single(c => c.Edition.Book.Value.ForeignBookId == workId);

            return DistanceCalculator.BookDistance(localBooks, candidate.Edition).NormalizedDistance();
        }

        [Test]
        public void search_candidate_should_not_claim_one_editions_isbn_as_its_own()
        {
            var search = OpenLibraryFixtureLoader.Load<OpenLibrarySearchResource>("search_dune_herbert.json");

            var dune = OpenLibrarySearchMapper.ReRankAndMap(search, "Dune", "Frank Herbert")
                .Single(b => b.ForeignBookId == DuneWork);

            // The placeholder edition stands for the whole work. It has no ISBN
            // of its own to assert.
            dune.Editions.Value.Single().Isbn13.Should().BeNull();
        }

        [Test]
        public void file_whose_isbn_belongs_to_a_non_first_edition_should_match_the_work()
        {
            // Was 0.6250: author and title perfect, and the whole 10.0-weight
            // isbn bucket lost to an ISBN the candidate never really had.
            Score(DuneWork, AcePaperbackIsbn).Should().Be(0.0);
        }

        [Test]
        public void isbn10_file_tag_should_match_the_work_too()
        {
            // 0441172717 is the ISBN-10 form of the same edition. The work list
            // carries both forms; the comparison is in ISBN-13 space either way.
            Score(DuneWork, "0441172717").Should().Be(0.0);
        }

        [Test]
        public void isbn_in_the_works_list_should_carry_an_imperfect_title_over_the_gate()
        {
            // This is what carrying the list buys over simply dropping the
            // ISBN. Without one the title difference alone decides, and
            // decides against.
            Score(DuneWork, null, "Dune (Dune Chronicles)").Should().BeApproximately(0.3989, 0.0001).And.BeGreaterThan(Gate);

            // Was 0.7708.
            Score(DuneWork, AcePaperbackIsbn, "Dune (Dune Chronicles)").Should().BeApproximately(0.1458, 0.0001).And.BeLessThan(Gate);
        }

        [Test]
        public void isbn_the_work_does_not_list_should_score_as_missing_not_mismatched()
        {
            // A valid ISBN-13 that is not among the 187. OpenLibrary's list is
            // only as complete as its edition records, and this path is reached
            // precisely when OL did not know the file's ISBN -- so its absence
            // says nothing about whether this is the right work.
            //
            // Was 0.6250. Now exactly what a file with no ISBN scores.
            Score(DuneWork, "9780306406157").Should().BeApproximately(0.1 / 6.1, 0.0001);
        }

        [Test]
        public void isbn_of_another_work_should_not_make_an_unrelated_work_match()
        {
            // Dune's ISBN is not in Dune Messiah's list, so Messiah gets no
            // credit for it and stays where the title puts it: rejected, and
            // by the same margin as if the file had no ISBN at all.
            var messiah = Score(DuneMessiahWork, AcePaperbackIsbn);

            messiah.Should().BeApproximately(0.3294, 0.0001).And.BeGreaterThan(Gate);
            messiah.Should().Be(Score(DuneMessiahWork, null));
        }

        [Test]
        public void the_right_work_should_outscore_its_neighbours_in_the_same_result()
        {
            // Identification takes the closest candidate. The search returned
            // five Dune novels; only one of them lists the file's ISBN.
            var dune = Score(DuneWork, AcePaperbackIsbn);
            var messiah = Score(DuneMessiahWork, AcePaperbackIsbn);

            dune.Should().BeLessThan(messiah);
        }

        [Test]
        public void file_without_an_isbn_should_score_as_it_did_before()
        {
            // The candidate still knows ISBNs the file does not: the light
            // isbn_missing penalty, unchanged.
            Score(DuneWork, null).Should().BeApproximately(0.1 / 6.1, 0.0001);
        }
    }
}
