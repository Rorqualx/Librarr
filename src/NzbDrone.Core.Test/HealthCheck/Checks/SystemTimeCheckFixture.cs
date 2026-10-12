using System;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class SystemTimeCheckFixture : CoreTest<SystemTimeCheck>
    {
        private HttpRequest _request;

        [SetUp]
        public void Setup()
        {
            _request = null;

            Mocker.SetConstant<IOpenLibraryRequestBuilder>(new OpenLibraryRequestBuilder());

            Mocker.GetMock<ILocalizationService>()
                  .Setup(s => s.GetLocalizedString(It.IsAny<string>()))
                  .Returns("System time is off by more than 1 day. Scheduled tasks may not run correctly until the time is corrected");
        }

        private void GivenResponse(string dateHeader, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            var headers = new HttpHeader();

            if (dateHeader != null)
            {
                headers.Set("Date", dateHeader);
            }

            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                  .Callback<HttpRequest>(r => _request = r)
                  .Returns<HttpRequest>(r => new HttpResponse(r, headers, "{}", statusCode));
        }

        private void GivenServerTime(DateTime dateTime, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            // RFC 1123, as an HTTP Date header is written.
            GivenResponse(dateTime.ToString("r"), statusCode);
        }

        [Test]
        public void should_not_return_error_when_system_time_is_close_to_server_time()
        {
            GivenServerTime(DateTime.UtcNow);

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_return_error_when_system_time_is_more_than_one_day_from_server_time()
        {
            GivenServerTime(DateTime.UtcNow.AddDays(2));

            Subject.Check().ShouldBeError();
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_return_error_when_system_time_is_more_than_one_day_ahead_of_server_time()
        {
            GivenServerTime(DateTime.UtcNow.AddDays(-2));

            Subject.Check().ShouldBeError();
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_read_the_date_of_an_error_response_too()
        {
            GivenServerTime(DateTime.UtcNow.AddDays(2), HttpStatusCode.ServiceUnavailable);

            Subject.Check().ShouldBeError();
            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_ask_the_metadata_source_and_not_the_upstream_project()
        {
            GivenServerTime(DateTime.UtcNow);

            Subject.Check();

            _request.Url.Host.Should().Be("openlibrary.org");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not a date")]
        public void should_be_ok_when_the_response_has_no_usable_date(string dateHeader)
        {
            GivenResponse(dateHeader);

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_be_ok_when_the_request_fails()
        {
            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                  .Throws(new WebException("Name or service not known"));

            Subject.Check().ShouldBeOk();
        }
    }
}
