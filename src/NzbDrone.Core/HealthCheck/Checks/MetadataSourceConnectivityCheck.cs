using System;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource.Events;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Health check for the active metadata source. Ships in Phase 4 alongside
    // the OpenLibraryProxy hardening. Will start gating when Phase 5's
    // MetadataSourceFactory makes MetadataSourceType = "OpenLibrary"
    // an active runtime setting.
    [CheckOn(typeof(ApplicationStartedEvent))]
    [CheckOn(typeof(ConfigSavedEvent))]

    // Also re-probe the moment the circuit breaker trips. Until this, the check
    // only ran at startup and on a config save, so an outage that began during
    // a long import was invisible on the health page for exactly as long as it
    // mattered. The probe goes direct through IHttpClient rather than the proxy,
    // so an open breaker does not suppress the very request that would confirm
    // it.
    [CheckOn(typeof(MetadataSourceUnavailableEvent))]
    public class MetadataSourceConnectivityCheck : HealthCheckBase
    {
        private readonly IConfigService _configService;
        private readonly IHttpClient _httpClient;
        private readonly IOpenLibraryRequestBuilder _requestBuilder;
        private readonly Logger _logger;

        public MetadataSourceConnectivityCheck(IConfigService configService,
                                               IHttpClient httpClient,
                                               IOpenLibraryRequestBuilder requestBuilder,
                                               ILocalizationService localizationService,
                                               Logger logger)
            : base(localizationService)
        {
            _configService = configService;
            _httpClient = httpClient;
            _requestBuilder = requestBuilder;
            _logger = logger;
        }

        public override HealthCheck Check()
        {
            // OL connectivity is only load-bearing when the user has flipped
            // to OpenLibrary as the active source. Skip the network probe
            // otherwise so we don't fail the health page for BookInfo users.
            if (!_configService.MetadataSourceType.EqualsIgnoreCase("OpenLibrary"))
            {
                return new HealthCheck(GetType());
            }

            // Probe a tiny stable resource (OL author "Mark Twain") rather
            // than the root, so we test the JSON content path and not just
            // the marketing redirect.
            var request = _requestBuilder.For(OpenLibraryRequestBuilder.ProbePath).Build();
            request.SuppressHttpError = true;

            try
            {
                var response = _httpClient.Execute(request);

                if (response.HasHttpError)
                {
                    _logger.Warn("OL connectivity probe returned {0}", response.StatusCode);
                    return new HealthCheck(
                        GetType(),
                        HealthCheckResult.Error,
                        $"Open Library is unreachable (HTTP {(int)response.StatusCode}). Metadata refreshes and searches will fail until openlibrary.org is reachable.",
                        "#metadata-source-unreachable");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "OL connectivity probe threw");
                return new HealthCheck(
                    GetType(),
                    HealthCheckResult.Error,
                    "Open Library is unreachable. Metadata refreshes and searches will fail until openlibrary.org is reachable.",
                    "#metadata-source-unreachable");
            }

            return new HealthCheck(GetType());
        }
    }
}
