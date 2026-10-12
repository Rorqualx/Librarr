using System.Collections.Generic;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Books;

namespace NzbDrone.Api.Test.Books
{
    // PUT /api/v1/book/{id}/cover. The URL stored here is later fetched by the
    // server, so only an Open Library cover image may be pinned.
    [TestFixture]
    public class BookControllerCoverFixture : TestBase<BookController>
    {
        private Book _book;

        [SetUp]
        public void Setup()
        {
            _book = new Book
            {
                Id = 4,
                ForeignBookId = "OL27448W",
                PreferredCoverUrl = "https://covers.openlibrary.org/b/id/1-L.jpg",
                Editions = new List<Edition>()
            };

            Mocker.GetMock<IBookService>().Setup(s => s.GetBook(4)).Returns(_book);
        }

        [TestCase("http://127.0.0.1:8787/initialize.svg")]
        [TestCase("https://localhost/cover.svg")]
        [TestCase("https://example.com/cover.jpg")]
        [TestCase("http://covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.svg")]
        [TestCase("file:///etc/passwd")]
        public void should_reject_cover_url_that_is_not_an_open_library_image(string url)
        {
            Assert.Throws<ValidationException>(() => Subject.SetCover(4, new BookCoverPinResource { PreferredCoverUrl = url }));

            _book.PreferredCoverUrl.Should().Be("https://covers.openlibrary.org/b/id/1-L.jpg");

            Mocker.GetMock<IBookService>().Verify(s => s.UpdateBook(It.IsAny<Book>()), Times.Never());
        }

        [Test]
        public void should_store_open_library_cover_url()
        {
            const string url = "https://covers.openlibrary.org/b/id/8231856-L.jpg";

            Subject.SetCover(4, new BookCoverPinResource { PreferredCoverUrl = url });

            Mocker.GetMock<IBookService>().Verify(s => s.UpdateBook(It.Is<Book>(b => b.PreferredCoverUrl == url)), Times.Once());
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        public void should_clear_pin_when_no_url_is_given(string url)
        {
            Subject.SetCover(4, new BookCoverPinResource { PreferredCoverUrl = url });

            Mocker.GetMock<IBookService>().Verify(s => s.UpdateBook(It.Is<Book>(b => b.PreferredCoverUrl == null)), Times.Once());
        }

        [Test]
        public void should_clear_pin_when_body_is_missing()
        {
            Subject.SetCover(4, null);

            Mocker.GetMock<IBookService>().Verify(s => s.UpdateBook(It.Is<Book>(b => b.PreferredCoverUrl == null)), Times.Once());
        }
    }
}
