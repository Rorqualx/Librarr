using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Integration.Test.Client;

namespace NzbDrone.Integration.Test.ApiTests
{
    // Ran as "Not ready to be used on this branch" from upstream 1b2ae92
    // (2025-06-03) until 2026-11-21. The branch it named was upstream's, and
    // upstream retired on 2025-06-27; the sentence had no referent here from
    // the day the fork started.
    //
    // Re-checked rather than reasoned about: marker removed, fixture run, and
    // it passes -- locally, and on ubuntu-22.04 via nightly-integration.yml
    // (PASS CommandFixture). Posting rsssync returns a command with a real id.
    [TestFixture]
    public class CommandFixture : IntegrationTest
    {
        [Test]
        public void should_be_able_to_run_rss_sync()
        {
            var response = Commands.Post(new SimpleCommandResource { Name = "rsssync" });

            response.Id.Should().NotBe(0);
        }
    }
}
