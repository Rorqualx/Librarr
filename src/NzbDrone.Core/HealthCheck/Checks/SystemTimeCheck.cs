using System;
using System.Globalization;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Compares the system clock with the Date header of a response from the
    // metadata source, a host Librarr talks to anyway.
    //
    // Upstream asked readarr.servarr.com/time, the retired project's server, for
    // nothing more than the current time. Any HTTP response already carries that.
    public class SystemTimeCheck : HealthCheckBase
    {
        private readonly IHttpClient _client;
        private readonly IOpenLibraryRequestBuilder _requestBuilder;
        private readonly Logger _logger;

        public SystemTimeCheck(IHttpClient client, IOpenLibraryRequestBuilder requestBuilder, ILocalizationService localizationService, Logger logger)
            : base(localizationService)
        {
            _client = client;
            _requestBuilder = requestBuilder;
            _logger = logger;
        }

        public override HealthCheck Check()
        {
            var serverTime = GetServerTime();

            // Not knowing the time is not evidence that the clock is wrong, and
            // whether the metadata source can be reached is another check's job.
            if (!serverTime.HasValue)
            {
                return new HealthCheck(GetType());
            }

            var systemTime = DateTime.UtcNow;

            // +/- more than 1 day
            if (Math.Abs(serverTime.Value.Subtract(systemTime).TotalDays) >= 1)
            {
                _logger.Error("System time mismatch. SystemTime: {0} Expected Time: {1}. Update system time", systemTime, serverTime.Value);
                return new HealthCheck(GetType(), HealthCheckResult.Error, _localizationService.GetLocalizedString("SystemTimeCheckMessage"), "#system-time-off");
            }

            return new HealthCheck(GetType());
        }

        private DateTime? GetServerTime()
        {
            try
            {
                var request = _requestBuilder.For(OpenLibraryRequestBuilder.ProbePath).Build();

                // An error response is dated just the same.
                request.SuppressHttpError = true;

                var response = _client.Execute(request);
                var date = response.Headers.GetSingleValue("Date");

                if (date != null && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return parsed.UtcDateTime;
                }

                _logger.Debug("Could not check the system time, the response carried no usable Date header");
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not check the system time");
            }

            return null;
        }
    }
}
