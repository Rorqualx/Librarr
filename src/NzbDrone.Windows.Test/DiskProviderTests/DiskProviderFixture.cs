using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Test.DiskTests;
using NzbDrone.Windows.Disk;

namespace NzbDrone.Windows.Test.DiskProviderTests
{
    [TestFixture]
    [Platform("Win")]
    public class DiskProviderFixture : DiskProviderFixtureBase<DiskProvider>
    {
        public DiskProviderFixture()
        {
            WindowsOnly();
        }

        private static bool HasExplicitEveryoneRule(string path)
        {
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

            return new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, false, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Any(r => r.IdentityReference.Equals(everyone));
        }

        [Test]
        public void SetEveryonePermissions_should_grant_everyone_on_an_inheriting_folder()
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);

            Subject.SetEveryonePermissions(folder);

            HasExplicitEveryoneRule(folder).Should().BeTrue();
        }

        [Test]
        public void SetEveryonePermissions_should_leave_a_protected_acl_alone()
        {
            var folder = GetTempFilePath();
            Directory.CreateDirectory(folder);

            // What the installer does to the data folder: inheritance off,
            // existing entries kept as explicit ones.
            var directory = new DirectoryInfo(folder);
            var security = directory.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(true, true);
            directory.SetAccessControl(security);

            Subject.SetEveryonePermissions(folder);

            HasExplicitEveryoneRule(folder).Should().BeFalse();
        }

        protected override void SetWritePermissions(string path, bool writable)
        {
            // Remove Write permissions, we're owner and have Delete permissions, so we can still clean it up.
            var owner = WindowsIdentity.GetCurrent().Owner;
            var accessControlType = writable ? AccessControlType.Allow : AccessControlType.Deny;

            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var ds = directory.GetAccessControl();
                ds.SetAccessRule(new FileSystemAccessRule(owner, FileSystemRights.Write, accessControlType));
                directory.SetAccessControl(ds);
            }
            else
            {
                var file = new FileInfo(path);
                var fs = file.GetAccessControl();
                fs.SetAccessRule(new FileSystemAccessRule(owner, FileSystemRights.Write, accessControlType));
                file.SetAccessControl(fs);
            }
        }
    }
}
