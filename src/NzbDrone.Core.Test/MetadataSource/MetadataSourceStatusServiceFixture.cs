using System;
using System.IO;
using System.Net;
using System.Net.Http;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Events;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MetadataSource
{
    // The circuit breaker in front of the metadata source. Two behaviours carry
    // the whole design: what counts as a refusal (404s must not), and that the
    // cooldown escalates and then expires.
    [TestFixture]
    public class MetadataSourceStatusServiceFixture : CoreTest<TestMetadataSourceStatusService>
    {
        private static HttpException WithStatus(HttpStatusCode status)
        {
            var request = new HttpRequest("https://openlibrary.org/isbn/9780345391803.json");
            return new HttpException(new HttpResponse(request, new HttpHeader(), Array.Empty<byte>(), status));
        }

        private void GivenFailures(int count, HttpStatusCode status)
        {
            for (var i = 0; i < count; i++)
            {
                Subject.RecordFailure(WithStatus(status));
            }
        }

        // The single most important property. A 404 is OL answering about a
        // record it does not have, which any library of obscure ISBNs produces
        // constantly. Counting those would trip the breaker on a healthy import.
        [Test]
        public void should_never_trip_on_not_found()
        {
            GivenFailures(50, HttpStatusCode.NotFound);

            Subject.IsAvailable.Should().BeTrue();
        }

        [TestCase(HttpStatusCode.BadRequest)]
        [TestCase(HttpStatusCode.UnprocessableEntity)]
        [TestCase(HttpStatusCode.Unauthorized)]
        public void should_not_trip_on_other_answered_statuses(HttpStatusCode status)
        {
            GivenFailures(50, status);

            Subject.IsAvailable.Should().BeTrue();
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void should_trip_after_five_refusals(HttpStatusCode status)
        {
            GivenFailures(MetadataSourceStatusService.ConsecutiveFailureThreshold - 1, status);
            Subject.IsAvailable.Should().BeTrue("four is not yet a pattern");

            GivenFailures(1, status);
            Subject.IsAvailable.Should().BeFalse();

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_trip_on_network_level_failures()
        {
            for (var i = 0; i < MetadataSourceStatusService.ConsecutiveFailureThreshold; i++)
            {
                Subject.RecordFailure(new HttpRequestException("no such host"));
            }

            Subject.IsAvailable.Should().BeFalse("a DNS failure is the clearest possible refusal");

            ExceptionVerification.ExpectedErrors(1);
        }

        // What the HTTP dispatcher actually raises when a request hangs past its
        // timeout, or the body stops arriving part-way. Hanging and dropping
        // connections is how an overloaded server behaves, so these are the
        // failures the breaker most needs to see.
        [TestCase(WebExceptionStatus.Timeout)]
        [TestCase(WebExceptionStatus.ReceiveFailure)]
        [TestCase(WebExceptionStatus.ConnectFailure)]
        [TestCase(WebExceptionStatus.NameResolutionFailure)]
        public void should_trip_on_dispatcher_level_failures(WebExceptionStatus status)
        {
            for (var i = 0; i < MetadataSourceStatusService.ConsecutiveFailureThreshold; i++)
            {
                Subject.RecordFailure(new WebException("Http request timed out", null, status, null));
            }

            Subject.IsAvailable.Should().BeFalse();

            ExceptionVerification.ExpectedErrors(1);
        }

        // Not being counted was only half of it: an unrecognised failure is
        // filed as "the source answered", which wipes the run so far. One
        // timeout among the 429s kept the breaker closed indefinitely.
        [Test]
        public void a_timeout_should_not_reset_a_run_of_refusals()
        {
            GivenFailures(2, HttpStatusCode.TooManyRequests);
            Subject.RecordFailure(new WebException("Http request timed out", null, WebExceptionStatus.Timeout, null));
            GivenFailures(2, HttpStatusCode.TooManyRequests);

            Subject.IsAvailable.Should().BeFalse("that is five failures to get an answer, in a row");

            ExceptionVerification.ExpectedErrors(1);
        }

        // HttpClient raises a WebException of its own for a redirect chain that
        // never ends. The server was answering every hop of that, so it is a
        // bad record rather than an unreachable source.
        [Test]
        public void should_not_trip_on_a_redirect_loop()
        {
            for (var i = 0; i < 50; i++)
            {
                Subject.RecordFailure(new WebException("Too many automatic redirections were attempted", WebExceptionStatus.ProtocolError));
            }

            Subject.IsAvailable.Should().BeTrue();
        }

        [Test]
        public void should_count_mixed_transient_failures_together()
        {
            Subject.RecordFailure(new HttpRequestException("connection reset"));
            Subject.RecordFailure(new IOException("stream torn"));
            Subject.RecordFailure(WithStatus(HttpStatusCode.TooManyRequests));
            Subject.RecordFailure(WithStatus(HttpStatusCode.BadGateway));
            Subject.IsAvailable.Should().BeTrue();

            Subject.RecordFailure(new IOException("stream torn"));
            Subject.IsAvailable.Should().BeFalse();

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void a_single_success_should_reset_the_run_of_failures()
        {
            GivenFailures(4, HttpStatusCode.TooManyRequests);
            Subject.RecordReachable();
            GivenFailures(4, HttpStatusCode.TooManyRequests);

            Subject.IsAvailable.Should().BeTrue("the counter restarts from the success");
        }

        [Test]
        public void should_throw_from_ensure_available_while_tripped()
        {
            GivenFailures(5, HttpStatusCode.TooManyRequests);

            Assert.Throws<MetadataSourceUnavailableException>(() => Subject.EnsureAvailable());

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_not_throw_from_ensure_available_when_healthy()
        {
            Assert.DoesNotThrow(() => Subject.EnsureAvailable());
        }

        [Test]
        public void cooldown_should_escalate_across_repeated_outages()
        {
            var expected = new[] { 5.0, 15.0, 60.0, 60.0 };

            foreach (var minutes in expected)
            {
                GivenFailures(5, HttpStatusCode.TooManyRequests);

                Subject.UnavailableUntil.Should().NotBeNull();
                (Subject.UnavailableUntil.Value - Subject.Now).TotalMinutes
                    .Should().BeApproximately(minutes, 0.01);

                // Ride out the cooldown; the source is still refusing when we
                // come back, so the next outage escalates.
                Subject.Now = Subject.UnavailableUntil.Value;
                Subject.IsAvailable.Should().BeTrue("the window has expired, so we probe again");
            }

            // One Error per trip; tripping is meant to be loud.
            ExceptionVerification.ExpectedErrors(expected.Length);
        }

        [Test]
        public void should_recover_when_the_source_answers_after_a_cooldown()
        {
            GivenFailures(5, HttpStatusCode.TooManyRequests);
            Subject.Now = Subject.UnavailableUntil.Value;

            Subject.RecordReachable();

            Subject.IsAvailable.Should().BeTrue();
            Subject.UnavailableUntil.Should().BeNull();

            // Escalation reset too: the next outage starts at the shortest window.
            GivenFailures(5, HttpStatusCode.TooManyRequests);
            (Subject.UnavailableUntil.Value - Subject.Now).TotalMinutes.Should().BeApproximately(5.0, 0.01);

            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public void should_publish_an_event_so_the_health_check_reruns()
        {
            GivenFailures(5, HttpStatusCode.TooManyRequests);

            VerifyEventPublished<MetadataSourceUnavailableEvent>();

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_not_escalate_twice_for_one_outage()
        {
            GivenFailures(5, HttpStatusCode.TooManyRequests);
            var first = Subject.UnavailableUntil.Value;

            // A request already in flight when the breaker tripped reports back.
            GivenFailures(3, HttpStatusCode.TooManyRequests);

            Subject.UnavailableUntil.Should().Be(first, "the window must not keep sliding out");

            ExceptionVerification.ExpectedErrors(1);
        }
    }

    // Test seam over the service's clock. The production class reads
    // DateTime.UtcNow; asserting an escalating cooldown otherwise means
    // sleeping for an hour.
    public class TestMetadataSourceStatusService : MetadataSourceStatusService
    {
        public TestMetadataSourceStatusService(NzbDrone.Core.Messaging.Events.IEventAggregator eventAggregator, NLog.Logger logger)
            : base(eventAggregator, logger)
        {
            Now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        }

        public DateTime Now { get; set; }

        protected override DateTime UtcNow => Now;
    }
}
