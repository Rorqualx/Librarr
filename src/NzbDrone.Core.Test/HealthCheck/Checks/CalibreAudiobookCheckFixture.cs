using System.Collections.Generic;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.HealthCheck.Checks
{
    [TestFixture]
    public class CalibreAudiobookCheckFixture : CoreTest<CalibreAudiobookCheck>
    {
        private static readonly string CalibrePath = @"C:\Calibre".AsOsAgnostic();
        private static readonly string BooksPath = @"C:\Books".AsOsAgnostic();

        private RootFolder _calibreRoot;
        private RootFolder _ordinaryRoot;
        private List<Author> _authors;

        [SetUp]
        public void Setup()
        {
            _calibreRoot = new RootFolder { Name = "Calibre", Path = CalibrePath, IsCalibreLibrary = true, CalibreSettings = new CalibreSettings() };
            _ordinaryRoot = new RootFolder { Name = "Books", Path = BooksPath };
            _authors = new List<Author>();

            Mocker.GetMock<IRootFolderService>()
                  .Setup(s => s.All())
                  .Returns(() => new List<RootFolder> { _calibreRoot, _ordinaryRoot });

            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.GetAllAuthors())
                  .Returns(() => _authors);
        }

        private void GivenAuthor(string rootPath, int audiobookQualityProfileId)
        {
            var author = Builder<Author>.CreateNew()
                .With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().With(m => m.Name = "Author Name").Build())
                .With(a => a.Path = System.IO.Path.Combine(rootPath, "Author Name"))
                .With(a => a.AudiobookQualityProfileId = audiobookQualityProfileId)
                .Build();

            _authors.Add(author);
        }

        [Test]
        public void should_be_ok_when_nothing_asks_for_audiobooks_in_calibre()
        {
            _ordinaryRoot.DefaultAudiobookQualityProfileId = 2;
            GivenAuthor(CalibrePath, 0);
            GivenAuthor(BooksPath, 2);

            Subject.Check().ShouldBeOk();
        }

        [Test]
        public void should_be_ok_without_asking_for_authors_when_there_is_no_calibre_root()
        {
            _calibreRoot.IsCalibreLibrary = false;

            Subject.Check().ShouldBeOk();

            Mocker.GetMock<IAuthorService>().Verify(s => s.GetAllAuthors(), Times.Never());
        }

        [Test]
        public void should_warn_when_a_calibre_root_has_an_audiobook_quality_profile()
        {
            _calibreRoot.DefaultAudiobookQualityProfileId = 2;

            Subject.Check().ShouldBeWarning("root folder(s): Calibre");
        }

        [Test]
        public void should_warn_when_an_author_in_a_calibre_root_has_an_audiobook_quality_profile()
        {
            GivenAuthor(CalibrePath, 2);

            Subject.Check().ShouldBeWarning("Author Name");
        }
    }
}
