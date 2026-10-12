using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MetadataSource
{
    // How a library imported from Readarr is translated to and from
    // OpenLibrary ids through the BookIdMapping table.
    [TestFixture]
    public class MetadataSourceFactoryLegacyFixture : CoreTest<MetadataSourceFactory>
    {
        private const string LegacyAuthor = "656983";
        private const string AuthorOlid = "OL26320A";

        private const string LegacyBook = "3275001";
        private const string LegacyTwin = "3275003";
        private const string WorkOlid = "OL3140822W";

        private List<BookIdMapping> _rows;
        private HashSet<string> _booksInLibrary;

        [SetUp]
        public void Setup()
        {
            _rows = new List<BookIdMapping>();
            _booksInLibrary = new HashSet<string>();

            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.MetadataSourceType)
                .Returns(MetadataSourceFactory.OpenLibrarySource);

            // The same split by kind, and the same order of preference, the
            // repository gives; its own fixture covers the real thing.
            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.FindBookByGoodreadsId(It.IsAny<string>()))
                .Returns<string>(id => _rows.FirstOrDefault(x => x.GoodreadsId == id && x.IsBookMapping()));

            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.FindAuthorByGoodreadsId(It.IsAny<string>()))
                .Returns<string>(id => _rows.FirstOrDefault(x => x.GoodreadsId == id && x.IsAuthorMapping()));

            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.FindBooksByOpenLibraryWorkId(It.IsAny<string>()))
                .Returns<string>(id => _rows.Where(x => x.OpenLibraryWorkId == id && x.IsBookMapping())
                    .OrderByDescending(x => x.Source == BookIdMappingSource.Manual)
                    .ThenByDescending(x => x.Confidence)
                    .ThenBy(x => x.Id)
                    .ToList());

            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.FindAuthorByOpenLibraryAuthorId(It.IsAny<string>()))
                .Returns<string>(id => _rows.FirstOrDefault(x => x.OpenLibraryEditionId == id && x.IsAuthorMapping()));

            Mocker.GetMock<IBookRepository>()
                .Setup(s => s.FindById(It.IsAny<string>()))
                .Returns<string>(id => _booksInLibrary.Contains(id) ? new Book { ForeignBookId = id } : null);
        }

        private void GivenAuthorRow(string goodreadsId, string olid, double confidence, string source = BookIdMappingSource.TitleAuthor)
        {
            _rows.Add(new BookIdMapping
            {
                Id = _rows.Count + 1,
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = null,
                OpenLibraryEditionId = olid,
                Confidence = confidence,
                Source = source
            });
        }

        private void GivenBookRow(string goodreadsId, string workId, double confidence, string source = BookIdMappingSource.Isbn, bool inLibrary = true)
        {
            _rows.Add(new BookIdMapping
            {
                Id = _rows.Count + 1,
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = workId,
                OpenLibraryEditionId = "OL7353617M",
                Confidence = confidence,
                Source = source
            });

            if (inLibrary)
            {
                _booksInLibrary.Add(goodreadsId);
            }
        }

        // Serves the author and works cassettes whatever author is asked
        // for, and records the paths asked for.
        private List<string> GivenOpenLibraryAuthor()
        {
            var requested = new List<string>();

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Get<OpenLibraryAuthorResource>(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(req =>
                {
                    requested.Add(req.Url.Path);
                    return Cassette<OpenLibraryAuthorResource>(req, "author_tolkien.json");
                });

            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Get<OpenLibraryAuthorWorksResource>(It.IsAny<HttpRequest>()))
                .Returns<HttpRequest>(req => SinglePage(Cassette<OpenLibraryAuthorWorksResource>(req, "author_tolkien_works.json")));

            return requested;
        }

        // The cassette is the first page of a longer list and says so. This
        // mock answers every request with it, so drop the pointer to the next
        // page or the proxy would follow it back to the same page until its
        // page limit.
        private static HttpResponse<OpenLibraryAuthorWorksResource> SinglePage(HttpResponse<OpenLibraryAuthorWorksResource> response)
        {
            response.Resource.Links = null;
            response.Resource.Size = response.Resource.Entries.Count;

            return response;
        }

        private static HttpResponse<T> Cassette<T>(HttpRequest req, string name)
            where T : new()
        {
            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "OpenLibrary", name);

            return new HttpResponse<T>(new HttpResponse(req, new HttpHeader(), File.ReadAllText(path), HttpStatusCode.OK));
        }

        [Test]
        public void should_not_take_a_books_row_for_the_author_with_the_same_id()
        {
            GivenBookRow("3389", WorkOlid, 0.95);

            Subject.IsUnmappedLegacyAuthor("3389").Should().BeTrue();
            Subject.IsUnmappedLegacyBook("3389").Should().BeFalse();
        }

        [Test]
        public void should_not_take_an_authors_row_for_the_book_with_the_same_id()
        {
            GivenAuthorRow("3389", AuthorOlid, 0.90);

            Subject.IsUnmappedLegacyBook("3389").Should().BeTrue();
            Subject.IsUnmappedLegacyAuthor("3389").Should().BeFalse();
        }

        [Test]
        public void should_translate_both_when_an_author_and_a_book_share_an_id()
        {
            GivenAuthorRow("3389", AuthorOlid, 0.90);
            GivenBookRow("3389", WorkOlid, 0.95);

            Subject.IsUnmappedLegacyAuthor("3389").Should().BeFalse();
            Subject.IsUnmappedLegacyBook("3389").Should().BeFalse();
        }

        [TestCase(0.50, true)]
        [TestCase(0.55, true)]
        [TestCase(0.69, true)]
        [TestCase(0.70, false)]
        [TestCase(0.85, false)]
        public void should_treat_a_book_with_only_a_guessed_mapping_as_unmapped(double confidence, bool unmapped)
        {
            GivenBookRow(LegacyBook, WorkOlid, confidence, BookIdMappingSource.TitleAuthor);

            Subject.IsUnmappedLegacyBook(LegacyBook).Should().Be(unmapped);
        }

        [TestCase(0.50, true)]
        [TestCase(0.90, false)]
        public void should_treat_an_author_with_only_a_guessed_mapping_as_unmapped(double confidence, bool unmapped)
        {
            GivenAuthorRow(LegacyAuthor, AuthorOlid, confidence);

            Subject.IsUnmappedLegacyAuthor(LegacyAuthor).Should().Be(unmapped);
        }

        // What the review page's save writes, whatever confidence the row
        // started with.
        [Test]
        public void should_use_a_mapping_the_user_confirmed()
        {
            GivenAuthorRow(LegacyAuthor, AuthorOlid, 1.0, BookIdMappingSource.Manual);
            GivenBookRow(LegacyBook, WorkOlid, 1.0, BookIdMappingSource.Manual);

            Subject.IsUnmappedLegacyAuthor(LegacyAuthor).Should().BeFalse();
            Subject.IsUnmappedLegacyBook(LegacyBook).Should().BeFalse();
        }

        [Test]
        public void should_not_call_native_ids_legacy()
        {
            Subject.IsUnmappedLegacyAuthor(AuthorOlid).Should().BeFalse();
            Subject.IsUnmappedLegacyBook(WorkOlid).Should().BeFalse();
        }

        [Test]
        public void should_ask_the_source_for_the_olid_of_a_mapped_author()
        {
            var requested = GivenOpenLibraryAuthor();
            GivenAuthorRow(LegacyAuthor, AuthorOlid, 0.90);

            var author = Subject.GetAuthorInfo(LegacyAuthor);

            requested.Should().ContainSingle().Which.Should().EndWith($"authors/{AuthorOlid}.json");
            author.Metadata.Value.ForeignAuthorId.Should().Be(LegacyAuthor);
        }

        // A guess can be a different person altogether. Refreshing through
        // it would hold the author's books up against that person's works.
        [Test]
        public void should_not_ask_the_source_for_the_olid_of_a_guessed_author()
        {
            var requested = GivenOpenLibraryAuthor();
            GivenAuthorRow(LegacyAuthor, AuthorOlid, 0.50);

            Subject.GetAuthorInfo(LegacyAuthor);

            requested.Should().ContainSingle().Which.Should().EndWith($"authors/{LegacyAuthor}.json");
        }

        [Test]
        public void should_name_the_book_refreshed_instead_when_two_legacy_books_share_a_work()
        {
            GivenBookRow(LegacyTwin, WorkOlid, 0.85, BookIdMappingSource.TitleAuthor);
            GivenBookRow(LegacyBook, WorkOlid, 0.95);

            Subject.FindBookRefreshedInstead(LegacyTwin).Should().Be(LegacyBook);
            Subject.FindBookRefreshedInstead(LegacyBook).Should().BeNull();
        }

        [Test]
        public void should_name_nothing_for_a_book_that_has_its_work_to_itself()
        {
            GivenBookRow(LegacyBook, WorkOlid, 0.95);

            Subject.FindBookRefreshedInstead(LegacyBook).Should().BeNull();
            Subject.FindBookRefreshedInstead("OL1W").Should().BeNull();
            Subject.FindBookRefreshedInstead("12345").Should().BeNull();
        }

        // A book added since the switch carries the work id itself, and the
        // works list hands that work to the legacy book mapped to it.
        [Test]
        public void should_name_the_legacy_book_for_a_native_book_of_the_same_work()
        {
            GivenBookRow(LegacyBook, WorkOlid, 0.95);

            Subject.FindBookRefreshedInstead(WorkOlid).Should().Be(LegacyBook);
        }

        // Mapping rows outlive their books.
        [Test]
        public void should_not_let_a_mapping_for_a_book_no_longer_in_the_library_claim_the_work()
        {
            GivenBookRow(LegacyBook, WorkOlid, 0.95, inLibrary: false);
            GivenBookRow(LegacyTwin, WorkOlid, 0.85, BookIdMappingSource.TitleAuthor);

            Subject.FindBookRefreshedInstead(LegacyTwin).Should().BeNull();
            Subject.FindBookRefreshedInstead(WorkOlid).Should().Be(LegacyTwin);
        }

        [Test]
        public void should_not_let_a_guessed_mapping_claim_the_work()
        {
            GivenBookRow(LegacyBook, WorkOlid, 0.55, BookIdMappingSource.TitleAuthor);

            Subject.FindBookRefreshedInstead(WorkOlid).Should().BeNull();
        }

        [Test]
        public void should_list_a_work_shared_by_two_legacy_books_once_under_the_preferred_id()
        {
            GivenOpenLibraryAuthor();
            GivenAuthorRow(LegacyAuthor, AuthorOlid, 0.90);

            var workId = Subject.GetAuthorInfo(AuthorOlid).Books.Value.First().ForeignBookId;
            OpenLibraryIdHelper.IsWorkId(workId).Should().BeTrue();

            GivenBookRow(LegacyTwin, workId, 0.85, BookIdMappingSource.TitleAuthor);
            GivenBookRow(LegacyBook, workId, 0.95);

            var ids = Subject.GetAuthorInfo(LegacyAuthor).Books.Value.Select(x => x.ForeignBookId).ToList();

            ids.Should().ContainSingle(x => x == LegacyBook);
            ids.Should().NotContain(LegacyTwin);
            ids.Should().NotContain(workId);
        }
    }
}
