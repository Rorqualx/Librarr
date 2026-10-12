using System.Linq;
using System.Net;
using DryIoc;
using DryIoc.Microsoft.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Readarr.Http.Authentication;

namespace NzbDrone.App.Test
{
    // The forwarded-headers options are filled in from IConfigFileProvider, which
    // lives in the DryIoc container rather than the service collection. Build
    // the provider the way the host does and check the options come out right.
    [TestFixture]
    public class ForwardedHeadersOptionsFixture : TestBase
    {
        private ForwardedHeadersOptions GivenOptions(bool trustCgnatIpAddresses)
        {
            var config = new Mock<IConfigFileProvider>();
            config.SetupGet(c => c.TrustCgnatIpAddresses).Returns(trustCgnatIpAddresses);

            var services = new ServiceCollection();
            services.AddTrustedForwardedHeaders();

            var factory = new DryIocServiceProviderFactory(new Container(rules => rules.WithNzbDroneRules()));
            var container = factory.CreateBuilder(services);
            container.RegisterInstance(config.Object);

            return factory.CreateServiceProvider(container).GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        }

        private static bool IsTrustedProxy(ForwardedHeadersOptions options, string address)
        {
            var ip = IPAddress.Parse(address);

            return options.KnownProxies.Contains(ip) || options.KnownIPNetworks.Any(n => n.Contains(ip));
        }

        [TestCase("127.0.0.1")]
        [TestCase("127.4.5.6")]
        [TestCase("::1")]
        [TestCase("10.20.30.40")]
        [TestCase("172.16.0.1")]
        [TestCase("172.31.255.254")]
        [TestCase("192.168.1.2")]
        [TestCase("169.254.10.10")]
        [TestCase("fd12:3456:789a::1")]
        [TestCase("fe80::1")]
        public void should_trust_forwarded_headers_from_local_peer(string address)
        {
            IsTrustedProxy(GivenOptions(false), address).Should().BeTrue();
        }

        [TestCase("8.8.8.8")]
        [TestCase("203.0.113.9")]
        [TestCase("172.32.0.1")]
        [TestCase("192.169.0.1")]
        [TestCase("11.0.0.1")]
        [TestCase("100.64.0.1")]
        [TestCase("2001:4860:4860::8888")]
        public void should_not_trust_forwarded_headers_from_any_other_peer(string address)
        {
            IsTrustedProxy(GivenOptions(false), address).Should().BeFalse();
        }

        [Test]
        public void should_trust_cgnat_peer_only_when_configured()
        {
            IsTrustedProxy(GivenOptions(false), "100.100.100.100").Should().BeFalse();
            IsTrustedProxy(GivenOptions(true), "100.100.100.100").Should().BeTrue();
            IsTrustedProxy(GivenOptions(true), "100.128.0.1").Should().BeFalse();
        }

        [Test]
        public void should_not_be_left_trusting_everyone()
        {
            var options = GivenOptions(false);

            // Empty lists are what make the middleware believe any peer.
            options.KnownIPNetworks.Should().NotBeEmpty();
            options.ForwardLimit.Should().BeNull();
        }
    }
}
