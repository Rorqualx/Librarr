using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Http.Authentication;
using NzbDrone.Test.Common;
using Readarr.Http.Authentication;
using Readarr.Http.Extensions;

namespace NzbDrone.Api.Test.Authentication
{
    // Who counts as "local" for the "authentication disabled for local
    // addresses" setting. Each case runs a request through the real
    // forwarded-headers middleware, configured the way Startup configures it,
    // and then asks the same question UiAuthorizationHandler asks.
    [TestFixture]
    public class LocalRequestFixture : TestBase
    {
        private static HttpContext GivenRequest(string peer, string forwardedFor, bool trustCgnat = false)
        {
            var options = new ForwardedHeadersOptions();
            TrustedForwardedHeaders.Apply(options, trustCgnat);

            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);

            if (forwardedFor != null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            }

            var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
            middleware.Invoke(context).GetAwaiter().GetResult();

            return context;
        }

        [TestCase("127.0.0.1")]
        [TestCase("::1")]
        [TestCase("::ffff:127.0.0.1")]
        [TestCase("192.168.1.20")]
        [TestCase("10.0.0.5")]
        [TestCase("172.16.4.1")]
        public void direct_connection_from_local_address_should_be_local(string peer)
        {
            GivenRequest(peer, null).IsLocalRequest(false).Should().BeTrue();
        }

        [TestCase("8.8.8.8")]
        [TestCase("203.0.113.9")]
        [TestCase("100.64.0.1")]
        [TestCase("2001:4860:4860::8888")]
        public void direct_connection_from_public_address_should_not_be_local(string peer)
        {
            GivenRequest(peer, null).IsLocalRequest(false).Should().BeFalse();
        }

        // The audited defect: the header alone decided.
        [TestCase("8.8.8.8", "127.0.0.1")]
        [TestCase("8.8.8.8", "192.168.1.20")]
        [TestCase("8.8.8.8", "10.0.0.5")]
        [TestCase("8.8.8.8", "::1")]
        [TestCase("203.0.113.9", "192.168.1.20, 127.0.0.1")]
        [TestCase("100.64.0.1", "192.168.1.20")]
        public void public_peer_claiming_a_local_address_should_not_be_local(string peer, string forwardedFor)
        {
            var context = GivenRequest(peer, forwardedFor);

            context.IsLocalRequest(false).Should().BeFalse();
            context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(peer), "an untrusted peer does not get to rename itself");
        }

        [TestCase("127.0.0.1", "8.8.8.8")]
        [TestCase("::1", "8.8.8.8")]
        [TestCase("192.168.1.2", "203.0.113.9")]
        [TestCase("::ffff:10.0.0.2", "203.0.113.9")]
        public void remote_client_behind_local_proxy_should_not_be_local(string proxy, string forwardedFor)
        {
            var context = GivenRequest(proxy, forwardedFor);

            context.IsLocalRequest(false).Should().BeFalse();
            context.Connection.RemoteIpAddress.Should().Be(IPAddress.Parse(forwardedFor));
        }

        [TestCase("127.0.0.1", "192.168.1.20")]
        [TestCase("192.168.1.2", "10.0.0.5")]
        public void local_client_behind_local_proxy_should_be_local(string proxy, string forwardedFor)
        {
            GivenRequest(proxy, forwardedFor).IsLocalRequest(false).Should().BeTrue();
        }

        // The proxy appends the address it saw; whatever the client put in front
        // of that is the client's own claim and must not win.
        [TestCase("127.0.0.1", "192.168.1.20, 8.8.8.8")]
        [TestCase("127.0.0.1", "127.0.0.1, 8.8.8.8")]
        [TestCase("192.168.1.2", "10.0.0.5, 203.0.113.9")]
        public void remote_client_prepending_a_local_address_should_not_be_local(string proxy, string forwardedFor)
        {
            GivenRequest(proxy, forwardedFor).IsLocalRequest(false).Should().BeFalse();
        }

        [TestCase("127.0.0.1", "8.8.8.8, 10.0.0.2")]
        [TestCase("127.0.0.1", "203.0.113.9, 10.0.0.2, 192.168.1.2")]
        public void remote_client_behind_a_chain_of_local_proxies_should_not_be_local(string proxy, string forwardedFor)
        {
            GivenRequest(proxy, forwardedFor).IsLocalRequest(false).Should().BeFalse();
        }

        [TestCase("127.0.0.1", "192.168.1.20, 10.0.0.2")]
        public void local_client_behind_a_chain_of_local_proxies_should_be_local(string proxy, string forwardedFor)
        {
            GivenRequest(proxy, forwardedFor).IsLocalRequest(false).Should().BeTrue();
        }

        [TestCase("127.0.0.1", "unknown")]
        [TestCase("127.0.0.1", "not-an-address, 10.0.0.2")]
        [TestCase("192.168.1.2", "_hidden")]
        public void unresolvable_forwarded_client_should_not_be_local(string proxy, string forwardedFor)
        {
            GivenRequest(proxy, forwardedFor).IsLocalRequest(false).Should().BeFalse();
        }

        [Test]
        public void cgnat_address_should_only_be_local_when_trusted()
        {
            GivenRequest("100.64.0.1", null, trustCgnat: false).IsLocalRequest(false).Should().BeFalse();
            GivenRequest("100.64.0.1", null, trustCgnat: true).IsLocalRequest(true).Should().BeTrue();
        }

        [Test]
        public void cgnat_proxy_should_only_be_believed_when_trusted()
        {
            GivenRequest("100.64.0.1", "192.168.1.20", trustCgnat: false).IsLocalRequest(false).Should().BeFalse();
            GivenRequest("100.64.0.1", "192.168.1.20", trustCgnat: true).IsLocalRequest(true).Should().BeTrue();
            GivenRequest("100.64.0.1", "8.8.8.8", trustCgnat: true).IsLocalRequest(true).Should().BeFalse();
        }

        [Test]
        public void request_without_a_remote_address_should_not_be_local()
        {
            new DefaultHttpContext().IsLocalRequest(true).Should().BeFalse();
            ((HttpContext)null).IsLocalRequest(true).Should().BeFalse();
        }

        private bool IsAuthorized(HttpContext httpContext, AuthenticationRequiredType authenticationRequired)
        {
            Mocker.GetMock<IConfigFileProvider>().SetupGet(c => c.AuthenticationRequired).Returns(authenticationRequired);

            var requirement = new BypassableDenyAnonymousAuthorizationRequirement();
            var context = new AuthorizationHandlerContext(new[] { requirement }, new ClaimsPrincipal(new ClaimsIdentity()), httpContext);

            Mocker.Resolve<UiAuthorizationHandler>().HandleAsync(context).GetAwaiter().GetResult();

            return context.HasSucceeded;
        }

        [Test]
        public void handler_should_let_local_request_in_when_authentication_is_disabled_for_local_addresses()
        {
            IsAuthorized(GivenRequest("127.0.0.1", null), AuthenticationRequiredType.DisabledForLocalAddresses).Should().BeTrue();
        }

        [TestCase("8.8.8.8", "127.0.0.1")]
        [TestCase("8.8.8.8", "192.168.1.20")]
        [TestCase("127.0.0.1", "8.8.8.8")]
        [TestCase("127.0.0.1", "192.168.1.20, 8.8.8.8")]
        public void handler_should_not_let_remote_request_in(string peer, string forwardedFor)
        {
            IsAuthorized(GivenRequest(peer, forwardedFor), AuthenticationRequiredType.DisabledForLocalAddresses).Should().BeFalse();
        }

        [Test]
        public void handler_should_not_let_local_request_in_when_authentication_is_always_required()
        {
            IsAuthorized(GivenRequest("127.0.0.1", null), AuthenticationRequiredType.Enabled).Should().BeFalse();
        }
    }
}
