using System;
using NUnit.Framework;

namespace NzbDrone.Integration.Test
{
    // Assembly-wide gate for the integration suite. Every fixture here boots a
    // real Readarr instance and calls /author/lookup during setup.
    //
    // That used to hit api.bookinfo.club, retired upstream on 2025-06-27, and
    // this gate was written on the assumption that every test therefore failed
    // its setup. The OpenLibrary cutover fixed that, and the six fixtures that
    // still carried a stale [Ignore("Waiting for metadata to be back again")]
    // from that era — AuthorFixture, AuthorLookupFixture, CalendarFixture,
    // BlocklistFixture, MissingFixture and CutoffUnmetFixture — have had it
    // removed along with the Goodreads identifiers that were the real
    // blocker. See OpenLibraryFixtureData.
    //
    // The gate stays because the reason it is useful survives the fix: these
    // tests need the network and a couple of minutes, so they should not run
    // on a bare `dotnet test`. Opt in with READARR_RUN_INTEGRATION=1.
    //
    // ONE CI JOB OPTS IN, FOR SIX TESTS. build.yml's "API 4xx contract
    // tests" sets READARR_RUN_INTEGRATION=1 and then filters to
    // TestCategory=ApiContract: the six tests here that assert a 4xx and
    // reach no third party doing it. That is the gate working as designed,
    // not a hole in it -- what keeps the other 88 out is the filter, so the
    // filter is the load-bearing part. Removing it would point all 94 at
    // openlibrary.org on every push, which is the exact thing the paragraph
    // below describes going wrong. check_required_tests_wired.py fails the
    // commit if the filter disappears, and assert_tests_ran.py --only fails
    // the job if anything outside the list runs anyway. Do not add
    // [Category("ApiContract")] to a test that touches OpenLibrary.
    //
    // RUN ONE FIXTURE AT A TIME. Every fixture starts with an empty appdata,
    // so no OpenLibrary cache carries over and each one re-fetches the author
    // and its works from scratch. Running the whole ApiTests namespace in a
    // single pass was enough to get the source IP refused --
    // "Connection refused (openlibrary.org:443)" on 26 of 88 tests, with the
    // refusals continuing for minutes afterwards. Individually the fixtures
    // are fine:
    //
    //   dotnet test src/NzbDrone.Integration.Test/ -c Debug \
    //       --filter "FullyQualifiedName~AuthorFixture"
    //
    // -c Debug matters too: the Release path boots _tests/bin, which only a
    // full ./build.sh refreshes. See NzbDroneRunner.Start.
    [SetUpFixture]
    public class AssemblyGate
    {
        [OneTimeSetUp]
        public void GateAssembly()
        {
            if (Environment.GetEnvironmentVariable("READARR_RUN_INTEGRATION") != "1")
            {
                Assert.Ignore(
                    "Integration.Test boots a real instance and calls live OpenLibrary. " +
                    "Set READARR_RUN_INTEGRATION=1 to run it.");
            }
        }
    }
}
