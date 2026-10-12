using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    // Phase 5 closeout. The master plan wanted a 500-book Goodreads-shaped
    // seed library + a >= 85% reidentify-match assertion. We don't carry a
    // 500-book snapshot in tree, but Phase 3 committed 116 real OL cassettes
    // — enough to drive the ISBN and title+author routes deterministically.
    //
    // Strategy: seed 10 books (5 with ISBN-13s that match `isbn_*.json`
    // cassettes, 5 with title+author shapes that match `search_*.json`
    // cassettes), wire the real OpenLibraryProxy against a cassette-backed
    // IHttpClient stub, run the reidentify pass, and assert the recorded
    // mappings clear the 85% threshold.
    //
    // If you add new ISBN or title+author cassettes, extend IsbnCassettes
    // / SearchCassettes below to bring them into the corpus.
    [TestFixture]
    public class ReidentifyRegressionFixture : CoreTest<ReidentifyService>
    {
        private const double TargetMatchRate = 0.85;

        // Mirror of ReidentifyService.MediumConfidence — anything at or above
        // this is "the wizard does not need to surface this for review".
        private const double MediumConfidence = 0.70;

        // Maps the ISBN embedded in the request URL to its cassette filename.
        private static readonly (string Isbn, string Cassette)[] IsbnCassettes =
        {
            ("9780441172719", "isbn_dune_9780441172719.json"),
            ("9780451524935", "isbn_1984_9780451524935.json"),
            ("9780553293357", "isbn_foundation_9780553293357.json"),
            ("9780261103573", "isbn_hobbit_9780261103573.json"),
            ("9780062316097", "isbn_sapiens_9780062316097.json"),
        };

        // Maps the (title, author) the proxy URL-encodes into search.json
        // back to its cassette. Match is substring on the lower-cased query
        // string so the table doesn't have to reproduce OL's exact encoding.
        private static readonly (string Title, string Author, string Cassette)[] SearchCassettes =
        {
            ("1984", "George Orwell", "search_1984_orwell.json"),
            ("Dune", "Frank Herbert", "search_dune_herbert.json"),
            ("Foundation", "Isaac Asimov", "search_foundation_asimov.json"),
            ("Norwegian Wood", "Haruki Murakami", "search_norwegian_wood.json"),
            ("Sapiens", "Yuval Noah Harari", "search_sapiens.json"),
        };

        private List<Book> _seedBooks;
        private Dictionary<int, List<Edition>> _seedEditions;
        private List<BookIdMapping> _writtenMappings;

        [SetUp]
        public void Setup()
        {
            _writtenMappings = new List<BookIdMapping>();
            _seedBooks = BuildSeedLibrary(out _seedEditions);
            WireServices();
            WireOpenLibraryProxy();
        }

        [Test]
        public void Reidentify_against_cassette_corpus_should_hit_at_least_85_percent()
        {
            Subject.Execute(new ReidentifyLibraryCommand());

            // Only count rows that the Phase 5 wizard would consider
            // "auto-accepted" — i.e. confidence >= 0.70. Anything below that
            // falls into the manual-review bucket and should not contribute
            // to the regression rate.
            var total = _seedBooks.Count;
            var matched = _writtenMappings.Count(m => m.Confidence >= MediumConfidence);
            var rate = (double)matched / total;

            rate.Should().BeGreaterOrEqualTo(
                TargetMatchRate,
                "Phase 5 exit criterion: reidentify must hit >= 85% of the seed library. " +
                "Hit {0}/{1} = {2:P0}. Recorded mappings: {3}",
                matched,
                total,
                rate,
                string.Join("; ", _writtenMappings.Select(m =>
                    $"{m.GoodreadsId}->{m.OpenLibraryWorkId} ({m.Source} {m.Confidence:F2})")));
        }

        [TestCaseSource(nameof(IsbnCases))]
        public void Isbn_route_should_map_cassette_to_high_confidence_work(string isbn, string cassette)
        {
            Subject.Execute(new ReidentifyLibraryCommand());

            var idx = Array.FindIndex(IsbnCassettes, t => t.Isbn == isbn);
            var bookId = $"GR-BOOK-{idx + 1}";
            var mapping = _writtenMappings.FirstOrDefault(m => m.GoodreadsId == bookId);

            mapping.Should().NotBeNull("book seeded with ISBN {0} must produce a mapping ({1})", isbn, cassette);
            mapping.Source.Should().Be(BookIdMappingSource.Isbn);
            mapping.Confidence.Should().BeGreaterOrEqualTo(0.95);
            mapping.OpenLibraryWorkId.Should().NotBeNullOrEmpty();

            // OL's /isbn/{isbn}.json points at an edition whose `works[0].key`
            // is the work this mapping should claim. Stripping the prefix is
            // the mapper's job; we just verify nothing leaked through.
            mapping.OpenLibraryWorkId.Should().NotStartWith("/", "mapper must strip the /works/ prefix");
        }

        // Goodreads numbered authors and works separately, so a library can
        // hold an author and a book with the same id. The author is mapped
        // first; its row used to make the book look already mapped.
        [Test]
        public void Book_sharing_its_goodreads_id_with_an_author_should_still_be_mapped()
        {
            var authorRow = new BookIdMapping
            {
                GoodreadsId = "GR-BOOK-1",
                OpenLibraryWorkId = null,
                OpenLibraryEditionId = "OL79034A",
                Confidence = 0.90,
                Source = BookIdMappingSource.TitleAuthor
            };
            _writtenMappings.Add(authorRow);

            Subject.Execute(new ReidentifyLibraryCommand());

            var rows = _writtenMappings.Where(m => m.GoodreadsId == "GR-BOOK-1").ToList();

            rows.Should().HaveCount(2);
            rows.Should().ContainSingle(m => m.IsBookMapping())
                .Which.Source.Should().Be(BookIdMappingSource.Isbn);

            // The author's row is as it was: still an author's, still its own.
            rows.Should().ContainSingle(m => m.IsAuthorMapping()).Which.Should().BeSameAs(authorRow);
            authorRow.OpenLibraryWorkId.Should().BeNull();
            authorRow.OpenLibraryEditionId.Should().Be("OL79034A");

            Mocker.GetMock<IBookIdMappingRepository>()
                  .Verify(r => r.Update(It.IsAny<BookIdMapping>()), Times.Never());
        }

        // A title search whose first hit has a different title is recorded
        // for review, below the line the factory translates through.
        [Test]
        public void Mappings_reidentify_was_unsure_of_should_be_stored_but_not_trusted()
        {
            _seedBooks.Add(new Book
            {
                Id = 99,
                ForeignBookId = "GR-BOOK-99",
                Title = "A Title OpenLibrary Does Not Return First",
                Author = new Author { Name = "Frank Herbert" }
            });

            // Whatever is searched for, the Dune results come back.
            Mocker.GetMock<IHttpClient>()
                  .Setup(c => c.Get<OpenLibrarySearchResource>(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(req => BuildResponse<OpenLibrarySearchResource>(req, "search_dune_herbert.json"));

            Subject.Execute(new ReidentifyLibraryCommand());

            var guess = _writtenMappings.Single(m => m.GoodreadsId == "GR-BOOK-99");

            guess.Confidence.Should().BeLessThan(MediumConfidence);
            guess.IsTrusted().Should().BeFalse();

            _writtenMappings.Where(m => m.Confidence >= MediumConfidence)
                .Should().OnlyContain(m => m.IsTrusted());
        }

        public static IEnumerable<TestCaseData> IsbnCases()
        {
            foreach (var (isbn, cassette) in IsbnCassettes)
            {
                yield return new TestCaseData(isbn, cassette).SetName($"{{m}}({cassette})");
            }
        }

        private static List<Book> BuildSeedLibrary(out Dictionary<int, List<Edition>> editionsByBook)
        {
            var books = new List<Book>();
            editionsByBook = new Dictionary<int, List<Edition>>();
            var id = 1;

            foreach (var (isbn, _) in IsbnCassettes)
            {
                var book = new Book
                {
                    Id = id,
                    ForeignBookId = $"GR-BOOK-{id}",
                    Title = "ISBN-route seed",
                    Author = new Author { Name = "Seed Author" }
                };
                editionsByBook[id] = new List<Edition>
                {
                    new Edition { Id = id, BookId = id, Isbn13 = isbn, Monitored = true }
                };
                books.Add(book);
                id++;
            }

            foreach (var (title, author, _) in SearchCassettes)
            {
                var book = new Book
                {
                    Id = id,
                    ForeignBookId = $"GR-BOOK-{id}",
                    Title = title,
                    Author = new Author { Name = author }
                };
                editionsByBook[id] = new List<Edition>();
                books.Add(book);
                id++;
            }

            return books;
        }

        private void WireServices()
        {
            // Skip the author pass — the per-author cassette set isn't keyed
            // to author names, and MapAuthor's no-op-on-miss path would
            // pollute the mapping count.
            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.GetAllAuthors())
                  .Returns(new List<Author>());

            Mocker.GetMock<IBookService>()
                  .Setup(s => s.GetAllBooks())
                  .Returns(() => _seedBooks);

            Mocker.GetMock<IEditionService>()
                  .Setup(s => s.GetEditionsByBook(It.IsAny<int>()))
                  .Returns<int>(bookId =>
                      _seedEditions.TryGetValue(bookId, out var eds) ? eds : new List<Edition>());

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesByBook(It.IsAny<int>()))
                  .Returns(new List<BookFile>());

            // By kind, as the repository answers: a book lookup never sees
            // an author's row and the other way round.
            Mocker.GetMock<IBookIdMappingRepository>()
                  .Setup(r => r.FindBookByGoodreadsId(It.IsAny<string>()))
                  .Returns<string>(gr => _writtenMappings.FirstOrDefault(m => m.GoodreadsId == gr && m.IsBookMapping()));

            Mocker.GetMock<IBookIdMappingRepository>()
                  .Setup(r => r.FindAuthorByGoodreadsId(It.IsAny<string>()))
                  .Returns<string>(gr => _writtenMappings.FirstOrDefault(m => m.GoodreadsId == gr && m.IsAuthorMapping()));

            Mocker.GetMock<IBookIdMappingRepository>()
                  .Setup(r => r.Insert(It.IsAny<BookIdMapping>()))
                  .Returns<BookIdMapping>(m =>
                  {
                      _writtenMappings.Add(m);
                      return m;
                  });
        }

        private void WireOpenLibraryProxy()
        {
            // Real request builder so the URL shape matches production.
            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            // /isbn/{isbn}.json → OpenLibraryEditionResource
            Mocker.GetMock<IHttpClient>()
                  .Setup(c => c.Get<OpenLibraryEditionResource>(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(req =>
                      BuildResponse<OpenLibraryEditionResource>(req, ResolveCassette(req)));

            // /search.json?title=...&author=... → OpenLibrarySearchResource
            Mocker.GetMock<IHttpClient>()
                  .Setup(c => c.Get<OpenLibrarySearchResource>(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(req =>
                      BuildResponse<OpenLibrarySearchResource>(req, ResolveCassette(req)));
        }

        private static string ResolveCassette(HttpRequest request)
        {
            var path = request.Url.Path ?? string.Empty;
            var query = (request.Url.Query ?? string.Empty).ToLowerInvariant();

            if (path.Contains("/isbn/"))
            {
                foreach (var (isbn, cassette) in IsbnCassettes)
                {
                    if (path.Contains(isbn))
                    {
                        return cassette;
                    }
                }
            }

            if (path.EndsWith("/search.json"))
            {
                foreach (var (title, author, cassette) in SearchCassettes)
                {
                    var titleNeedle = "title=" + Uri.EscapeDataString(title).ToLowerInvariant();
                    var authorNeedle = "author=" + Uri.EscapeDataString(author).ToLowerInvariant();
                    if (query.Contains(titleNeedle) && query.Contains(authorNeedle))
                    {
                        return cassette;
                    }
                }
            }

            return null;
        }

        private static HttpResponse<T> BuildResponse<T>(HttpRequest req, string cassetteName)
            where T : new()
        {
            // No cassette match → empty JSON body. The proxy's null-checks
            // turn that into "no hits" and reidentify falls through to the
            // next route or records nothing, exactly as it would on a 404.
            var content = "{}";
            if (cassetteName != null)
            {
                var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "OpenLibrary", cassetteName);
                if (File.Exists(path))
                {
                    content = File.ReadAllText(path);
                }
            }

            return new HttpResponse<T>(new HttpResponse(req, new HttpHeader(), content, HttpStatusCode.OK));
        }
    }
}
