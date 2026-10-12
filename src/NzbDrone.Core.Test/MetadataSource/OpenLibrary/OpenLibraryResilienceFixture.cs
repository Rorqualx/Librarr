using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // How the proxy behaves when OpenLibrary is not simply answering 200:
    // hanging, asking us to go away for an hour, not having the record, or
    // having replaced it with a redirect or a tombstone. Each of these was a
    // path where the proxy either gave up too early, waited far too long, or
    // threw something the callers were not written to expect.
    [TestFixture]
    public class OpenLibraryResilienceFixture : CoreTest<TestOpenLibraryProxy>
    {
        private const string AuthorJson = @"{
            ""key"": ""/authors/OL2A"",
            ""name"": ""Ursula K. Le Guin"",
            ""type"": { ""key"": ""/type/author"" }
        }";

        private const string AuthorRedirectJson = @"{
            ""key"": ""/authors/OL1A"",
            ""location"": ""/authors/OL2A"",
            ""type"": { ""key"": ""/type/redirect"" }
        }";

        private const string AuthorDeleteJson = @"{
            ""key"": ""/authors/OL1A"",
            ""type"": { ""key"": ""/type/delete"" }
        }";

        private const string WorksJson = @"{
            ""size"": 1,
            ""entries"": [{ ""key"": ""/works/OL45804W"", ""title"": ""Fantastic Mr Fox"", ""type"": { ""key"": ""/type/work"" } }]
        }";

        private const string WorkJson = @"{
            ""key"": ""/works/OL45804W"",
            ""title"": ""Fantastic Mr Fox"",
            ""type"": { ""key"": ""/type/work"" },
            ""authors"": [{ ""author"": { ""key"": ""/authors/OL2A"" } }]
        }";

        // The shape captured in Files/OpenLibrary/work_redirect_record.json.
        private const string WorkRedirectJson = @"{
            ""location"": ""/works/OL45804W"",
            ""key"": ""/works/OL45883W"",
            ""type"": { ""key"": ""/type/redirect"" }
        }";

        private const string WorkDeleteJson = @"{
            ""key"": ""/works/OL45883W"",
            ""type"": { ""key"": ""/type/delete"" }
        }";

        private const string EditionsJson = @"{ ""size"": 0, ""entries"": [] }";

        private List<string> _requested;

        [SetUp]
        public void Setup()
        {
            _requested = new List<string>();

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());
        }

        private static HttpResponse Response(HttpRequest request, string json, HttpStatusCode status = HttpStatusCode.OK, string retryAfter = null)
        {
            var headers = new HttpHeader();

            if (retryAfter != null)
            {
                headers.Add("Retry-After", retryAfter);
            }

            return new HttpResponse(request, headers, Encoding.UTF8.GetBytes(json ?? string.Empty), status);
        }

        // Mirrors what the real HttpClient does with an error status: it throws
        // rather than returning, and a 429 gets its own exception type.
        private void Given<T>(Func<HttpRequest, HttpResponse> behaviour)
            where T : new()
        {
            Mocker.GetMock<IHttpClient>()
                .Setup(c => c.Get<T>(It.IsAny<HttpRequest>()))
                .Returns((HttpRequest r) =>
                {
                    _requested.Add(r.Url.ToString());

                    var response = behaviour(r);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        throw new TooManyRequestsException(r, response);
                    }

                    if (response.HasHttpError)
                    {
                        throw new HttpException(r, response);
                    }

                    return new HttpResponse<T>(response);
                });
        }

        private int Requests(string fragment) => _requested.Count(u => u.Contains(fragment));

        private static WebException Timeout()
        {
            // Exactly what ManagedHttpDispatcher raises when the request's
            // cancellation token fires.
            return new WebException("Http request timed out", null, WebExceptionStatus.Timeout, null);
        }

        private void GivenWorksAndEditions()
        {
            Given<OpenLibraryAuthorWorksResource>(r => Response(r, WorksJson));
            Given<OpenLibraryEditionListResource>(r => Response(r, EditionsJson));
        }

        [Test]
        public void should_retry_a_request_that_timed_out()
        {
            var attempts = 0;

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r =>
            {
                if (attempts++ == 0)
                {
                    throw Timeout();
                }

                return Response(r, AuthorJson);
            });

            var author = Subject.GetAuthorInfo("OL2A");

            author.Metadata.Value.Name.Should().Be("Ursula K. Le Guin");
            Subject.Delays.Should().HaveCount(1, "one timeout, one back-off");

            ExceptionVerification.ExpectedWarns(1);
        }

        // A timeout is the opposite of "the source answered". Recording it as
        // contact resets the breaker's failure count and clears an open
        // cooldown, so a server that has stopped responding looks healthy.
        [Test]
        public void should_report_an_exhausted_timeout_to_the_breaker_as_a_failure()
        {
            Given<OpenLibraryAuthorResource>(r => throw Timeout());

            Assert.Throws<WebException>(() => Subject.GetAuthorInfo("OL2A"));

            Requests("authors/OL2A.json").Should().Be(4, "the first attempt plus three retries");

            Mocker.GetMock<IMetadataSourceStatusService>()
                .Verify(s => s.RecordFailure(It.Is<Exception>(e => MetadataSourceStatusService.IndicatesUnavailable(e))), Times.Once());
            Mocker.GetMock<IMetadataSourceStatusService>()
                .Verify(s => s.RecordReachable(), Times.Never());

            ExceptionVerification.ExpectedWarns(3);
        }

        [Test]
        public void should_honour_a_short_retry_after()
        {
            var attempts = 0;

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => attempts++ == 0
                ? Response(r, null, HttpStatusCode.TooManyRequests, "5")
                : Response(r, AuthorJson));

            Subject.GetAuthorInfo("OL2A").Should().NotBeNull();

            Subject.Delays.Should().Equal(TimeSpan.FromSeconds(5));

            ExceptionVerification.ExpectedWarns(1);
        }

        // "Come back in an hour" is not something to sit through on a command
        // thread or inside a UI search request -- three times over. Give up at
        // once and let the breaker count it.
        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void should_not_wait_out_a_retry_after_longer_than_the_cap(HttpStatusCode status)
        {
            Given<OpenLibraryAuthorResource>(r => Response(r, null, status, "3600"));

            var thrown = Assert.Throws(Is.InstanceOf<HttpException>(), () => Subject.GetAuthorInfo("OL2A"));

            ((HttpException)thrown).Response.StatusCode.Should().Be(status);
            Subject.Delays.Should().BeEmpty("the server asked for longer than we are prepared to block");
            Requests("authors/OL2A.json").Should().Be(1, "retrying before the server said to is what extends a ban");

            Mocker.GetMock<IMetadataSourceStatusService>()
                .Verify(s => s.RecordFailure(It.Is<Exception>(e => MetadataSourceStatusService.IndicatesUnavailable(e))), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        // Retry-After is allowed to be an HTTP date instead of a number of
        // seconds. Ignoring that form would fall back to the 2s schedule and
        // retry three times into a server that named a time.
        [Test]
        public void should_apply_the_cap_to_a_retry_after_given_as_a_date()
        {
            var when = DateTime.UtcNow.AddHours(1).ToString("r");

            Given<OpenLibraryAuthorResource>(r => Response(r, null, HttpStatusCode.TooManyRequests, when));

            Assert.Throws<TooManyRequestsException>(() => Subject.GetAuthorInfo("OL2A"));

            Subject.Delays.Should().BeEmpty();
            Requests("authors/OL2A.json").Should().Be(1);

            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase("0")]
        [TestCase("-30")]
        [TestCase("soon")]
        public void should_fall_back_to_the_backoff_schedule_for_an_unusable_retry_after(string retryAfter)
        {
            var attempts = 0;

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => attempts++ == 0
                ? Response(r, null, HttpStatusCode.TooManyRequests, retryAfter)
                : Response(r, AuthorJson));

            Subject.GetAuthorInfo("OL2A").Should().NotBeNull();

            Subject.Delays.Should().Equal(TimeSpan.FromSeconds(2));

            ExceptionVerification.ExpectedWarns(1);
        }

        // `work:`, `edition:` and `author:` already answer an unknown id with
        // an empty list. An ISBN OpenLibrary does not hold is by far the most
        // common of the four and was the one that threw.
        [Test]
        public void should_return_no_results_for_an_isbn_openlibrary_does_not_have()
        {
            Given<OpenLibraryEditionResource>(r => Response(r, null, HttpStatusCode.NotFound));

            Subject.SearchByIsbn("9780000000002").Should().BeEmpty();
            Subject.SearchForNewEntity("isbn:9780000000002").Should().BeEmpty();
        }

        // Not-found must not become the cached answer: a record added to
        // OpenLibrary tomorrow should be findable tomorrow, not in 30 days.
        [Test]
        public void should_not_cache_an_isbn_miss()
        {
            Given<OpenLibraryEditionResource>(r => Response(r, null, HttpStatusCode.NotFound));

            Subject.SearchByIsbn("9780000000002");
            Subject.SearchByIsbn("9780000000002");

            Requests("isbn/9780000000002.json").Should().Be(2);
        }

        // The narrowness matters as much as the catch. A rate-limited lookup
        // that reads as "no such ISBN" would have Reidentify write the book
        // off as unmatched instead of retrying it.
        [Test]
        public void should_still_throw_from_an_isbn_search_that_was_refused()
        {
            Given<OpenLibraryEditionResource>(r => Response(r, null, HttpStatusCode.TooManyRequests, "3600"));

            Assert.Throws<TooManyRequestsException>(() => Subject.SearchByIsbn("9780000000002"));

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_follow_an_author_redirect_document_to_the_surviving_record()
        {
            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, r.Url.ToString().Contains("OL1A") ? AuthorRedirectJson : AuthorJson));

            var author = Subject.GetAuthorInfo("OL1A");

            author.Metadata.Value.ForeignAuthorId.Should().Be("OL2A");
            author.Metadata.Value.Name.Should().Be("Ursula K. Le Guin");

            // The merged-away record has no works of its own; asking it for
            // them would return an author with an empty discography.
            Requests("authors/OL2A/works.json").Should().Be(1);
            Requests("authors/OL1A/works.json").Should().Be(0);
            author.Books.Value.Should().HaveCount(1);
        }

        [Test]
        public void should_accept_the_document_type_as_a_plain_string()
        {
            const string StringTyped = @"{ ""key"": ""/authors/OL1A"", ""location"": ""/authors/OL2A"", ""type"": ""/type/redirect"" }";

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, r.Url.ToString().Contains("OL1A") ? StringTyped : AuthorJson));

            Subject.GetAuthorInfo("OL1A").Metadata.Value.ForeignAuthorId.Should().Be("OL2A");
        }

        // One hop only. Two records redirecting at each other would otherwise
        // spend the rate limit going round until something else gave out.
        [Test]
        public void should_not_chase_a_redirect_loop()
        {
            const string BackAgain = @"{ ""key"": ""/authors/OL2A"", ""location"": ""/authors/OL1A"", ""type"": { ""key"": ""/type/redirect"" } }";

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, r.Url.ToString().Contains("OL1A") ? AuthorRedirectJson : BackAgain));

            Assert.Throws<OpenLibraryException>(() => Subject.GetAuthorInfo("OL1A"));

            _requested.Count(u => u.EndsWith("A.json")).Should().Be(2, "the original and exactly one hop");

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_follow_a_redirect_that_points_at_itself()
        {
            const string Self = @"{ ""key"": ""/authors/OL1A"", ""location"": ""/authors/OL1A"", ""type"": { ""key"": ""/type/redirect"" } }";

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, Self));

            Assert.Throws<OpenLibraryException>(() => Subject.GetAuthorInfo("OL1A"));

            Requests("authors/OL1A.json").Should().Be(1);

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_treat_a_deleted_author_document_as_not_found()
        {
            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, AuthorDeleteJson));

            Assert.Throws<OpenLibraryException>(() => Subject.GetAuthorInfo("OL1A"));

            // And through the search route, where not-found means an empty list.
            Subject.SearchForNewAuthor("OL1A").Should().BeEmpty();
        }

        // `location` is also an ordinary field on a live author record (where
        // they lived). Only a redirect document's location is a pointer.
        [Test]
        public void should_not_mistake_an_authors_home_town_for_a_redirect()
        {
            const string Located = @"{ ""key"": ""/authors/OL2A"", ""name"": ""Ursula K. Le Guin"", ""location"": ""Portland, Oregon"", ""type"": { ""key"": ""/type/author"" } }";

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, Located));

            Subject.GetAuthorInfo("OL2A").Metadata.Value.Name.Should().Be("Ursula K. Le Guin");
            Requests("authors/OL2A.json").Should().Be(1);
        }

        [Test]
        public void should_resolve_a_display_name_through_a_redirect()
        {
            Given<OpenLibraryAuthorResource>(r => Response(r, r.Url.ToString().Contains("OL1A") ? AuthorRedirectJson : AuthorJson));

            Subject.GetAuthorName("OL1A").Should().Be("Ursula K. Le Guin");
        }

        [Test]
        public void should_follow_a_work_redirect_document_to_the_surviving_record()
        {
            GivenWorksAndEditions();
            Given<OpenLibraryWorkResource>(r => Response(r, r.Url.ToString().Contains("OL45883W") ? WorkRedirectJson : WorkJson));

            var (authorId, book, _) = Subject.GetBookInfo("OL45883W");

            book.ForeignBookId.Should().Be("OL45804W");
            book.Title.Should().Be("Fantastic Mr Fox");
            authorId.Should().Be("OL2A");

            Requests("works/OL45804W/editions.json").Should().Be(1);
            Requests("works/OL45883W/editions.json").Should().Be(0);
        }

        [Test]
        public void should_treat_a_deleted_work_document_as_not_found()
        {
            GivenWorksAndEditions();
            Given<OpenLibraryWorkResource>(r => Response(r, WorkDeleteJson));

            Assert.Throws<OpenLibraryException>(() => Subject.GetBookInfo("OL45883W"));

            Subject.SearchByForeignBookId("OL45883W", true).Should().BeEmpty();
        }

        [Test]
        public void should_read_the_author_key_through_a_work_redirect()
        {
            Given<OpenLibraryWorkResource>(r => Response(r, r.Url.ToString().Contains("OL45883W") ? WorkRedirectJson : WorkJson));

            Subject.GetAuthorIdFromWork("OL45883W").Should().Be("OL2A");
        }

        [Test]
        public void should_serve_a_work_from_the_cache_by_default()
        {
            GivenWorksAndEditions();
            Given<OpenLibraryWorkResource>(r => Response(r, WorkJson));

            Subject.GetBookInfo("OL45804W");
            Subject.GetBookInfo("OL45804W");

            Requests("works/OL45804W.json").Should().Be(1);
        }

        // A user who has just corrected a record on OpenLibrary and clicked
        // Refresh should see the correction, not the copy from six days ago.
        [Test]
        public void should_refetch_a_work_when_asked_to_bypass_the_cache()
        {
            var title = "Fantastic Mr Fox";

            GivenWorksAndEditions();
            Given<OpenLibraryWorkResource>(r => Response(r, WorkJson.Replace("Fantastic Mr Fox", title)));

            Subject.GetBookInfo("OL45804W").Item2.Title.Should().Be("Fantastic Mr Fox");

            title = "Fantastic Mr. Fox";

            Subject.GetBookInfo("OL45804W", useCache: false).Item2.Title.Should().Be("Fantastic Mr. Fox");
            Requests("works/OL45804W.json").Should().Be(2);
        }

        // Bypassing has to replace the cached copy, not just skip it. Otherwise
        // the refresh shows the new data and the very next cached reader -- the
        // cover picker, an import -- goes back to showing the old.
        [Test]
        public void a_bypassing_fetch_should_replace_the_cached_work()
        {
            var title = "Fantastic Mr Fox";

            GivenWorksAndEditions();
            Given<OpenLibraryWorkResource>(r => Response(r, WorkJson.Replace("Fantastic Mr Fox", title)));

            Subject.GetBookInfo("OL45804W");

            title = "Fantastic Mr. Fox";
            Subject.GetBookInfo("OL45804W", useCache: false);

            Subject.GetBookInfo("OL45804W").Item2.Title.Should().Be("Fantastic Mr. Fox");
            Requests("works/OL45804W.json").Should().Be(2, "the third call is a cache hit on the refreshed copy");
        }

        [Test]
        public void a_bypassing_fetch_should_replace_the_cached_author()
        {
            var name = "Ursula K. Le Guin";

            GivenWorksAndEditions();
            Given<OpenLibraryAuthorResource>(r => Response(r, AuthorJson.Replace("Ursula K. Le Guin", name)));

            Subject.GetAuthorInfo("OL2A");

            name = "Ursula K. LeGuin";
            Subject.GetAuthorInfo("OL2A", useCache: false).Metadata.Value.Name.Should().Be("Ursula K. LeGuin");

            Subject.GetAuthorInfo("OL2A").Metadata.Value.Name.Should().Be("Ursula K. LeGuin");
            Requests("authors/OL2A.json").Should().Be(2);
        }
    }

    // Test seam over the proxy's back-off. The production class sleeps; the
    // schedule is what is under test, and asserting it for real costs up to
    // fourteen seconds a case.
    public class TestOpenLibraryProxy : OpenLibraryProxy
    {
        public TestOpenLibraryProxy(IHttpClient httpClient,
                                    IOpenLibraryRequestBuilder requestBuilder,
                                    IMetadataSourceStatusService statusService,
                                    NLog.Logger logger)
            : base(httpClient, requestBuilder, statusService, logger)
        {
        }

        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

        protected override void Delay(TimeSpan delay)
        {
            Delays.Add(delay);
        }
    }
}
