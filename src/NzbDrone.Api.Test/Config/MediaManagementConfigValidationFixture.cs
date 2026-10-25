using System;
using System.Linq;
using FluentAssertions;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Config;

namespace NzbDrone.Api.Test.Config
{
    // Issue #96: a relative recycle bin path reached FolderWritableValidator,
    // whose disk call throws on an invalid path, so the API returned a 500
    // instead of the "invalid path" validation error.
    [TestFixture]
    public class MediaManagementConfigValidationFixture : TestBase
    {
        private TestableMediaManagementConfigController _controller;

        [SetUp]
        public void Setup()
        {
            // The real DiskProvider guards with Ensure.IsValidPath and throws.
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FolderWritable(It.IsAny<string>()))
                  .Throws(new ArgumentException("not a valid path", "path"));

            _controller = Mocker.Resolve<TestableMediaManagementConfigController>();
        }

        private static MediaManagementConfigResource GivenRecycleBin(string recycleBin)
        {
            return new MediaManagementConfigResource
            {
                RecycleBin = recycleBin,
                MinimumFreeSpaceWhenImporting = 100
            };
        }

        [TestCase("relative/bin")]
        [TestCase("bin")]
        public void should_fail_validation_not_throw_for_relative_recycle_bin(string recycleBin)
        {
            var errors = _controller.ValidateShared(GivenRecycleBin(recycleBin)).Errors
                                    .Where(e => e.PropertyName == nameof(MediaManagementConfigResource.RecycleBin))
                                    .ToList();

            errors.Should().ContainSingle();
            Mocker.GetMock<IDiskProvider>().Verify(s => s.FolderWritable(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_not_validate_an_empty_recycle_bin()
        {
            _controller.ValidateShared(GivenRecycleBin(string.Empty)).Errors
                       .Should().NotContain(e => e.PropertyName == nameof(MediaManagementConfigResource.RecycleBin));
        }

        public class TestableMediaManagementConfigController : MediaManagementConfigController
        {
            public TestableMediaManagementConfigController(IConfigService configService,
                                                           PathExistsValidator pathExistsValidator,
                                                           FolderChmodValidator folderChmodValidator,
                                                           FolderWritableValidator folderWritableValidator,
                                                           AuthorPathValidator authorPathValidator,
                                                           StartupFolderValidator startupFolderValidator,
                                                           SystemFolderValidator systemFolderValidator,
                                                           RootFolderAncestorValidator rootFolderAncestorValidator,
                                                           RootFolderValidator rootFolderValidator)
                : base(configService,
                       pathExistsValidator,
                       folderChmodValidator,
                       folderWritableValidator,
                       authorPathValidator,
                       startupFolderValidator,
                       systemFolderValidator,
                       rootFolderAncestorValidator,
                       rootFolderValidator)
            {
            }

            public ValidationResult ValidateShared(MediaManagementConfigResource resource)
            {
                return SharedValidator.Validate(resource);
            }
        }
    }
}
