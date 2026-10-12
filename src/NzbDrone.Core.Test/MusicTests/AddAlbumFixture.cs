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

        // Some OpenLibrary works are stubs with no edition records at all.
        [Test]
        public void should_add_a_book_the_source_lists_no_editions_for()
        {
            var newBook = BookToAdd("book", "book", "author");

            GivenValidBook("book", "edition");
            GivenValidPath();

            _fakeBook.Editions.Value.Clear();

            var book = Subject.AddBook(newBook);

            book.Editions.Value.Should().BeEmpty();
            Mocker.GetMock<IBookService>().Verify(s => s.AddBook(book, true), Times.Once());
        }

        [Test]
        public void should_not_throw_when_the_book_to_add_names_several_editions()
        {
            var newBook = BookToAdd("edition", "book", "author");
            newBook.Editions.Value.Add(new Edition { ForeignEditionId = "other", Monitored = true });

            GivenValidBook("book", "edition");
            GivenValidPath();

            var book = Subject.AddBook(newBook);

            book.Editions.Value.Where(x => x.Monitored).Select(x => x.ForeignEditionId).Should().Equal("edition");
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

        // An import list item from OpenLibrary names a work and no edition
        // at all, so there is nothing to match. It takes the same fallback.
        [Test]
        public void should_monitor_the_sources_pick_when_the_book_arrives_with_no_edition()
        {
            var newBook = BookToAdd("unused", "book", "author");
            newBook.Editions = new List<Edition>();

            GivenValidBook("book", "first-listed");
            GivenValidPath();

            _fakeBook.Editions.Value[0].Monitored = false;
            _fakeBook.Editions.Value.Add(new Edition { ForeignEditionId = "source-pick", Monitored = true });

            var book = Subject.AddBook(newBook);

            book.Editions.Value.Where(x => x.Monitored).Select(x => x.ForeignEditionId).Should().Equal("source-pick");
            book.Editions.Value.Single(x => x.Monitored).ManualAdd.Should().BeTrue();
        }

        [Test]
        public void should_add_every_book_that_arrives_with_no_edition()
        {
            var newBook = BookToAdd("unused", "book", "author");
            newBook.Editions = new List<Edition>();

            GivenValidBook("book", "first-listed");
            GivenValidPath();

            var added = Subject.AddBooks(new List<Book> { newBook }, false);

            added.Should().HaveCount(1);
            added[0].Editions.Value.Where(x => x.Monitored).Select(x => x.ForeignEditionId).Should().Equal("first-listed");

            Mocker.GetMock<IBookService>()
                .Verify(v => v.AddBook(newBook, false), Times.Once());
        }
    }
}
