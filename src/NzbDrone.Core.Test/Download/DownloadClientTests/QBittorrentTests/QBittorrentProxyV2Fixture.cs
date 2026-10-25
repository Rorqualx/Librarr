using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.DownloadClientTests.QBittorrentTests
{
    // Issue #101: qBittorrent 5.2+ answers any Basic Authorization header with
    // 401 and may return an empty body from a successful login. It also offers
    // API keys, sent as a Bearer token.
    [TestFixture]
    public class QBittorrentProxyV2Fixture : CoreTest<QBittorrentProxyV2>
    {
        private Mock<IHttpClient> _httpClient;
        private List<HttpRequest> _requests;
        private Func<HttpRequest, HttpResponse> _respond;

        [SetUp]
        public void Setup()
        {
            _requests = new List<HttpRequest>();
            _httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            _httpClient.Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                       .Returns<HttpRequest>(r =>
                       {
                           _requests.Add(r);
                           return _respond(r);
                       });

            Mocker.SetConstant<IHttpClient>(_httpClient.Object);
            Mocker.SetConstant<ICacheManager>(new CacheManager());

            _respond = r => Respond(r, r.Url.Path.EndsWith("/auth/login") ? "Ok." : "v5.2.4", loginCookie: true);
        }

        private static QBittorrentSettings GivenSettings(string username = null, string password = null, string apiKey = null)
        {
            return new QBittorrentSettings
            {
                Host = "127.0.0.1",
                Port = 8080,
                Username = username,
                Password = password,
                ApiKey = apiKey
            };
        }

        private static HttpResponse Respond(HttpRequest request, string content, HttpStatusCode status = HttpStatusCode.OK, bool loginCookie = false)
        {
            var headers = new HttpHeader();

            if (loginCookie && request.Url.Path.EndsWith("/auth/login"))
            {
                headers.Add("Set-Cookie", "SID=abc123; HttpOnly; path=/");
            }

            var response = new HttpResponse(request, headers, content, status);

            if (status != HttpStatusCode.OK && !request.SuppressHttpError &&
                (request.SuppressHttpErrorStatusCodes == null || !request.SuppressHttpErrorStatusCodes.Contains(status)))
            {
                throw new HttpException(request, response);
            }

            return response;
        }

        private IEnumerable<HttpRequest> ApiRequests => _requests.Where(r => !r.Url.Path.EndsWith("/auth/login"));

        [Test]
        public void should_not_send_a_basic_credential()
        {
            Subject.GetVersion(GivenSettings("admin", "secret"));

            _requests.Should().NotBeEmpty();
            _requests.Should().OnlyContain(r => r.Credentials == null && r.Headers["Authorization"] == null);
        }

        [Test]
        public void should_not_store_the_session_cookie_in_the_shared_cookie_jar()
        {
            Subject.GetVersion(GivenSettings("admin", "secret"));

            ApiRequests.Should().NotBeEmpty();
            _requests.Should().OnlyContain(r => !r.StoreRequestCookie);
        }

        [Test]
        public void should_not_send_a_credential_when_none_is_configured()
        {
            Subject.GetVersion(GivenSettings());

            _requests.Should().ContainSingle()
                     .Which.Should().Match<HttpRequest>(r => r.Credentials == null && r.Headers["Authorization"] == null);
        }

        [Test]
        public void should_accept_an_empty_login_response()
        {
            _respond = r => Respond(r, r.Url.Path.EndsWith("/auth/login") ? string.Empty : "v5.2.4", loginCookie: true);

            Subject.GetVersion(GivenSettings("admin", "secret")).Should().Be("5.2.4");

            ApiRequests.Should().OnlyContain(r => r.Cookies["SID"] == "abc123");
        }

        [Test]
        public void should_reject_a_failed_login()
        {
            _respond = r => Respond(r, r.Url.Path.EndsWith("/auth/login") ? "Fails." : "v5.2.4");

            Assert.Throws<DownloadClientAuthenticationException>(() => Subject.GetVersion(GivenSettings("admin", "wrong")));
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void should_report_an_authentication_failure_when_login_is_refused(HttpStatusCode status)
        {
            _respond = r => Respond(r, string.Empty, r.Url.Path.EndsWith("/auth/login") ? status : HttpStatusCode.OK);

            Assert.Throws<DownloadClientAuthenticationException>(() => Subject.GetVersion(GivenSettings("admin", "wrong")));
        }

        [Test]
        public void should_send_api_key_as_bearer_token_without_logging_in()
        {
            Subject.GetVersion(GivenSettings(apiKey: "qbt_key")).Should().Be("5.2.4");

            _requests.Should().ContainSingle()
                     .Which.Headers["Authorization"].Should().Be("Bearer qbt_key");
        }

        [TestCase(HttpStatusCode.Unauthorized)]
        [TestCase(HttpStatusCode.Forbidden)]
        public void should_report_an_authentication_failure_for_a_rejected_api_key(HttpStatusCode status)
        {
            _respond = r => Respond(r, string.Empty, status);

            Assert.Throws<DownloadClientAuthenticationException>(() => Subject.GetVersion(GivenSettings(apiKey: "qbt_bad")));
        }

        [Test]
        public void should_detect_v2_api_on_qbittorrent_5_2_without_credentials()
        {
            // 5.2 answers an unauthenticated version probe with 403.
            _respond = r => Respond(r, string.Empty, HttpStatusCode.Forbidden);

            Subject.IsApiSupported(GivenSettings("admin", "secret")).Should().BeTrue();
            _requests.Should().OnlyContain(r => r.Credentials == null);
        }

        [Test]
        public void should_require_empty_username_and_password_with_api_key()
        {
            var result = GivenSettings("admin", "secret", "qbt_key").Validate();

            result.IsValid.Should().BeFalse();
            result.Errors.Select(e => e.PropertyName).Should().Contain(new[] { "Username", "Password" });
        }

        [Test]
        public void should_accept_api_key_alone()
        {
            GivenSettings(apiKey: "qbt_key").Validate().IsValid.Should().BeTrue();
        }
    }
}
