using System.Linq;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using Readarr.Api.V1.Indexers;

namespace NzbDrone.Integration.Test.ApiTests
{
    // BACKLOG, not a stale marker. Disabled by upstream 1b2ae92 (2025-06-03,
    // "Skip failing tests"), 24 days before the fork point -- but unlike the
    // markers removed from AuthorFixture and friends, this reason did not
    // expire with bookinfo.club. It describes work nobody has done.
    //
    // Every test here needs releases to exist, which needs an indexer that
    // returns some. There is none. IntegrationTest.InitializeTestTarget reads
    // the Newznab schema, sets seven fields on the resource and then drops
    // it -- the only save in that method is HostConfig.Put -- so the indexer
    // it appears to configure is never created, and Releases.All() comes back
    // empty. Enabling these tests means both halves: actually saving an
    // indexer, and giving it a stub Newznab endpoint to answer from. That is
    // the file's own "Maybe we should create a full mock Newznab server
    // endpoint" from upstream, still unbuilt.
    //
    // Tracked in #138. Do not drop the marker without building that stub:
    // the tests would fail on empty input, not on anything we broke.
    [TestFixture]
    [Ignore("Need mock Newznab to test -- see #138")]
    public class ReleaseFixture : IntegrationTest
    {
        [Test]
        public void should_only_have_unknown_series_releases()
        {
            var releases = Releases.All();
            var indexers = Indexers.All();

            releases.Should().OnlyContain(c => c.Rejections.Contains("Unknown Series"));
            releases.Should().OnlyContain(c => BeValidRelease(c));
        }

        [Test]
        public void should_reject_unknown_release()
        {
            var result = Releases.Post(new ReleaseResource { Guid = "unknown" }, HttpStatusCode.NotFound);

            result.Id.Should().Be(0);
        }

        [Test]
        public void should_accept_request_with_only_guid_supplied()
        {
            var releases = Releases.All();

            // InternalServerError is caused by the Release being invalid for download (no Series).
            // But if it didn't accept it, it would return NotFound.
            // TODO: Maybe we should create a full mock Newznab server endpoint.
            //var result = Releases.Post(new ReleaseResource { Guid = releases.First().Guid });
            //result.Guid.Should().Be(releases.First().Guid);
            var result = Releases.Post(new ReleaseResource { Guid = releases.First().Guid }, HttpStatusCode.InternalServerError);
        }

        private bool BeValidRelease(ReleaseResource releaseResource)
        {
            releaseResource.Guid.Should().NotBeNullOrEmpty();
            releaseResource.Age.Should().BeGreaterOrEqualTo(-1);
            releaseResource.Title.Should().NotBeNullOrWhiteSpace();
            releaseResource.DownloadUrl.Should().NotBeNullOrWhiteSpace();
            releaseResource.AuthorName.Should().NotBeNullOrWhiteSpace();

            //TODO: uncomment these after moving to restsharp for rss
            //releaseResource.NzbInfoUrl.Should().NotBeNullOrWhiteSpace();
            //releaseResource.Size.Should().BeGreaterThan(0);
            return true;
        }
    }
}
