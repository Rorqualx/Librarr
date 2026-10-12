using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MusicTests
{
    [TestFixture]
    public class AddBookFixture : CoreTest<AddBookService>
    {
        private Author _fakeAuthor;
        private Book _fakeBook;

        [SetUp]
        public void Setup()
        {
            _fakeAuthor = Builder<Author>
                .CreateNew()
                .With(s => s.Path = null)
                .With(s => s.Metadata = Builder<AuthorMetadata>.CreateNew().Build())
                .Build();
        }

        private void GivenValidBook(string bookId, string editionId)
        {
            _fakeBook = Builder<Book>
                .CreateNew()
                .With(x => x.Editions = Builder<Edition>
                      .CreateListOfSize(1)
                      .TheFirst(1)
                      .With(e => e.ForeignEditionId = editionId)
                      .With(e => e.Monitored = true)
                      .BuildList())
                .Build();

            Mocker.GetMock<IProvideBookInfo>()
                .Setup(s => s.GetBookInfo(bookId))
                .Returns(Tuple.Create(_fakeAuthor.Metadata.Value.ForeignAuthorId,
                                      _fakeBook,
                                      new List<AuthorMetadata> { _fakeAuthor.Metadata.Value }));

            Mocker.GetMock<IAddAuthorService>()
                .Setup(s => s.AddAuthor(It.IsAny<Author>(), It.IsAny<bool>()))
                .Returns(_fakeAuthor);
        }

        private void GivenValidPath()
        {
            Mocker.GetMock<IBuildFileNames>()
                  .Setup(s => s.GetAuthorFolder(It.IsAny<Author>(), null))
                  .Returns<Author, NamingConfig>((c, n) => c.Name);
        }

        private Book BookToAdd(string editionId, string bookId, string authorId)
        {
            return new Book
            {
                ForeignBookId = bookId,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = editionId,
                        Monitored = true
                    }
                },
                AuthorMetadata = new AuthorMetadata
                {
                    ForeignAuthorId = authorId
                }
            };
        }

        [Test]
        public void should_be_able_to_add_a_book_without_passing_in_name()
        {
            var newBook = BookToAdd("edition", "book", "author");

            GivenValidBook("book", "edition");
            GivenValidPath();

            var book = Subject.AddBook(newBook);

            book.Title.Should().Be(_fakeBook.Title);
        }

        [Test]
        public void should_throw_if_book_cannot_be_found()
        {
            var newBook = BookToAdd("edition", "book", "author");

            Mocker.GetMock<IProvideBookInfo>()
                  .Setup(s => s.GetBookInfo("book"))
                  .Throws(new BookNotFoundException("edition"));

            Assert.Throws<ValidationException>(() => Subject.AddBook(newBook));

            ExceptionVerification.ExpectedErrors(1);
        }

        // An OpenLibrary search result has no edition key, only the work's.
        // The book must start on the edition the source picked, not on
        // whichever edition it happens to list first.
        [Test]
        public void should_monitor_the_sources_pick_when_the_search_result_named_no_real_edition()
        {
            var newBook = BookToAdd("book", "book", "author");

            GivenValidBook("book", "first-listed");
            GivenValidPath();

            _fakeBook.Editions.Value[0].Monitored = false;
            _fakeBook.Editions.Value.Add(new Edition { ForeignEditionId = "source-pick", Monitored = true });

            var book = Subject.AddBook(newBook);

            book.Editions.Value.Where(x => x.Monitored).Select(x => x.ForeignEditionId).Should().Equal("source-pick");
        }

        [Test]
        public void should_monitor_the_edition_the_search_result_named()
        {
            var newBook = BookToAdd("first-listed", "book", "author");

            GivenValidBook("book", "first-listed");
            GivenValidPath();

            _fakeBook.Editions.Value[0].Monitored = false;
            _fakeBook.Editions.Value.Add(new Edition { ForeignEditionId = "source-pick", Monitored = true });

            var book = Subject.AddBook(newBook);

            book.Editions.Value.Where(x => x.Monitored).Select(x => x.ForeignEditionId).Should().Equal("first-listed");
        }
    }
}
