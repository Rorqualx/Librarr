using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.SignalR;
using NzbDrone.Test.Common;
using Readarr.Api.V1.RootFolders;

namespace NzbDrone.Api.Test
{
    // Calibre roots hold ebooks only, so the API must not let a root folder
    // promise audiobooks it will then refuse to import.
    [TestFixture]
    public class RootFolderValidationFixture : TestBase
    {
        private const string AudiobookProfile = nameof(RootFolderResource.DefaultAudiobookQualityProfileId);

        private TestableRootFolderController _controller;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IRootFolderService>()
                  .Setup(s => s.All())
                  .Returns(new List<RootFolder>());

            Mocker.GetMock<IQualityProfileService>()
                  .Setup(s => s.Exists(It.IsAny<int>()))
                  .Returns(true);

            Mocker.GetMock<IMetadataProfileService>()
                  .Setup(s => s.Exists(It.IsAny<int>()))
                  .Returns(true);

            // The path rules run too; give them something to compare against.
            Mocker.GetMock<IAppFolderInfo>()
                  .SetupGet(s => s.StartUpFolder)
                  .Returns(@"C:\Librarr".AsOsAgnostic());

            Mocker.GetMock<IConfigService>()
                  .SetupGet(s => s.RecycleBin)
                  .Returns(string.Empty);

            _controller = Mocker.Resolve<TestableRootFolderController>();
        }

        private static RootFolderResource GivenRootFolder(bool isCalibreLibrary, int audiobookProfileId)
        {
            return new RootFolderResource
            {
                Name = "Books",
                Path = @"C:\Books".AsOsAgnostic(),
                DefaultQualityProfileId = 1,
                DefaultMetadataProfileId = 1,
                DefaultAudiobookQualityProfileId = audiobookProfileId,
                IsCalibreLibrary = isCalibreLibrary,
                Host = "localhost",
                Port = 8080,
                OutputProfile = "default"
            };
        }

        private List<ValidationFailure> AudiobookProfileErrors(RootFolderResource resource)
        {
            return _controller.ValidateShared(resource).Errors.Where(x => x.PropertyName == AudiobookProfile).ToList();
        }

        [Test]
        public void should_reject_an_audiobook_quality_profile_on_a_calibre_root()
        {
            AudiobookProfileErrors(GivenRootFolder(true, 2))
                .Should().ContainSingle()
                .Which.ErrorMessage.Should().Contain("Calibre libraries can only hold ebooks");
        }

        [Test]
        public void should_accept_a_calibre_root_with_no_audiobook_quality_profile()
        {
            AudiobookProfileErrors(GivenRootFolder(true, 0)).Should().BeEmpty();
        }

        [Test]
        public void should_accept_an_audiobook_quality_profile_on_an_ordinary_root()
        {
            AudiobookProfileErrors(GivenRootFolder(false, 2)).Should().BeEmpty();
        }

        public class TestableRootFolderController : RootFolderController
        {
            public TestableRootFolderController(IRootFolderService rootFolderService,
                                                ICalibreProxy calibreProxy,
                                                IBroadcastSignalRMessage signalRBroadcaster,
                                                RecycleBinValidator recycleBinValidator,
                                                RootFolderValidator rootFolderValidator,
                                                PathExistsValidator pathExistsValidator,
                                                MappedNetworkDriveValidator mappedNetworkDriveValidator,
                                                StartupFolderValidator startupFolderValidator,
                                                SystemFolderValidator systemFolderValidator,
                                                FolderWritableValidator folderWritableValidator,
                                                QualityProfileExistsValidator qualityProfileExistsValidator,
                                                MetadataProfileExistsValidator metadataProfileExistsValidator)
                : base(rootFolderService,
                       calibreProxy,
                       signalRBroadcaster,
                       recycleBinValidator,
                       rootFolderValidator,
                       pathExistsValidator,
                       mappedNetworkDriveValidator,
                       startupFolderValidator,
                       systemFolderValidator,
                       folderWritableValidator,
                       qualityProfileExistsValidator,
                       metadataProfileExistsValidator)
            {
            }

            public ValidationResult ValidateShared(RootFolderResource resource)
            {
                return SharedValidator.Validate(resource);
            }
        }
    }
}
