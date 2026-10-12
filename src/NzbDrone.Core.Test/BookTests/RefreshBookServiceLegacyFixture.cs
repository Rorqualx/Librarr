using System.Collections.Generic;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class RefreshBookServiceLegacyFixture : CoreTest<RefreshBookService>
    {
        private Author _author;
        private Book _book;

        [SetUp]
        public void Setup()
        {
            _author = Builder<Author>.CreateNew()
                .With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().Build())
                .Build();

            _book = Builder<Book>.CreateNew()
                .With(b => b.ForeignBookId = "3275007")
                .With(b => b.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic })
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(_book.Id))
                .Returns(new List<BookFile>());
        }

        private void GivenUnmappedLegacyBook(bool unmapped)
        {
            Mocker.GetMock<IIdentifyUnmappedLegacyBooks>()
                .Setup(s => s.IsUnmappedLegacyBook(_book.ForeignBookId))
                .Returns(unmapped);
        }

        [Test]
        public void should_delete_a_fileless_book_the_source_no_longer_lists()
        {
            GivenUnmappedLegacyBook(false);

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            Mocker.GetMock<IBookService>()
                .Verify(s => s.DeleteBook(_book.Id, It.IsAny<bool>(), It.IsAny<bool>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_keep_an_unmapped_legacy_book_and_not_ask_the_source_about_it()
        {
            GivenUnmappedLegacyBook(true);

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            Mocker.GetMock<IBookService>()
                .Verify(s => s.DeleteBook(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(It.IsAny<string>()), Times.Never());

            ExceptionVerification.ExpectedErrors(1);
        }
    }
}
