using NUnit.Framework;

namespace NzbDrone.Integration.Test.ApiTests
{
    // BACKLOG, not a stale marker. There is no test here to revive: the body
    // has been Assert.Ignore("TODO") since upstream 1b2ae92 (2025-06-03), so
    // what is missing is the test itself, not permission to run it.
    //
    // The marker moved from the body to the fixture on purpose. Assert.Ignore
    // inside the method runs AFTER IntegrationTest's OneTimeSetUp, so every
    // nightly booted a real Readarr instance, waited for its scheduler, and
    // then ignored the one test. [Ignore] on the fixture skips the setup too.
    //
    // Writing it means seeding book files on disk and importing them, which
    // is why it was never a one-liner. Tracked in #139.
    [TestFixture]
    [Ignore("No test written yet -- see #139")]
    public class BookFileFixture : IntegrationTest
    {
        [Test]
        public void get_all_bookfiles()
        {
            Assert.Fail("Not implemented -- see #139. The fixture-level [Ignore] is what skips this.");
        }
    }
}
