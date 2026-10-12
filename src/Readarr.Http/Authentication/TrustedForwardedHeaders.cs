using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using NzbDrone.Core.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace Readarr.Http.Authentication
{
    public static class TrustedForwardedHeaders
    {
        private const string CgnatNetwork = "100.64.0.0/10";

        // X-Forwarded-* are plain request headers: anyone can send them. They are
        // only believed when the peer that handed us the request is a proxy we
        // trust, which by default means one on this host or on a private network.
        // That covers a reverse proxy on the same machine or LAN with no
        // configuration, and means a client connecting directly from the internet
        // cannot claim to be somebody else.
        private static readonly string[] LocalNetworks =
        {
            "127.0.0.0/8",
            "10.0.0.0/8",
            "172.16.0.0/12",
            "192.168.0.0/16",
            "169.254.0.0/16",
            "::1/128",
            "fc00::/7",
            "fe80::/10",
            "fec0::/10"
        };

        public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services)
        {
            services.AddOptions<ForwardedHeadersOptions>()
                    .Configure<IConfigFileProvider>((options, config) => Apply(options, config.TrustCgnatIpAddresses));

            return services;
        }

        public static void Apply(ForwardedHeadersOptions options, bool trustCgnatIpAddresses)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            // Walk the whole chain rather than one hop. The walk stops at the
            // first address that is not itself a trusted proxy, so with several
            // proxies in a row the address we end up with is the first one that
            // did not come from a trusted hop's say-so.
            options.ForwardLimit = null;

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (var network in LocalNetworks)
            {
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }

            // Same switch that lets these addresses skip authentication: if the
            // operator treats the CGNAT range (Tailscale and friends) as local, a
            // proxy living there is local too.
            if (trustCgnatIpAddresses)
            {
                options.KnownIPNetworks.Add(IPNetwork.Parse(CgnatNetwork));
            }
        }
    }
}
