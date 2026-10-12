using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.MetadataSource.OpenLibrary.Fixtures;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // The search methods cache for an hour (title and author searches) or
    // thirty days (isbn:, asin:, edition:). What they cached was the *mapped*
    // Book and Author objects, so every caller inside the TTL was handed the
    // same instances -- and the callers write to them:
    //
    //   - the lookup controllers hand the edition's Images to
    //     MediaCoverService.ConvertToLocalUrls, which rewrites Url in place to
    //     a /MediaCoverProxy/ address that is registered for 24 hours;
    //   - CandidateService.ToCandidates attaches authors and back-references;
    //   - whatever adds the book afterwards assigns database ids.
    //
    // So a repeat of the same search was served objects carrying the previous
    // caller's edits: a cover URL pointing at a registration that expires 29
    // days before the cache entry does, and import identification reading
    // proxy URLs where it expects OpenLibrary's.
    //
    // GetAuthorInfo and GetBookInfo have always cached the HTTP resources and
    // mapped per call for exactly this reason (see the note on GetAuthorInfo).
    // These tests pin the same contract for the search methods: one request,
    // and a fresh object graph for every caller.
    [TestFixture]
    public class OpenLibrarySearchCacheFixture : CoreTest<OpenLibraryProxy>
    {
        private List<string> _requested;

        [SetUp]
        public void Setup()
        {
            _requested = new List<string>();

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            Serve<OpenLibrarySearchResource>(url => "search_dune_herbert.json");
            Serve<OpenLibraryAuthorSearchResource>(url => "search_author_sanderson.json");
            Serve<OpenLibraryEditionResource>(url => "isbn_dune_9780441172719.json");
            Serve<OpenLibraryAuthorResource>(url => url.Contains("authors/OL79034A.json") ? "author_frank_herbert.json" : null);
            Serve<OpenLibraryWorkResource>(url => null);
        }

        // A cassette name, or null for a 404.
        private void Serve<T>(System.Func<string, string> cassetteFor)
            where T : new()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Get<T>(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest r) =>
                {
                    var url = r.Url.ToString();
                    _requested.Add(url);

                    var cassette = cassetteFor(url);
                    var body = cassette == null ? string.Empty : OpenLibraryFixtureLoader.LoadJson(cassette);
                    var status = cassette == null ? HttpStatusCode.NotFound : HttpStatusCode.OK;

                    return new HttpResponse<T>(new HttpResponse(r, new HttpHeader(), Encoding.UTF8.GetBytes(body), status));
                });
        }

        private int Requests(string fragment) => _requested.Count(u => u.Contains(fragment));

        // What MediaCoverService.ConvertToLocalUrls does to a lookup result.
        private static void RewriteCoversInPlace(Book book)
        {
            foreach (var cover in book.Editions.Value.SelectMany(e => e.Images))
            {
                cover.RemoteUrl = cover.Url;
                cover.Url = "/MediaCoverProxy/0123abcd/cover.jpg";
            }
        }

        private static List<string> CoverUrls(Book book)
        {
            return book.Editions.Value.SelectMany(e => e.Images).Select(i => i.Url).ToList();
        }

        [Test]
        public void repeated_title_search_should_not_see_a_previous_callers_cover_rewrite()
        {
            var first = Subject.SearchForNewBook("Dune", "Frank Herbert");
            var original = CoverUrls(first[0]);

            original.Should().NotBeEmpty();
            original.Should().OnlyContain(u => u.StartsWith("https://covers.openlibrary.org/"));

            RewriteCoversInPlace(first[0]);

            var second = Subject.SearchForNewBook("Dune", "Frank Herbert");

            CoverUrls(second[0]).Should().Equal(original);
            second[0].Editions.Value.SelectMany(e => e.Images).Should().OnlyContain(i => i.RemoteUrl == null);

            Requests("search.json").Should().Be(1, "the second search is still served from the cache");
        }

        [Test]
        public void repeated_title_search_should_not_see_what_identification_or_an_add_wrote()
        {
            var first = Subject.SearchForNewBook("Dune", "Frank Herbert")[0];

            // CandidateService.ToCandidates, then an add.
            first.Id = 42;
            first.Monitored = true;
            first.AuthorMetadata.Value.Name = "Somebody Else";
            first.Editions.Value[0].Id = 7;
            first.Editions.Value[0].BookId = 42;
            first.Editions.Value[0].Book = first;

            var second = Subject.SearchForNewBook("Dune", "Frank Herbert")[0];

            second.Should().NotBeSameAs(first);
            second.Id.Should().Be(0);
            second.AuthorMetadata.Value.Name.Should().Be("Frank Herbert");
            second.Editions.Value[0].Should().NotBeSameAs(first.Editions.Value[0]);
            second.Editions.Value[0].Id.Should().Be(0);
            second.Editions.Value[0].BookId.Should().Be(0);
        }

        // The thirty-day one, and the one import identification asks first.
        [Test]
        public void repeated_isbn_search_should_not_see_a_previous_callers_cover_rewrite()
        {
            var first = Subject.SearchByIsbn("9780441172719");
            var original = CoverUrls(first[0]);

            original.Should().NotBeEmpty();

            RewriteCoversInPlace(first[0]);
            first[0].Editions.Value[0].Id = 7;

            var second = Subject.SearchByIsbn("9780441172719");

            second[0].Should().NotBeSameAs(first[0]);
            CoverUrls(second[0]).Should().Equal(original);
            second[0].Editions.Value[0].Id.Should().Be(0);

            Requests("isbn/9780441172719.json").Should().Be(1);
        }

        // Mapping per call must not cost the author-name lookup its cache: that
        // name used to ride along on the shared Book instance.
        [Test]
        public void repeated_isbn_search_should_still_resolve_the_author_name_once()
        {
            Subject.SearchByIsbn("9780441172719")[0].AuthorMetadata.Value.Name.Should().Be("Frank Herbert");
            Subject.SearchByIsbn("9780441172719")[0].AuthorMetadata.Value.Name.Should().Be("Frank Herbert");

            Requests("authors/OL79034A.json").Should().Be(1);
        }

        [Test]
        public void repeated_edition_lookup_should_return_fresh_objects()
        {
            var first = Subject.SearchByForeignBookId("OL7524980M", false);

            RewriteCoversInPlace(first[0]);

            var second = Subject.SearchByForeignBookId("OL7524980M", false);

            second[0].Should().NotBeSameAs(first[0]);
            CoverUrls(second[0]).Should().OnlyContain(u => u.StartsWith("https://covers.openlibrary.org/"));

            Requests("books/OL7524980M.json").Should().Be(1);
        }

        [Test]
        public void repeated_asin_search_should_return_fresh_objects()
        {
            var first = Subject.SearchByAsin("B00B7NPRY8");

            RewriteCoversInPlace(first[0]);

            var second = Subject.SearchByAsin("B00B7NPRY8");

            second[0].Should().NotBeSameAs(first[0]);
            CoverUrls(second[0]).Should().OnlyContain(u => u.StartsWith("https://covers.openlibrary.org/"));

            Requests("search.json").Should().Be(1);
        }

        [Test]
        public void repeated_author_search_should_return_fresh_objects()
        {
            var first = Subject.SearchForNewAuthor("Brandon Sanderson");

            first.Should().NotBeEmpty();
            first[0].Id = 99;
            first[0].Metadata.Value.Images[0].Url = "/MediaCoverProxy/0123abcd/photo.jpg";

            var second = Subject.SearchForNewAuthor("Brandon Sanderson");

            second[0].Should().NotBeSameAs(first[0]);
            second[0].Id.Should().Be(0);
            second[0].Metadata.Value.Images[0].Url.Should().StartWith("https://covers.openlibrary.org/");

            Requests("search/authors.json").Should().Be(1);
        }

        // An answer with nothing in it is still an answer, and is remembered
        // like any other. (A real 404 throws and is deliberately not cached --
        // see SearchByIsbn; this is the empty-body case.)
        [Test]
        public void isbn_lookup_that_came_back_empty_should_still_be_cached()
        {
            Serve<OpenLibraryEditionResource>(url => null);

            Subject.SearchByIsbn("9780000000002").Should().BeEmpty();
            Subject.SearchByIsbn("9780000000002").Should().BeEmpty();

            Requests("isbn/9780000000002.json").Should().Be(1);
        }
    }
}
