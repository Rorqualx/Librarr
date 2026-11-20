using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Test.Common;

namespace NzbDrone.Windows.Test.EnvironmentInfo
{
    [TestFixture]
    [Platform("Net")]
    public class DotNetPlatformInfoFixture : TestBase<PlatformInfo>
    {
        [Test]
        public void should_get_framework_version()
        {
            // Under NUnit 3, [Platform("Net")] meant .NET FRAMEWORK, so on
            // net10.0 this test was filtered out and reported Inconclusive --
            // it had not actually run since the .NET 6 migration. NUnit 5
            // matches "Net" against modern .NET, so it runs now, and the old
            // assertion (major 4, minor 0/5/6/7/8) was a .NET Framework 4.x
            // expectation that can never hold again.
            //
            // PlatformInfo.Version is just Environment.Version. Assert the
            // floor -- modern .NET rather than Framework -- so that a runtime
            // major upgrade does not have to touch this test.
            Subject.Version.Major.Should().BeGreaterOrEqualTo(5);
        }
    }
}
