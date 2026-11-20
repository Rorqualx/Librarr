using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Qualities;
using Readarr.Api.V1.RootFolders;

// NUnit 5 deprecates [Order] in favour of DependsOnTest/DependsOnFixture.
// This fixture uses [Order] as loose STAGES -- several tests share one stage
// number -- not as a 1:1 dependency chain, so there is no mechanical
// conversion: each dependent would have to name one specific predecessor.
// That is a behaviour change too (a failed dependency SKIPS its dependents,
// altering what the nightly integration run reports), so it belongs in its
// own change. [Order] still works in NUnit 5; suppress the deprecation here.
#pragma warning disable CS0618

namespace NzbDrone.Integration.Test.ApiTests.WantedTests
{
    [TestFixture]
    public class CutoffUnmetFixture : IntegrationTest
    {
        [SetUp]
        public void Setup()
        {
            // Add a root folder
            RootFolders.Post(new RootFolderResource
            {
                Name = "TestLibrary",
                Path = AuthorRootFolder,
                DefaultMetadataProfileId = 1,
                DefaultQualityProfileId = 1,
                DefaultMonitorOption = MonitorTypes.All
            });
        }

        [Test]
        [Order(2)]
        public void cutoff_should_have_monitored_items()
        {
            EnsureProfileCutoff(1, Quality.AZW3, true);
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);
            var (bookId, editionId) = FirstImportableBook(author);
            EnsureBookFile(author, bookId, editionId, Quality.MOBI);

            var result = WantedCutoffUnmet.GetPaged(0, 15, "releaseDate", "desc");

            result.Records.Should().NotBeEmpty();
        }

        [Test]
        [Order(2)]
        public void cutoff_should_not_have_unmonitored_items()
        {
            EnsureProfileCutoff(1, Quality.AZW3, true);
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, false);
            var (bookId, editionId) = FirstImportableBook(author);
            EnsureBookFile(author, bookId, editionId, Quality.MOBI);

            var result = WantedCutoffUnmet.GetPaged(0, 15, "releaseDate", "desc");

            result.Records.Should().BeEmpty();
        }

        [Test]
        [Order(2)]
        public void cutoff_should_have_author()
        {
            EnsureProfileCutoff(1, Quality.AZW3, true);
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);
            var (bookId, editionId) = FirstImportableBook(author);
            EnsureBookFile(author, bookId, editionId, Quality.MOBI);

            var result = WantedCutoffUnmet.GetPagedIncludeAuthor(0, 15, "releaseDate", "desc", includeAuthor: true);

            result.Records.First().Author.Should().NotBeNull();
            result.Records.First().Author.AuthorName.Should().Be(OpenLibraryFixtureData.AndrewHunterMurrayName);
        }

        [Test]
        [Order(2)]
        public void cutoff_should_not_have_author()
        {
            EnsureProfileCutoff(1, Quality.AZW3, true);
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);
            var (bookId, editionId) = FirstImportableBook(author);
            EnsureBookFile(author, bookId, editionId, Quality.MOBI);

            var result = WantedCutoffUnmet.GetPagedIncludeAuthor(0, 15, "releaseDate", "desc", includeAuthor: false);

            result.Records.First().Author.Should().BeNull();
        }

        [Test]
        [Order(2)]
        public void cutoff_should_have_unmonitored_items()
        {
            EnsureProfileCutoff(1, Quality.AZW3, true);
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, false);
            var (bookId, editionId) = FirstImportableBook(author);
            EnsureBookFile(author, bookId, editionId, Quality.MOBI);

            var result = WantedCutoffUnmet.GetPaged(0, 15, "releaseDate", "desc", "monitored", false);

            result.Records.Should().NotBeEmpty();
        }
    }
}

#pragma warning restore CS0618
