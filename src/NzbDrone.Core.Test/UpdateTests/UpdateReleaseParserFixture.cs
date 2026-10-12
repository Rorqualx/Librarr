using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Update;

namespace NzbDrone.Core.Test.UpdateTests
{
    [TestFixture]
    public class UpdateReleaseParserFixture
    {
        // The right-hand side is what build.sh stamps into a build of that tag.
        [TestCase("v1.2.3-beta", "1.2.3.0")]
        [TestCase("v1.2.3", "1.2.3.0")]
        [TestCase("1.2.3", "1.2.3.0")]
        [TestCase("V1.2.3-rc1", "1.2.3.0")]
        [TestCase("v1.0.0-beta.4", "1.0.0.4")]
        [TestCase("v1.2.3.7", "1.2.3.7")]
        [TestCase("v2.0", "2.0.0.0")]
        [TestCase("v1.2.3-beta+build.5", "1.2.3.0")]
        public void should_parse_tag_to_the_version_its_build_reports(string tag, string expected)
        {
            UpdateReleaseParser.TryParseVersion(tag, out var version).Should().BeTrue();
            version.Should().Be(Version.Parse(expected));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("nightly")]
        [TestCase("v1")]
        [TestCase("vNext")]
        [TestCase("v1.2.3.4.5")]
        public void should_reject_a_tag_that_is_not_a_version(string tag)
        {
            UpdateReleaseParser.TryParseVersion(tag, out var version).Should().BeFalse();
            version.Should().BeNull();
        }

        [TestCase("v1.2.3-beta", "v1.2.2-beta")]
        [TestCase("v1.2.10-beta", "v1.2.9-beta")]
        [TestCase("v1.10.0-beta", "v1.9.9-beta")]
        [TestCase("v2.0.0-beta", "v1.9.9")]
        [TestCase("v1.2.3", "v1.2.2-beta")]
        [TestCase("v1.0.0-beta.4", "v1.0.0-beta.3")]
        [TestCase("v1.0.0-beta.10", "v1.0.0-beta.9")]
        public void should_order_releases(string newer, string older)
        {
            UpdateReleaseParser.TryParseVersion(newer, out var newerVersion);
            UpdateReleaseParser.TryParseVersion(older, out var olderVersion);

            newerVersion.Should().BeGreaterThan(olderVersion);
        }

        [Test]
        public void should_treat_a_tag_and_the_build_made_from_it_as_equal()
        {
            UpdateReleaseParser.TryParseVersion("v1.2.3-beta", out var version);

            // AssemblyVersion 1.2.3, as the running app reports it.
            version.Should().Be(UpdateReleaseParser.Normalize(new Version(1, 2, 3)));
            version.Should().Be(new Version(1, 2, 3, 0));
        }

        [Test]
        public void should_not_order_by_the_label_alone()
        {
            // Documented limitation: the build of v1.2.3-beta reports 1.2.3.0,
            // exactly as a build of v1.2.3 would.
            UpdateReleaseParser.TryParseVersion("v1.2.3-beta", out var beta);
            UpdateReleaseParser.TryParseVersion("v1.2.3", out var stable);

            stable.Should().Be(beta);
        }

        [TestCase(Os.Linux, Architecture.X64, "linux-x64")]
        [TestCase(Os.Linux, Architecture.Arm, "linux-arm")]
        [TestCase(Os.Linux, Architecture.Arm64, "linux-arm64")]
        [TestCase(Os.LinuxMusl, Architecture.X64, "linux-musl-x64")]
        [TestCase(Os.LinuxMusl, Architecture.Arm, "linux-musl-arm")]
        [TestCase(Os.LinuxMusl, Architecture.Arm64, "linux-musl-arm64")]
        [TestCase(Os.Osx, Architecture.X64, "osx-x64")]
        [TestCase(Os.Osx, Architecture.Arm64, "osx-arm64")]
        [TestCase(Os.Windows, Architecture.X64, "win-x64")]
        [TestCase(Os.Windows, Architecture.X86, "win-x86")]
        [TestCase(Os.Bsd, Architecture.X64, null)]
        [TestCase(Os.Linux, Architecture.S390x, null)]
        public void should_map_os_and_architecture_to_runtime_identifier(Os os, Architecture architecture, string expected)
        {
            UpdatePlatform.GetRuntimeIdentifier(os, architecture).Should().Be(expected);
        }

        [TestCase("linux-x64", "Librarr.1.2.3-beta.linux-x64.tar.gz")]
        [TestCase("linux-musl-arm64", "Librarr.1.2.3-beta.linux-musl-arm64.tar.gz")]
        [TestCase("osx-arm64", "Librarr.1.2.3-beta.osx-arm64.tar.gz")]
        [TestCase("win-x64", "Librarr.1.2.3-beta.win-x64.zip")]
        [TestCase("win-x86", "Librarr.1.2.3-beta.win-x86.zip")]
        [TestCase(null, null)]
        public void should_name_the_package_for_a_runtime_identifier(string runtimeIdentifier, string expected)
        {
            UpdatePlatform.GetPackageFileName("1.2.3-beta", runtimeIdentifier).Should().Be(expected);
        }

        [Test]
        public void should_find_checksum_by_exact_file_name()
        {
            const string sums = "AAAA0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000  Librarr.1.2.3-beta.osx-x64-app.zip\n" +
                                "bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000  Librarr.1.2.3-beta.osx-x64.tar.gz\r\n" +
                                "cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000 *./Librarr.1.2.3-beta.win-x64.zip\n";

            UpdateReleaseParser.FindChecksum(sums, "Librarr.1.2.3-beta.osx-x64.tar.gz").Should().Be("bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000");
            UpdateReleaseParser.FindChecksum(sums, "Librarr.1.2.3-beta.osx-x64-app.zip").Should().Be("aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000");
            UpdateReleaseParser.FindChecksum(sums, "Librarr.1.2.3-beta.win-x64.zip").Should().Be("cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000cccc0000");
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("<html>Not Found</html>")]
        [TestCase("bbbb0000  Librarr.1.2.3-beta.linux-x64.tar.gz")]
        [TestCase("bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000  other-Librarr.1.2.3-beta.linux-x64.tar.gz")]
        [TestCase("bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000  Librarr.1.2.3-beta.linux-x64.tar.gz.sig")]
        public void should_not_find_checksum_when_there_is_no_valid_entry(string sums)
        {
            UpdateReleaseParser.FindChecksum(sums, "Librarr.1.2.3-beta.linux-x64.tar.gz").Should().BeNull();
        }

        [Test]
        public void should_split_notes_into_new_and_fixed()
        {
            const string body = "Intro paragraph that is not a change.\n\n" +
                                "## Added\n\n" +
                                "- First thing\n" +
                                "  wrapped onto a second line\n" +
                                "- Second thing\n\n" +
                                "### Bug fixes\n" +
                                "* A fix (#12)\n\n" +
                                "## Installing\n\n" +
                                "- Not a change\n\n" +
                                "```bash\n- not a change either\n```\n";

            var changes = UpdateReleaseParser.ParseChanges(body);

            changes.New.Should().Equal("First thing wrapped onto a second line", "Second thing");
            changes.Fixed.Should().Equal("A fix (#12)");
        }

        [Test]
        public void should_stop_a_section_at_a_horizontal_rule()
        {
            var changes = UpdateReleaseParser.ParseChanges("**Fixed**\n\n- One\n\n---\n\nVerify downloads against `SHA256SUMS`.\n");

            changes.Fixed.Should().Equal("One");
            changes.New.Should().BeEmpty();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Auto-generated release. Verify checksums against SHA256SUMS before installing.")]
        public void should_return_no_changes_when_nothing_is_recognisable(string body)
        {
            UpdateReleaseParser.ParseChanges(body).Should().BeNull();
        }
    }
}
