using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.Events;

namespace NzbDrone.Core.MetadataSource
{
    public interface IMetadataSourceStatusService
    {
        bool IsAvailable { get; }
        DateTime? UnavailableUntil { get; }

        void RecordReachable();
        void RecordFailure(Exception ex);
        void EnsureAvailable();
    }

    // A circuit breaker in front of the metadata source.
    //
    // Since the import stopped aborting on metadata errors, a systematic refusal
    // — a rate-limit ban, an outage — no longer announces itself. Every lookup
    // fails, every book imports unmatched, and the run reports success. Worse,
    // continuing to hammer an endpoint that is rate-limiting us is what extends
    // the ban: this repository already knows that openlibrary.org will refuse a
    // source IP for minutes at a time (it is why the integration suite runs one
    // fixture per invocation).
    //
    // So: after ConsecutiveFailureThreshold refusals with no successful contact
    // in between, stop asking for a while, and say so on the health page.
    //
    // What counts as a refusal is the crux. A 404 does not — it is the source
    // answering us about a record it does not have, which is an ordinary and
    // frequent outcome for an obscure ISBN. Only "we could not get an answer at
    // all" counts: a retry-exhausted 429 or 5xx, or a network-level failure.
    // Anything looser trips on a healthy import of an unusual library.
    public class MetadataSourceStatusService : IMetadataSourceStatusService
    {
        public const int ConsecutiveFailureThreshold = 5;

        // Each counted failure has already survived Send()'s three retries with
        // 2s/4s/8s backoff, so five in a row is roughly seventy seconds of solid
        // refusal with nothing succeeding in between. That is not ambiguous.
        // (The one faster route here is a refusal carrying a Retry-After longer
        // than the proxy will wait, which is not retried at all. Five of those
        // is the server saying "not now" five times in as many requests, and
        // is no more ambiguous.)
        private static readonly TimeSpan[] EscalatingCooldowns =
        {
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(1)
        };

        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _syncRoot = new object();

        private int _consecutiveFailures;
        private int _escalationLevel;
        private DateTime? _unavailableUntil;

        public MetadataSourceStatusService(IEventAggregator eventAggregator, Logger logger)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // Seam for tests: cooldown expiry is the behaviour that matters most and
        // it is not worth an hour of sleeping to assert.
        protected virtual DateTime UtcNow => DateTime.UtcNow;

        public bool IsAvailable
        {
            get
            {
                lock (_syncRoot)
                {
                    return !_unavailableUntil.HasValue || UtcNow >= _unavailableUntil.Value;
                }
            }
        }

        public DateTime? UnavailableUntil
        {
            get
            {
                lock (_syncRoot)
                {
                    return _unavailableUntil;
                }
            }
        }

        public void RecordReachable()
        {
            lock (_syncRoot)
            {
                if (_unavailableUntil.HasValue)
                {
                    _logger.Info("Metadata source is answering again");
                }

                _consecutiveFailures = 0;
                _escalationLevel = 0;
                _unavailableUntil = null;
            }
        }

        // Takes any failure and decides for itself whether it counts. The
        // classification lives here rather than in the caller on purpose: "a 404
        // must never trip the breaker" is the property the whole design rests
        // on, and it should not depend on every call site remembering to filter.
        public void RecordFailure(Exception ex)
        {
            DateTime until;

            if (!IndicatesUnavailable(ex))
            {
                // The source answered — about a record it does not have, or a
                // request it did not like. Either way it is reachable.
                RecordReachable();
                return;
            }

            lock (_syncRoot)
            {
                _consecutiveFailures++;

                if (_consecutiveFailures < ConsecutiveFailureThreshold)
                {
                    return;
                }

                // Already open and the window has not expired — the caller raced
                // past EnsureAvailable. Don't escalate twice for one outage.
                if (_unavailableUntil.HasValue && UtcNow < _unavailableUntil.Value)
                {
                    return;
                }

                var cooldown = EscalatingCooldowns[Math.Min(_escalationLevel, EscalatingCooldowns.Length - 1)];

                _escalationLevel = Math.Min(_escalationLevel + 1, EscalatingCooldowns.Length);
                _unavailableUntil = UtcNow + cooldown;
                until = _unavailableUntil.Value;

                _logger.Error(ex,
                    "Metadata source has refused {0} requests in a row; not asking again for {1} minutes. Imports running now will match nothing until it recovers.",
                    _consecutiveFailures,
                    (int)cooldown.TotalMinutes);
            }

            _eventAggregator.PublishEvent(new MetadataSourceUnavailableEvent(until));
        }

        public void EnsureAvailable()
        {
            DateTime until;

            lock (_syncRoot)
            {
                if (!_unavailableUntil.HasValue || UtcNow >= _unavailableUntil.Value)
                {
                    return;
                }

                until = _unavailableUntil.Value;
            }

            throw new MetadataSourceUnavailableException(
                "Metadata source is unavailable until {0:u}; not sending further requests until then",
                until);
        }

        // "We could not get an answer", as opposed to "the answer was no".
        // A 404 is the source telling us it has no such record; that is contact,
        // and it must never contribute to tripping the breaker.
        public static bool IndicatesUnavailable(Exception ex)
        {
            if (ex is MetadataSourceUnavailableException)
            {
                return false;
            }

            if (ex is HttpException http)
            {
                return http.Response == null ||
                       http.Response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                       http.Response.HasHttpServerError;
            }

            return IsTransportFailure(ex);
        }

        // The request never produced a response: no connection, a connection
        // that went quiet, or a body that stopped part-way.
        //
        // This is the one definition of that, shared with OpenLibraryProxy's
        // retry loop. They were two hand-copied lists, and both missed the same
        // thing: the HTTP dispatcher does not let a timeout out as a
        // TaskCanceledException, nor a torn body as an IOException -- it wraps
        // them in WebException (Timeout and ReceiveFailure respectively, see
        // ManagedHttpDispatcher). So a timeout was not retried, and here it fell
        // through to "the source answered", which RecordFailure treats as
        // contact: the failure count was reset and an open cooldown cleared. A
        // server that hangs is the textbook overloaded server, and it was the
        // one failure the breaker could not see.
        //
        // ProtocolError is the exception to the exception. It is the status a
        // WebException carries when there *was* a response -- HttpClient uses
        // it for a redirect chain that never ends -- and a server that answered
        // every hop of a loop is reachable, just wrong about that record.
        public static bool IsTransportFailure(Exception ex)
        {
            if (ex is System.Net.WebException web)
            {
                return web.Status != System.Net.WebExceptionStatus.ProtocolError;
            }

            return ex is System.Net.Http.HttpRequestException
                || ex is System.IO.IOException
                || ex is System.Threading.Tasks.TaskCanceledException;
        }
    }
}
