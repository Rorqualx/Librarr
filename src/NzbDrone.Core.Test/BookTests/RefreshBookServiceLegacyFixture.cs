using System;
using System.Collections.Generic;
using System.Net;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
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
        private Book _otherBook;

        [SetUp]
        public void Setup()
        {
            _author = Builder<Author>.CreateNew()
                .With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().Build())
                .Build();

            _book = Builder<Book>.CreateNew()
                .With(b => b.Id = 1)
                .With(b => b.ForeignBookId = "3275007")
                .With(b => b.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic })
                .Build();

            _otherBook = Builder<Book>.CreateNew()
                .With(b => b.Id = 2)
                .With(b => b.ForeignBookId = "3275008")
                .With(b => b.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic })
                .Build();

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(It.IsAny<int>()))
                .Returns(new List<BookFile>());

            Mocker.GetMock<IBookService>()
                .Setup(s => s.GetBook(_book.Id))
                .Returns(_book);
        }

        private void GivenUnmappedLegacyBook(bool unmapped)
        {
            Mocker.GetMock<IIdentifyUnmappedLegacyBooks>()
                .Setup(s => s.IsUnmappedLegacyBook(_book.ForeignBookId))
                .Returns(unmapped);
        }

        private void GivenBookSharesItsWorkWith(string foreignBookId)
        {
            Mocker.GetMock<IIdentifyUnmappedLegacyBooks>()
                .Setup(s => s.FindBookRefreshedInstead(_book.ForeignBookId))
                .Returns(foreignBookId);
        }

        private void GivenBookHasFiles()
        {
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(_book.Id))
                .Returns(new List<BookFile> { new BookFile { Id = 1 } });
        }

        private void GivenSourceFailsWith(Exception ex)
        {
            Mocker.GetMock<IProvideBookInfo>()
                .Setup(s => s.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()))
                .Throws(ex);
        }

        // What OpenLibrary answers for a work it has deleted or merged away.
        private static HttpException NotFound()
        {
            var request = new HttpRequest("https://openlibrary.invalid/works/OL1W.json");

            return new HttpException(new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.NotFound));
        }

        private static List<Book> Listed(params Book[] books)
        {
            var listed = new List<Book>();

            foreach (var book in books)
            {
                listed.Add(Builder<Book>.CreateNew().With(b => b.ForeignBookId = book.ForeignBookId).Build());
            }

            return listed;
        }

        private void VerifyNothingDeleted()
        {
            Mocker.GetMock<IBookService>()
                .Verify(s => s.DeleteBook(It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never());

            Mocker.GetMock<IBookService>()
                .Verify(s => s.DeleteMany(It.IsAny<List<Book>>()), Times.Never());
        }

        private void VerifyNothingSaved()
        {
            Mocker.GetMock<IBookService>()
                .Verify(s => s.UpdateMany(It.IsAny<List<Book>>()), Times.Never());
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

        // Kept, and with one line saying why rather than an error on every
        // refresh claiming the source may have removed it.
        [Test]
        public void should_keep_an_unmapped_legacy_book_and_not_ask_the_source_about_it()
        {
            GivenUnmappedLegacyBook(true);

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            VerifyNothingDeleted();
            VerifyNothingSaved();

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        // Two legacy books, one OpenLibrary work: the works list names the
        // work once, under the other book's id, so this one is never in it.
        [Test]
        public void should_keep_a_book_whose_work_is_listed_under_another_books_id()
        {
            GivenBookSharesItsWorkWith("3275001");

            Subject.RefreshBookInfo(_book, Listed(new Book { ForeignBookId = "3275001" }), _author, false);

            VerifyNothingDeleted();
            VerifyNothingSaved();

            // Fetching it would hand it the other book's editions.
            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_not_refresh_a_book_on_its_own_when_another_book_answers_for_its_work()
        {
            GivenBookSharesItsWorkWith("3275001");

            Subject.Execute(new RefreshBookCommand(_book.Id));

            VerifyNothingDeleted();

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_keep_a_listed_book_the_source_cannot_return_and_go_on_to_the_next()
        {
            GivenSourceFailsWith(NotFound());

            Subject.RefreshBookInfo(new List<Book> { _book, _otherBook }, Listed(_book, _otherBook), _author, true, false, null);

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(_book.ForeignBookId, It.IsAny<bool>()), Times.Once());

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(_otherBook.ForeignBookId, It.IsAny<bool>()), Times.Once());

            VerifyNothingDeleted();
            VerifyNothingSaved();

            ExceptionVerification.ExpectedWarns(2);
        }

        // An authorless or redirect work document fails in the mapper, not
        // with a status code.
        [Test]
        public void should_keep_a_listed_book_whose_record_cannot_be_read()
        {
            GivenSourceFailsWith(new NullReferenceException());

            Subject.RefreshBookInfo(_book, Listed(_book), _author, false);

            VerifyNothingDeleted();
            VerifyNothingSaved();

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_keep_an_unlisted_book_with_files_that_the_source_cannot_return()
        {
            GivenBookHasFiles();
            GivenSourceFailsWith(NotFound());

            Subject.RefreshBookInfo(_book, new List<Book>(), _author, false);

            VerifyNothingDeleted();

            ExceptionVerification.ExpectedWarns(1);
        }

        // Carrying on book by book against a source that is down fails
        // every one of them, slowly.
        [Test]
        public void should_stop_the_run_when_the_source_is_unavailable()
        {
            GivenSourceFailsWith(new MetadataSourceUnavailableException("Metadata source is unavailable"));

            Assert.Throws<MetadataSourceUnavailableException>(() =>
                Subject.RefreshBookInfo(new List<Book> { _book, _otherBook }, Listed(_book, _otherBook), _author, true, false, null));

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(_otherBook.ForeignBookId, It.IsAny<bool>()), Times.Never());

            VerifyNothingDeleted();
        }

        [Test]
        public void should_stop_the_run_when_the_source_is_refusing_requests()
        {
            var request = new HttpRequest("https://openlibrary.invalid/works/OL1W.json");
            var refused = new HttpException(new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.TooManyRequests));

            GivenSourceFailsWith(refused);

            Assert.Throws<HttpException>(() => Subject.RefreshBookInfo(_book, Listed(_book), _author, false));

            VerifyNothingDeleted();
        }

        // The Refresh Book command used to dereference the missing result.
        [Test]
        public void should_not_fail_or_delete_when_a_single_book_refresh_cannot_fetch_the_book()
        {
            GivenSourceFailsWith(NotFound());

            Assert.DoesNotThrow(() => Subject.Execute(new RefreshBookCommand(_book.Id)));

            VerifyNothingDeleted();
            VerifyNothingSaved();

            ExceptionVerification.ExpectedWarns(1);
        }

        // The source would name the author by an id the library does not
        // have, and the author would be added a second time.
        [Test]
        public void should_not_refresh_a_single_book_whose_legacy_author_has_no_confirmed_mapping()
        {
            _book.AuthorMetadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = "1825").Build();

            Mocker.GetMock<IIdentifyUnmappedLegacyBooks>()
                .Setup(s => s.IsUnmappedLegacyAuthor("1825"))
                .Returns(true);

            Subject.Execute(new RefreshBookCommand(_book.Id));

            Mocker.GetMock<IProvideBookInfo>()
                .Verify(s => s.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());

            Mocker.GetMock<IAddAuthorService>()
                .Verify(s => s.AddAuthor(It.IsAny<Author>(), It.IsAny<bool>()), Times.Never());
        }
    }
}
