using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Readarr.Http.Authentication;

namespace NzbDrone.Api.Test.Authentication
{
    [TestFixture]
    public class ApiKeyAuthenticationHandlerFixture : TestBase
    {
        private const string ApiKey = "0123456789abcdef0123456789abcdef";

        [TestCase(ApiKey, ApiKey, true)]
        [TestCase(ApiKey, "0123456789abcdef0123456789abcdeF", false)]
        [TestCase(ApiKey, "1123456789abcdef0123456789abcdef", false)]
        [TestCase(ApiKey, "0123456789abcdef", false)]
        [TestCase(ApiKey, "0123456789abcdef0123456789abcdef0", false)]
        [TestCase(ApiKey, "", false)]
        [TestCase(ApiKey, null, false)]
        [TestCase("", "", false)]
        [TestCase(null, null, false)]
        [TestCase(null, "", false)]
        public void should_only_match_the_exact_key(string expected, string provided, bool matches)
        {
            ApiKeyAuthenticationHandler.ApiKeyMatches(expected, provided).Should().Be(matches);
        }

        private AuthenticateResult Authenticate(string headerValue)
        {
            Mocker.GetMock<IConfigFileProvider>().SetupGet(c => c.ApiKey).Returns(ApiKey);

            var options = new Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
            options.Setup(o => o.Get(It.IsAny<string>())).Returns(new ApiKeyAuthenticationOptions
            {
                HeaderName = "X-Api-Key",
                QueryName = "apikey"
            });

            var handler = new ApiKeyAuthenticationHandler(options.Object, NullLoggerFactory.Instance, UrlEncoder.Default, Mocker.GetMock<IConfigFileProvider>().Object);

            var context = new DefaultHttpContext();
            context.Request.Headers["X-Api-Key"] = headerValue;

            handler.InitializeAsync(new AuthenticationScheme(ApiKeyAuthenticationOptions.DefaultScheme, null, typeof(ApiKeyAuthenticationHandler)), context)
                   .GetAwaiter()
                   .GetResult();

            return handler.AuthenticateAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void should_authenticate_request_with_the_right_key()
        {
            Authenticate(ApiKey).Succeeded.Should().BeTrue();
        }

        [TestCase("0123456789abcdef0123456789abcdeF")]
        [TestCase("0123456789abcdef")]
        [TestCase("0123456789abcdef0123456789abcdef0")]
        public void should_not_authenticate_request_with_the_wrong_key(string provided)
        {
            Authenticate(provided).Succeeded.Should().BeFalse();
        }
    }
}
