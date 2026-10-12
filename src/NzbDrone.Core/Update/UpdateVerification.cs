using System;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Update
{
    public interface IVerifyUpdates
    {
        bool Verify(UpdatePackage updatePackage, string packagePath);
    }

    public class UpdateVerification : IVerifyUpdates
    {
        private readonly IDiskProvider _diskProvider;

        public UpdateVerification(IDiskProvider diskProvider)
        {
            _diskProvider = diskProvider;
        }

        public bool Verify(UpdatePackage updatePackage, string packagePath)
        {
            // A package that arrives without a checksum has nothing to be
            // verified against, which is not the same as having passed.
            if (string.IsNullOrWhiteSpace(updatePackage.Hash))
            {
                return false;
            }

            using (var fileStream = _diskProvider.OpenReadStream(packagePath))
            {
                var hash = fileStream.SHA256Hash();

                return hash.Equals(updatePackage.Hash, StringComparison.CurrentCultureIgnoreCase);
            }
        }
    }
}
