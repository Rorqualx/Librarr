using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Integration.Test.Client;
using Readarr.Api.V1.DiskSpace;

namespace NzbDrone.Integration.Test.ApiTests
{
    // Ran as "Fails on build agent" from upstream 1b2ae92 (2025-06-03) until
    // 2026-11-21. That is a claim about a specific CI machine, and upstream's
    // was Azure Pipelines; ours is a GitHub ubuntu-22.04 runner. A laptop
    // cannot settle it either way, so it was settled on the machine the
    // sentence is about: marker removed, dispatched to
    // nightly-integration.yml, PASS DiskSpaceFixture.
    //
    // If it does turn out to be flaky there rather than broken, bring the
    // marker back with what actually happened in it -- a reason naming a
    // build agent nobody here uses is the thing that made this take a year
    // to check.
    [TestFixture]
    public class DiskSpaceFixture : IntegrationTest
    {
        public ClientBase<DiskSpaceResource> DiskSpace;

        protected override void InitRestClients()
        {
            base.InitRestClients();

            DiskSpace = new ClientBase<DiskSpaceResource>(RestClient, ApiKey, "diskSpace");
        }

        [Test]
        public void get_all_diskspace()
        {
            var items = DiskSpace.All();

            items.Should().NotBeEmpty();
            items.First().FreeSpace.Should().NotBe(0);
            items.First().TotalSpace.Should().NotBe(0);
            items.First().Path.Should().NotBeNullOrEmpty();
        }
    }
}
