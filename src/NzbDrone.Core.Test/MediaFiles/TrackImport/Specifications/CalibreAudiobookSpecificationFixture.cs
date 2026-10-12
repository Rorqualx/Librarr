using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles.BookImport.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.BookImport.Specifications
{
    [TestFixture]
    public class CalibreAudiobookSpecificationFixture : CoreTest<CalibreAudiobookSpecification>
    {
        private LocalBook _localBook;

        [SetUp]
        public void Setup()
        {
            _localBook = new LocalBook
            {
                Path = @"C:\Downloads\Book\Book.m4b".AsOsAgnostic(),
                Quality = new QualityModel(Quality.M4B),
                Author = Builder<Author>.CreateNew()
                    .With(a => a.Path = @"C:\Calibre\Author Name".AsOsAgnostic())
                    .Build()
            };
        }

        private void GivenRootFolder(bool isCalibreLibrary)
        {
            Mocker.GetMock<IRootFolderService>()
                  .Setup(s => s.GetBestRootFolder(_localBook.Author.Path))
                  .Returns(new RootFolder
                  {
                      Name = "Calibre",
                      Path = @"C:\Calibre".AsOsAgnostic(),
                      IsCalibreLibrary = isCalibreLibrary,
                      CalibreSettings = isCalibreLibrary ? new CalibreSettings() : null
                  });
        }

        [Test]
        public void should_reject_an_audiobook_bound_for_a_calibre_root()
        {
            GivenRootFolder(true);

            var result = Subject.IsSatisfiedBy(_localBook, null);

            result.Accepted.Should().BeFalse();
            result.Reason.Should().Contain("Calibre");
        }

        [Test]
        public void should_reject_an_audio_file_of_undetected_quality_bound_for_a_calibre_root()
        {
            GivenRootFolder(true);
            _localBook.Quality = new QualityModel(Quality.Unknown);

            Subject.IsSatisfiedBy(_localBook, null).Accepted.Should().BeFalse();
        }

        [Test]
        public void should_accept_an_ebook_bound_for_a_calibre_root()
        {
            GivenRootFolder(true);
            _localBook.Path = @"C:\Downloads\Book\Book.epub".AsOsAgnostic();
            _localBook.Quality = new QualityModel(Quality.EPUB);

            Subject.IsSatisfiedBy(_localBook, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_an_audiobook_bound_for_an_ordinary_root()
        {
            GivenRootFolder(false);

            Subject.IsSatisfiedBy(_localBook, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_a_file_already_in_the_library()
        {
            GivenRootFolder(true);
            _localBook.ExistingFile = true;

            Subject.IsSatisfiedBy(_localBook, null).Accepted.Should().BeTrue();
        }

        [Test]
        public void should_accept_when_the_author_has_no_path_yet()
        {
            _localBook.Author.Path = null;

            Subject.IsSatisfiedBy(_localBook, null).Accepted.Should().BeTrue();
        }
    }
}
