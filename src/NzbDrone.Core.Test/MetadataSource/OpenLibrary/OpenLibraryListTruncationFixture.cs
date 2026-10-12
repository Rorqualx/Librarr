using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Test.MetadataSource.OpenLibrary.Fixtures;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // OpenLibrary's list endpoints answer with one page and say how much more
    // there is: `size` for the total, `links.next` for where the rest starts.
    // The proxy asked for one page of each list and took it for the list.
    //
    // For an author's works that silently dropped books. The comment that set
    // the page to 1000 reckoned Asimov at about 500 works; the captured
    // author_asimov_works.json reports 1455. The works list is now read to the
    // end.
    //
    // For a work's editions it is deliberate -- Pride and Prejudice has 4038
    // and nobody wants 4038 edition rows -- so that list stays capped. What
    // changes is that the cap is no longer silent.
    [TestFixture]
    public class OpenLibraryListTruncationFixture : CoreTest<OpenLibraryProxy>
    {
        private const string AuthorJson = @"{ ""key"": ""/authors/OL34221A"", ""name"": ""Isaac Asimov"", ""type"": { ""key"": ""/type/author"" } }";

        private const string WorkJson = @"{ ""key"": ""/works/OL66554W"", ""title"": ""Pride and Prejudice"", ""type"": { ""key"": ""/type/work"" } }";

        private List<string> _requested;

        [SetUp]
        public void Setup()
        {
            _requested = new List<string>();

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            Given<OpenLibraryAuthorResource>(url => AuthorJson);
            Given<OpenLibraryWorkResource>(url => WorkJson);
        }

        private void Given<T>(Func<string, string> jsonFor)
            where T : new()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Get<T>(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest r) =>
                {
                    var url = r.Url.ToString();
                    _requested.Add(url);

                    return new HttpResponse<T>(new HttpResponse(r, new HttpHeader(), Encoding.UTF8.GetBytes(jsonFor(url)), HttpStatusCode.OK));
                });
        }

        private List<string> WorksRequests => _requested.Where(u => u.Contains("/works.json")).ToList();

        // A page of the works list, in the shape OL serves it. `next` is what
        // OL puts in links.next when there is more, or null on the last page.
        private static string WorksPage(int size, int from, int count, string next)
        {
            var entries = Enumerable.Range(from, count)
                .Select(i => $@"{{ ""key"": ""/works/OL{i}W"", ""title"": ""Work {i}"", ""type"": {{ ""key"": ""/type/work"" }} }}");

            var links = next == null
                ? @"{ ""self"": ""/authors/OL34221A/works.json"", ""author"": ""/authors/OL34221A"" }"
                : $@"{{ ""self"": ""/authors/OL34221A/works.json"", ""author"": ""/authors/OL34221A"", ""next"": ""{next}"" }}";

            return $@"{{ ""links"": {links}, ""size"": {size}, ""entries"": [{string.Join(",", entries)}] }}";
        }

        private static int OffsetOf(string url)
        {
            var marker = "offset=";
            var at = url.IndexOf(marker, StringComparison.Ordinal);

            return at < 0 ? 0 : int.Parse(new string(url.Substring(at + marker.Length).TakeWhile(char.IsDigit).ToArray()));
        }

        [Test]
        public void captured_works_list_should_expose_the_total_and_the_next_page()
        {
            var works = OpenLibraryFixtureLoader.Load<OpenLibraryAuthorWorksResource>("author_asimov_works.json");

            works.Size.Should().Be(1455);
            works.Entries.Should().HaveCount(50);
            works.Links.Next.Should().Be("/authors/OL34221A/works.json?limit=50&offset=50");
        }

        [Test]
        public void author_with_more_works_than_one_page_should_get_all_of_them()
        {
            // Asimov-sized: 1455 works, a thousand to a page.
            Given<OpenLibraryAuthorWorksResource>(url =>
            {
                var offset = OffsetOf(url);

                return offset == 0
                    ? WorksPage(1455, 0, 1000, "/authors/OL34221A/works.json?limit=1000&offset=1000")
                    : WorksPage(1455, offset, 455, null);
            });

            var author = Subject.GetAuthorInfo("OL34221A");

            author.Books.Value.Should().HaveCount(1455);
            author.Books.Value.Select(b => b.ForeignBookId).Should().OnlyHaveUniqueItems();
            author.Books.Value.Select(b => b.ForeignBookId).Should().Contain("OL1454W");
            author.Metadata.Value.WorkCount.Should().Be(1455);

            WorksRequests.Should().HaveCount(2);
            OffsetOf(WorksRequests[1]).Should().Be(1000);
        }

        [Test]
        public void author_whose_works_fit_one_page_should_cost_one_request()
        {
            Given<OpenLibraryAuthorWorksResource>(url => WorksPage(193, 0, 193, null));

            Subject.GetAuthorInfo("OL34221A").Books.Value.Should().HaveCount(193);

            WorksRequests.Should().HaveCount(1);
        }

        // `size` counts records the list then leaves out -- Le Guin reported
        // 252 and returned 251. A count that is one short with no next page is
        // the whole list, not a reason to ask again.
        [Test]
        public void total_that_overcounts_should_not_trigger_another_request()
        {
            Given<OpenLibraryAuthorWorksResource>(url => WorksPage(252, 0, 251, null));

            Subject.GetAuthorInfo("OL34221A").Books.Value.Should().HaveCount(251);

            WorksRequests.Should().HaveCount(1);
        }

        [Test]
        public void paged_works_list_should_be_cached_as_one_list()
        {
            Given<OpenLibraryAuthorWorksResource>(url => OffsetOf(url) == 0
                ? WorksPage(1455, 0, 1000, "/authors/OL34221A/works.json?limit=1000&offset=1000")
                : WorksPage(1455, OffsetOf(url), 455, null));

            Subject.GetAuthorInfo("OL34221A");
            Subject.GetAuthorInfo("OL34221A").Books.Value.Should().HaveCount(1455);

            WorksRequests.Should().HaveCount(2, "the second call is served from the cache");
        }

        // A source that keeps saying "there is more" must not be followed
        // forever: every page is a rate-limited request on a command thread.
        [Test]
        public void works_list_that_never_ends_should_stop_at_the_page_cap_and_say_so()
        {
            Given<OpenLibraryAuthorWorksResource>(url =>
            {
                var offset = OffsetOf(url);

                return WorksPage(1000000, offset, 1000, $"/authors/OL34221A/works.json?limit=1000&offset={offset + 1000}");
            });

            var author = Subject.GetAuthorInfo("OL34221A");

            WorksRequests.Should().HaveCount(OpenLibraryProxy.MaxWorksPages);
            author.Books.Value.Should().HaveCount(OpenLibraryProxy.MaxWorksPages * 1000);

            // The books beyond the cap are missing from the library, which is
            // worth a warning rather than a line nobody reads.
            ExceptionVerification.ExpectedWarns(1);
        }

        // A next link that leads nowhere would otherwise be followed until the
        // cap.
        [Test]
        public void empty_page_should_end_the_works_list()
        {
            Given<OpenLibraryAuthorWorksResource>(url => OffsetOf(url) == 0
                ? WorksPage(1455, 0, 1000, "/authors/OL34221A/works.json?limit=1000&offset=1000")
                : WorksPage(1455, OffsetOf(url), 0, "/authors/OL34221A/works.json?limit=1000&offset=2000"));

            Subject.GetAuthorInfo("OL34221A").Books.Value.Should().HaveCount(1000);

            WorksRequests.Should().HaveCount(2);

            // Fewer works than the source said there were, and no way to get
            // the rest.
            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void captured_editions_list_should_expose_the_total()
        {
            var editions = OpenLibraryFixtureLoader.Load<OpenLibraryEditionListResource>("editions_pride_and_prejudice.json");

            editions.Size.Should().Be(4038);
            editions.Entries.Should().HaveCount(20);
            OpenLibraryProxy.IsTruncated(editions).Should().BeTrue();
        }

        [Test]
        public void editions_list_that_is_complete_should_not_be_reported_truncated()
        {
            OpenLibraryProxy.IsTruncated(new OpenLibraryEditionListResource
            {
                Size = 2,
                Entries = new List<OpenLibraryEditionResource> { new OpenLibraryEditionResource(), new OpenLibraryEditionResource() }
            }).Should().BeFalse();

            OpenLibraryProxy.IsTruncated(null).Should().BeFalse();
            OpenLibraryProxy.IsTruncated(new OpenLibraryEditionListResource()).Should().BeFalse();
        }

        // The edition list is capped on purpose and stays capped: one request,
        // the page it returned, and no warning -- most well-known books have
        // more editions than the cap, so this is logged at Info.
        [Test]
        public void truncated_editions_list_should_still_be_one_request_and_no_warning()
        {
            Given<OpenLibraryEditionListResource>(url => OpenLibraryFixtureLoader.LoadJson("editions_pride_and_prejudice.json"));

            var (_, book, _) = Subject.GetBookInfo("OL66554W");

            book.Editions.Value.Should().HaveCount(20);
            _requested.Count(u => u.Contains("/editions.json")).Should().Be(1);
        }
    }
}
