using NzbDrone.Common.Http;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    public interface IOpenLibraryRequestBuilder
    {
        HttpRequestBuilder For(string path);
    }

    public class OpenLibraryRequestBuilder : IOpenLibraryRequestBuilder
    {
        // A tiny stable resource (OL author "Mark Twain") for checks that only
        // need some response back: connectivity, the proxy, the system clock.
        public const string ProbePath = "authors/OL18319A.json";

        private const string BaseUrl = "https://openlibrary.org/";

        public HttpRequestBuilder For(string path)
        {
            // OL's documented politeness rule: identify the consumer and
            // throttle. See https://openlibrary.org/developers/api#politeness
            return new HttpRequestBuilder(BaseUrl + path.TrimStart('/'))
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", MetadataUserAgent.Value)

                // WithRateLimit(seconds) — 0.6s between calls ≈ 100 req/min.
                //
                // OL publishes a hard number for the covers API only (100 req
                // per IP per 5 minutes, 403 when exceeded) and that limit does
                // not apply here — this builder only issues bibliographic JSON
                // requests against openlibrary.org, not covers.openlibrary.org.
                // See OpenLibraryCoverUrls for the covers side.
                //
                // No published number exists for the JSON endpoints, so stay
                // conservative and identifiable rather than fast.
                .WithRateLimit(0.6);
        }
    }
}
