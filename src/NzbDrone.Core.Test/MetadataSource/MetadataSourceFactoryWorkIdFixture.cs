using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MetadataSource
{
    // The cover picker talks to OpenLibraryProxy directly, so it needs the work
    // id a locally stored book id stands for. A book migrated from Readarr
    // still has its numeric Goodreads id.
    [TestFixture]
    public class MetadataSourceFactoryWorkIdFixture : CoreTest<MetadataSourceFactory>
    {
        private void GivenMapping(string goodreadsId, string workId)
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                  .Setup(r => r.FindByGoodreadsId(goodreadsId))
                  .Returns(new BookIdMapping { GoodreadsId = goodreadsId, OpenLibraryWorkId = workId });
        }

        [Test]
        public void should_return_open_library_work_id_unchanged()
        {
            Subject.GetOpenLibraryWorkId("OL27448W").Should().Be("OL27448W");

            Mocker.GetMock<IBookIdMappingRepository>()
                  .Verify(r => r.FindByGoodreadsId(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_translate_migrated_goodreads_id()
        {
            GivenMapping("18423", "OL59800W");

            Subject.GetOpenLibraryWorkId("18423").Should().Be("OL59800W");
        }

        [Test]
        public void should_return_null_for_goodreads_id_without_a_mapping()
        {
            Subject.GetOpenLibraryWorkId("18423").Should().BeNull();
        }

        [TestCase("")]
        [TestCase("not-a-work-id")]
        [TestCase(null)]
        public void should_return_null_when_mapping_has_no_usable_work_id(string mappedWorkId)
        {
            GivenMapping("18423", mappedWorkId);

            Subject.GetOpenLibraryWorkId("18423").Should().BeNull();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void should_return_null_for_missing_id(string foreignBookId)
        {
            Subject.GetOpenLibraryWorkId(foreignBookId).Should().BeNull();
        }
    }
}
