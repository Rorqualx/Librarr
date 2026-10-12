using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.UpdateTests
{
    // Files/Update/github_releases.json is the real response of
    // https://api.github.com/repos/Rorqualx/Librarr/releases, cut down to three
    // releases and to the fields that matter: v1.2.3-beta and v1.2.2-beta (15
    // assets each) and v1.1.0-beta (which shipped nothing for Windows).
    // Files/Update/SHA256SUMS is rebuilt for v1.2.3-beta out of the digests in
    // that same response, in the format `sha256sum` writes.
    //
    // No test here touches the network.
    public class UpdatePackageProviderFixture : CoreTest<UpdatePackageProvider>
    {
        private const string LinuxX64Hash = "9e0664bdd27b70ced50fa751b55538d8489764fad91212903d52e6a453c95b6b";

        private List<HttpRequest> _requests;
        private string _releasesJson;
        private string _checksums;
        private HttpStatusCode _releasesStatus;
        private HttpStatusCode _checksumsStatus;
        private HttpHeader _releasesHeaders;
        private Exception _exception;

        [SetUp]
        public void Setup()
        {
            _requests = new List<HttpRequest>();
            _releasesJson = ReadAllText("Files/Update/github_releases.json");
            _checksums = ReadAllText("Files/Update/SHA256SUMS");
            _releasesStatus = HttpStatusCode.OK;
            _checksumsStatus = HttpStatusCode.OK;
            _releasesHeaders = new HttpHeader();
            _exception = null;

            Mocker.SetConstant<ICacheManager>(new CacheManager());

            GivenPlatform("linux-x64");

            Mocker.GetMock<IHttpClient>()
                  .Setup(s => s.Execute(It.IsAny<HttpRequest>()))
                  .Returns<HttpRequest>(Respond);
        }

        private HttpResponse Respond(HttpRequest request)
        {
            _requests.Add(request);

            if (_exception != null)
            {
                throw _exception;
            }

            if (request.Url.FullUri.StartsWith(UpdatePackageProvider.ReleasesUrl))
            {
                return new HttpResponse(request, _releasesHeaders, _releasesJson, _releasesStatus);
            }

            if (request.Url.FullUri.EndsWith("/SHA256SUMS"))
            {
                return new HttpResponse(request, new HttpHeader(), _checksums, _checksumsStatus);
            }

            throw new InvalidOperationException("Unexpected request: " + request.Url);
        }

        private void GivenPlatform(string runtimeIdentifier)
        {
            Mocker.GetMock<IUpdatePlatform>()
                  .SetupGet(s => s.RuntimeIdentifier)
                  .Returns(runtimeIdentifier);
        }

        private void GivenRateLimited()
        {
            _releasesStatus = HttpStatusCode.Forbidden;
            _releasesJson = "{\"message\":\"API rate limit exceeded for 203.0.113.7.\"}";
            _releasesHeaders = new HttpHeader();
            _releasesHeaders.Set("X-RateLimit-Remaining", "0");
        }

        private int ReleaseRequests => _requests.Count(r => r.Url.FullUri.StartsWith(UpdatePackageProvider.ReleasesUrl));

        [Test]
        public void should_offer_the_newest_release_when_running_an_older_version()
        {
            var update = Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0));

            update.Should().NotBeNull();
            update.Version.Should().Be(new Version(1, 2, 3, 0));
            update.FileName.Should().Be("Librarr.1.2.3-beta.linux-x64.tar.gz");
            update.Url.Should().Be("https://github.com/Rorqualx/Librarr/releases/download/v1.2.3-beta/Librarr.1.2.3-beta.linux-x64.tar.gz");
            update.ReleaseDate.Should().Be(new DateTime(2026, 10, 12, 2, 28, 25, DateTimeKind.Utc));
        }

        [Test]
        public void should_not_offer_an_update_when_running_the_newest_release()
        {
            Subject.GetLatestUpdate("develop", new Version(1, 2, 3, 0)).Should().BeNull();
        }

        [Test]
        public void should_not_offer_an_update_when_running_something_newer()
        {
            // An unstamped development build reports 10.0.0.x.
            Subject.GetLatestUpdate("develop", new Version(10, 0, 0, 1234)).Should().BeNull();
        }

        [Test]
        public void should_treat_a_three_part_running_version_as_the_same_release()
        {
            // System.Version orders 1.2.3 below 1.2.3.0.
            Subject.GetLatestUpdate("develop", new Version(1, 2, 3)).Should().BeNull();
        }

        [TestCase("linux-x64", "Librarr.1.2.3-beta.linux-x64.tar.gz")]
        [TestCase("linux-arm", "Librarr.1.2.3-beta.linux-arm.tar.gz")]
        [TestCase("linux-arm64", "Librarr.1.2.3-beta.linux-arm64.tar.gz")]
        [TestCase("linux-musl-x64", "Librarr.1.2.3-beta.linux-musl-x64.tar.gz")]
        [TestCase("linux-musl-arm64", "Librarr.1.2.3-beta.linux-musl-arm64.tar.gz")]
        [TestCase("osx-x64", "Librarr.1.2.3-beta.osx-x64.tar.gz")]
        [TestCase("osx-arm64", "Librarr.1.2.3-beta.osx-arm64.tar.gz")]
        [TestCase("win-x64", "Librarr.1.2.3-beta.win-x64.zip")]
        [TestCase("win-x86", "Librarr.1.2.3-beta.win-x86.zip")]
        public void should_pick_the_package_built_for_the_running_platform(string runtimeIdentifier, string fileName)
        {
            GivenPlatform(runtimeIdentifier);

            var update = Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0));

            update.FileName.Should().Be(fileName);
            update.Url.Should().EndWith("/v1.2.3-beta/" + fileName);
            update.Hash.Should().Be(UpdateReleaseParser.FindChecksum(_checksums, fileName));
        }

        [Test]
        public void should_take_the_hash_from_the_checksums_file_of_the_same_release()
        {
            var update = Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0));

            update.Hash.Should().Be(LinuxX64Hash);

            _requests.Should().Contain(r => r.Url.FullUri == "https://github.com/Rorqualx/Librarr/releases/download/v1.2.3-beta/SHA256SUMS");
        }

        [Test]
        public void should_not_offer_an_update_when_no_package_exists_for_the_platform()
        {
            GivenPlatform("linux-riscv64");

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
        }

        [Test]
        public void should_not_offer_an_update_when_the_platform_is_not_supported_at_all()
        {
            GivenPlatform(null);

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
        }

        [Test]
        public void should_skip_releases_without_a_package_for_the_platform()
        {
            // Drop the two newest releases: v1.1.0-beta has no Windows package.
            var releases = Newtonsoft.Json.Linq.JArray.Parse(_releasesJson);
            releases.RemoveAt(0);
            releases.RemoveAt(0);
            _releasesJson = releases.ToString();

            GivenPlatform("win-x64");
            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
        }

        [Test]
        public void should_not_offer_an_update_that_has_no_checksum_entry()
        {
            _checksums = string.Join("\n", _checksums.Split('\n').Where(l => !l.Contains("linux-x64")));

            Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0)).Should().BeNull();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_offer_an_update_when_the_checksums_file_is_missing()
        {
            _checksumsStatus = HttpStatusCode.NotFound;
            _checksums = "Not Found";

            Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0)).Should().BeNull();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_use_a_package_served_from_anywhere_but_the_project_releases()
        {
            _releasesJson = _releasesJson.Replace(
                "https://github.com/Rorqualx/Librarr/releases/download/v1.2.3-beta/Librarr.1.2.3-beta.linux-x64.tar.gz",
                "https://example.invalid/Librarr.1.2.3-beta.linux-x64.tar.gz");

            // v1.2.3-beta is passed over, so 1.2.2 is already the newest usable.
            Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0)).Should().BeNull();
        }

        [Test]
        public void should_report_no_update_quietly_when_rate_limited()
        {
            GivenRateLimited();

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
            Subject.GetRecentUpdates("develop", new Version(1, 0, 0, 0)).Should().BeEmpty();

            // Nothing at Warn or Error: ExceptionVerification fails the test otherwise.
        }

        [Test]
        public void should_report_no_update_quietly_when_the_request_fails()
        {
            _exception = new WebException("Name or service not known");

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
            Subject.GetRecentUpdates("develop", new Version(1, 0, 0, 0)).Should().BeEmpty();
        }

        [Test]
        public void should_report_no_update_quietly_when_the_response_is_not_a_release_list()
        {
            _releasesJson = "<html>captive portal</html>";

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0)).Should().BeNull();
        }

        [Test]
        public void should_not_ask_again_while_rate_limited()
        {
            GivenRateLimited();

            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0));
            Subject.GetLatestUpdate("develop", new Version(1, 0, 0, 0));
            Subject.GetRecentUpdates("develop", new Version(1, 0, 0, 0));

            ReleaseRequests.Should().Be(1);
        }

        [Test]
        public void should_fetch_releases_and_checksums_once_however_often_it_is_asked()
        {
            for (var i = 0; i < 5; i++)
            {
                Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0)).Should().NotBeNull();
                Subject.GetRecentUpdates("develop", new Version(1, 2, 2, 0)).Should().NotBeEmpty();
            }

            _requests.Should().HaveCount(2);
        }

        [Test]
        public void should_not_send_anything_about_the_install()
        {
            Subject.GetLatestUpdate("develop", new Version(1, 2, 2, 0));

            foreach (var request in _requests)
            {
                request.Url.FullUri.Should().NotContain("1.2.2");
                request.Url.Query.Should().NotContainAny("version", "os=", "arch", "runtime", "dbType", "active", "branch");
                request.Headers.GetSingleValue("User-Agent").Should().Be("Librarr (+https://github.com/Rorqualx/Librarr)");
            }

            _requests.Should().OnlyContain(r => r.Url.Host == "api.github.com" || r.Url.Host == "github.com");
        }

        [Test]
        public void should_list_every_release_newest_first()
        {
            var recent = Subject.GetRecentUpdates("develop", new Version(1, 2, 2, 0));

            recent.Select(r => r.Version).Should().Equal(new Version(1, 2, 3, 0), new Version(1, 2, 2, 0), new Version(1, 1, 0, 0));
            recent.Should().OnlyContain(r => r.Branch == "develop");
            recent.Should().OnlyContain(r => r.ReleaseDate.Year == 2026);
            recent.Should().OnlyContain(r => r.FileName.StartsWith("Librarr."));
        }

        [Test]
        public void should_list_a_release_even_without_a_package_for_the_platform()
        {
            GivenPlatform("win-x64");

            var recent = Subject.GetRecentUpdates("develop", new Version(1, 2, 2, 0));

            recent.Should().HaveCount(3);
            recent.Last().Version.Should().Be(new Version(1, 1, 0, 0));
            recent.Last().Url.Should().BeNull();
        }

        [Test]
        public void should_map_release_notes_to_changes()
        {
            var recent = Subject.GetRecentUpdates("develop", new Version(1, 2, 2, 0));

            var latest = recent[0];
            latest.Changes.New.Should().BeEmpty();
            latest.Changes.Fixed.Should().HaveCount(6);
            latest.Changes.Fixed.First().Should().Be("Logged-out browsers got a blank 401 instead of the login page (#17).");

            // v1.2.2-beta carries only installation boilerplate.
            recent[1].Changes.Should().BeNull();

            var oldest = recent[2];

            // Two paragraphs under "Added" and one under "Changed".
            oldest.Changes.New.Should().HaveCount(3);
            oldest.Changes.New.First().Should().StartWith("**Library Import wizard**");
            oldest.Changes.Fixed.Should().NotBeEmpty();
            oldest.Changes.New.Concat(oldest.Changes.Fixed).Should().NotContain(c => c.Contains("docker pull"));
        }

        [Test]
        public void should_ignore_drafts_and_tags_that_are_not_versions()
        {
            _releasesJson = @"[
                { ""tag_name"": ""v9.0.0"", ""draft"": true, ""assets"": [] },
                { ""tag_name"": ""nightly"", ""draft"": false, ""assets"": [] },
                { ""tag_name"": ""v1.0.0-beta"", ""draft"": false, ""published_at"": ""2026-05-20T04:26:38Z"", ""assets"": [] }
            ]";

            var recent = Subject.GetRecentUpdates("develop", new Version(1, 0, 0, 0));

            recent.Should().ContainSingle().Which.Version.Should().Be(new Version(1, 0, 0, 0));
        }
    }
}
