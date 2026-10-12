using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Test.Common;

namespace NzbDrone.Common.Test.DiskTests
{
    [TestFixture]
    public class PathContainmentFixture : TestBase
    {
        private static readonly string Root = @"C:\Test\UI".AsOsAgnostic();

        [TestCase("index.js")]
        [TestCase("Content/Images/logo.png")]
        [TestCase(@"Content\Images\logo.png")]
        [TestCase("Content/../Content/logo.png")]
        [TestCase("..data/file.txt")]
        public void should_accept_path_inside_folder(string relative)
        {
            PathContainment.IsInside(Root, Root + "/" + relative).Should().BeTrue();
        }

        [TestCase("../secret.txt")]
        [TestCase("Content/../../secret.txt")]
        [TestCase(@"..\secret.txt")]
        [TestCase(@"Content\..\..\secret.txt")]
        [TestCase(@"Content/..\../secret.txt")]
        [TestCase("../UI-other/secret.txt")]
        [TestCase("")]
        [TestCase(".")]
        [TestCase("Content/..")]
        public void should_reject_path_outside_folder(string relative)
        {
            PathContainment.IsInside(Root, Root + "/" + relative).Should().BeFalse();
        }

        [Test]
        public void should_reject_sibling_folder_sharing_a_name_prefix()
        {
            PathContainment.IsInside(Root, Root + "-other/secret.txt").Should().BeFalse();
        }

        [Test]
        public void should_reject_unrelated_absolute_path()
        {
            PathContainment.IsInside(Root, @"C:\Windows\win.ini".AsOsAgnostic()).Should().BeFalse();
        }

        [Test]
        public void should_resolve_relative_segments_in_the_folder_itself()
        {
            PathContainment.IsInside(Root + "/../UI", Root + "/index.js").Should().BeTrue();
        }

        [Test]
        public void should_honour_requested_case_sensitivity()
        {
            var path = Root.ToUpperInvariant() + "/index.js";

            PathContainment.IsInside(Root.ToLowerInvariant(), path, StringComparison.Ordinal).Should().BeFalse();
            PathContainment.IsInside(Root.ToLowerInvariant(), path, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        }

        [TestCase(null, "/a/b")]
        [TestCase("/a", null)]
        [TestCase("/a", " ")]
        public void should_reject_missing_input(string folder, string path)
        {
            PathContainment.IsInside(folder, path).Should().BeFalse();
        }
    }
}
