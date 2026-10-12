using System.IO;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update;

namespace NzbDrone.Core.Test.UpdateTests
{
    [TestFixture]
    public class UpdateVerificationFixture : CoreTest<UpdateVerification>
    {
        // A well-formed hash that is not the package's.
        private const string OtherHash = "0f7b0a7c0ed7b9ad0a4a2c9d0e1b47bd0f5f0d2d8d0a1c1a8d4b9a5e4a2a5d0f";

        private string _packagePath;

        [SetUp]
        public void Setup()
        {
            _packagePath = GetTempFilePath();
            File.WriteAllText(_packagePath, "package");

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.OpenReadStream(_packagePath))
                  .Returns(() => File.OpenRead(_packagePath));
        }

        private string ActualHash()
        {
            using (var stream = File.OpenRead(_packagePath))
            {
                return stream.SHA256Hash();
            }
        }

        [Test]
        public void should_accept_a_package_that_matches_its_checksum()
        {
            Subject.Verify(new UpdatePackage { Hash = ActualHash().ToUpperInvariant() }, _packagePath).Should().BeTrue();
        }

        [Test]
        public void should_reject_a_package_that_does_not_match_its_checksum()
        {
            Subject.Verify(new UpdatePackage { Hash = OtherHash }, _packagePath).Should().BeFalse();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void should_reject_a_package_that_has_no_checksum(string hash)
        {
            Subject.Verify(new UpdatePackage { Hash = hash }, _packagePath).Should().BeFalse();
        }
    }
}
