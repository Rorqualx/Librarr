using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentAssertions.Equivalency;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests.BookRepositoryTests
{
    [TestFixture]
    public class BookRepositoryFixture : DbTest<BookService, Book>
    {
        private Author _author;
        private Book _book;
        private Book _bookSpecial;
        private List<Book> _books;
        private BookRepository _bookRepo;

        [SetUp]
        public void Setup()
        {
            AssertionOptions.AssertEquivalencyUsing(options =>
            {
                options.Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation.ToUniversalTime(), TimeSpan.FromMilliseconds(20))).WhenTypeIs<DateTime>();
                options.Using<DateTime?>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation.Value.ToUniversalTime(), TimeSpan.FromMilliseconds(20))).WhenTypeIs<DateTime?>();
                return options;
            });

            _author = new Author
            {
                Name = "Alien Ant Farm",
                Monitored = true,
                ForeignAuthorId = "this is a fake id",
                Id = 1,
                AuthorMetadataId = 1
            };

            _bookRepo = Mocker.Resolve<BookRepository>();

            _book = new Book
            {
                Title = "ANThology",
                ForeignBookId = "1",
                TitleSlug = "1-ANThology",
                CleanTitle = "anthology",
                Author = _author,
                AuthorMetadataId = _author.AuthorMetadataId,
            };

            _bookRepo.Insert(_book);
            _bookRepo.Update(_book);

            _bookSpecial = new Book
            {
                Title = "+",
                ForeignBookId = "2",
                TitleSlug = "2-_",
                CleanTitle = "",
                Author = _author,
                AuthorMetadataId = _author.AuthorMetadataId
            };

            _bookRepo.Insert(_bookSpecial);
        }

        [TestCase("ANThology")]
        [TestCase("anthology")]
        [TestCase("anthology!")]
        public void should_find_book_in_db_by_title(string title)
        {
            var book = _bookRepo.FindByTitle(_author.AuthorMetadataId, title);

            book.Should().NotBeNull();
            book.Title.Should().Be(_book.Title);
        }

        [Test]
        public void should_find_book_in_db_by_title_all_special_characters()
        {
            var book = _bookRepo.FindByTitle(_author.AuthorMetadataId, "+");

            book.Should().NotBeNull();
            book.Title.Should().Be(_bookSpecial.Title);
        }

        [Test]
        public void should_find_books_by_foreign_ids_in_one_call()
        {
            var books = _bookRepo.FindByIds(new List<string> { "1", "2", "not-in-the-library" });

            books.Select(b => b.Title).Should().BeEquivalentTo(_book.Title, _bookSpecial.Title);
        }

        [Test]
        public void should_find_books_by_foreign_ids_across_more_than_one_chunk()
        {
            // 1,200 ids is more than one chunk; the two real ones sit at
            // either end so each lands in a different statement.
            var ids = new List<string> { "1" };
            ids.AddRange(Enumerable.Range(1000, 1200).Select(i => i.ToString()));
            ids.Add("2");

            _bookRepo.FindByIds(ids).Should().HaveCount(2);
        }

        [Test]
        public void should_find_no_books_for_an_empty_id_list()
        {
            _bookRepo.FindByIds(new List<string>()).Should().BeEmpty();
        }

        [TestCase("ANTholog")]
        [TestCase("nthology")]
        [TestCase("antholoyg")]
        [TestCase("÷")]
        public void should_not_find_book_in_db_by_incorrect_title(string title)
        {
            var book = _bookRepo.FindByTitle(_author.AuthorMetadataId, title);

            book.Should().BeNull();
        }

        [Test]
        public void should_not_find_book_when_two_books_have_same_name()
        {
            var books = Builder<Book>.CreateListOfSize(2)
                .All()
                .With(x => x.Id = 0)
                .With(x => x.Author = _author)
                .With(x => x.AuthorMetadataId = _author.AuthorMetadataId)
                .With(x => x.Title = "Weezer")
                .With(x => x.CleanTitle = "weezer")
                .Build();

            _bookRepo.InsertMany(books);

            var book = _bookRepo.FindByTitle(_author.AuthorMetadataId, "Weezer");

            _bookRepo.All().Should().HaveCount(4);
            book.Should().BeNull();
        }

        private void GivenMultipleBooks()
        {
            _books = Builder<Book>.CreateListOfSize(4)
                .All()
                .With(x => x.Id = 0)
                .With(x => x.Author = _author)
                .With(x => x.AuthorMetadataId = _author.AuthorMetadataId)
                .TheFirst(1)

                // next
                .With(x => x.ReleaseDate = DateTime.UtcNow.AddDays(1))
                .TheNext(1)

                // another future one
                .With(x => x.ReleaseDate = DateTime.UtcNow.AddDays(2))
                .TheNext(1)

                // most recent
                .With(x => x.ReleaseDate = DateTime.UtcNow.AddDays(-1))
                .TheNext(1)

                // an older one
                .With(x => x.ReleaseDate = DateTime.UtcNow.AddDays(-2))
                .BuildList();

            _bookRepo.InsertMany(_books);
        }

        [Test]
        public void get_next_books_should_return_next_book()
        {
            GivenMultipleBooks();

            var result = _bookRepo.GetNextBooks(new[] { _author.AuthorMetadataId });
            result.Should().BeEquivalentTo(_books.Take(1), BookComparerOptions);
        }

        [Test]
        public void get_last_books_should_return_next_book()
        {
            GivenMultipleBooks();

            var result = _bookRepo.GetLastBooks(new[] { _author.AuthorMetadataId });
            result.Should().BeEquivalentTo(_books.Skip(2).Take(1), BookComparerOptions);
        }

        private EquivalencyAssertionOptions<Book> BookComparerOptions(EquivalencyAssertionOptions<Book> opts) => opts.ComparingByMembers<Book>()
                .Excluding(ctx => ctx.Type.IsGenericType && ctx.Type.GetGenericTypeDefinition() == typeof(LazyLoaded<>))
                .Excluding(x => x.AuthorId)
                .Excluding(x => x.ForeignEditionId);
    }
}
