using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class ProxyCheckFixture : CoreTest<ProxyCheck>
    {
        private HttpRequest _request;

        [SetUp]
        public void Setup()
        {
            _request = null;

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            Mocker.GetMock<ILocalizationService>()
                  .Setup(s => s.GetLocalizedString(It.IsAny<string>()))
                  .Returns("Failed to test proxy: {0}");

            // A literal address, so nothing is resolved over the network.
            Mocker.GetMock<IConfigService>().SetupGet(s => s.ProxyHostname).Returns("127.0.0.1");
        }

        private void GivenProxyEnabled(bool enabled = true)
        {
            Mocker.GetMock<IConfigService>().SetupGet(s => s.ProxyEnabled).Returns(enabled);
        }

        private void GivenResponse(HttpStatusCode statusCode)
        {
            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                  .Callback<HttpRequest>(r => _request = r)
                  .Returns<HttpRequest>(r => new HttpResponse(r, new HttpHeader(), "{}", statusCode));
        }

        [Test]
        public void should_not_make_a_request_when_no_proxy_is_configured()
        {
            GivenProxyEnabled(false);

            Subject.Check().ShouldBeOk();

            Mocker.GetMock<IHttpClient>().Verify(s => s.Execute(It.IsAny<HttpRequest>()), Times.Never);
        }

        [Test]
        public void should_test_the_proxy_against_the_metadata_source()
        {
            GivenProxyEnabled();
            GivenResponse(HttpStatusCode.OK);

            Subject.Check().ShouldBeOk();

            _request.Url.Host.Should().Be("openlibrary.org");
        }

        [Test]
        public void should_return_error_when_the_proxy_answers_bad_request()
        {
            GivenProxyEnabled();
            GivenResponse(HttpStatusCode.BadRequest);

            Subject.Check().ShouldBeError();
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_ignore_other_error_codes()
        {
            GivenProxyEnabled();
            GivenResponse(HttpStatusCode.ServiceUnavailable);

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_return_error_when_the_request_fails()
        {
            GivenProxyEnabled();

            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                  .Throws(new WebException("Unable to connect to the remote server"));

            Subject.Check().ShouldBeError("openlibrary.org");
            ExceptionVerification.ExpectedErrors(1);
        }
    }
}
