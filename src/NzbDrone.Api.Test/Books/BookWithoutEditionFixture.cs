using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Books;
using Readarr.Api.V1.Calendar;
using Readarr.Api.V1.ManualImport;
using Readarr.Api.V1.Search;

namespace NzbDrone.Api.Test.Books
{
    // A book has no editions until its first per-book fetch, can have none
    // monitored, and can briefly have several. None of those is a reason for
    // an endpoint that mentions the book to answer 500.
    internal static class BookWithoutEdition
    {
        public static Author Author()
        {
            return new Author
            {
                Id = 1,
                Metadata = new AuthorMetadata { Name = "Author", ForeignAuthorId = "OL1A" }
            };
        }

        public static Book Book()
        {
            var author = Author();

            return new Book
            {
                Id = 4,
                Title = "Book",
                ForeignBookId = "OL4W",
                ReleaseDate = DateTime.UtcNow,
                Author = author,
                AuthorMetadata = author.Metadata.Value,
                Editions = new List<Edition>()
            };
        }
    }

    [TestFixture]
    public class BookControllerOverviewFixture : TestBase<BookController>
    {
        private static string OverviewOf(object response)
        {
            return (string)response.GetType().GetProperty("overview").GetValue(response);
        }

        [Test]
        public void should_return_an_empty_overview_for_a_book_without_editions()
        {
            Mocker.GetMock<IEditionService>().Setup(s => s.GetEditionsByBook(4)).Returns(new List<Edition>());

            OverviewOf(Subject.Overview(4)).Should().BeEmpty();
        }

        [Test]
        public void should_return_the_overview_of_the_monitored_edition()
        {
            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEditionsByBook(4))
                .Returns(new List<Edition>
                {
                    new Edition { Id = 1, Overview = "other" },
                    new Edition { Id = 2, Overview = "monitored", Monitored = true }
                });

            OverviewOf(Subject.Overview(4)).Should().Be("monitored");
        }

        [Test]
        public void should_return_an_overview_when_several_editions_are_monitored()
        {
            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEditionsByBook(4))
                .Returns(new List<Edition>
                {
                    new Edition { Id = 1, Overview = "first", Monitored = true },
                    new Edition { Id = 2, Overview = "second", Monitored = true }
                });

            OverviewOf(Subject.Overview(4)).Should().Be("second");
        }
    }

    [TestFixture]
    public class BookResourceMapperFixture : TestBase
    {
        [Test]
        public void should_map_a_book_without_editions()
        {
            var resource = BookWithoutEdition.Book().ToResource();

            resource.Title.Should().Be("Book");
            resource.ForeignEditionId.Should().BeNull();
            resource.Images.Should().BeEmpty();
        }

        [Test]
        public void should_map_a_book_with_several_monitored_editions()
        {
            var book = BookWithoutEdition.Book();
            book.Editions = new List<Edition>
            {
                new Edition { Id = 1, Title = "First", ForeignEditionId = "OL1M", Monitored = true },
                new Edition { Id = 2, Title = "Second", ForeignEditionId = "OL2M", Monitored = true }
            };

            var resource = book.ToResource();

            resource.Title.Should().Be("Second");
            resource.ForeignEditionId.Should().Be("OL2M");
        }
    }

    [TestFixture]
    public class CalendarFeedControllerFixture : TestBase<CalendarFeedController>
    {
        [Test]
        public void should_list_a_book_without_editions()
        {
            var book = BookWithoutEdition.Book();

            Mocker.GetMock<IBookService>()
                .Setup(s => s.BooksBetweenDates(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<bool>()))
                .Returns(new List<Book> { book });

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.GetAuthor(It.IsAny<int>()))
                .Returns(BookWithoutEdition.Author());

            var feed = (ContentResult)Subject.GetCalendarFeed();

            feed.Content.Should().Contain("UID:Readarr_book_4");
            feed.Content.Should().Contain("SUMMARY:Author - Book");
            feed.Content.Should().NotContain("DESCRIPTION");
        }
    }

    [TestFixture]
    public class SearchControllerFixture : TestBase<SearchController>
    {
        [Test]
        public void should_return_a_book_without_editions()
        {
            Mocker.GetMock<ISearchForNewEntity>()
                .Setup(s => s.SearchForNewEntity("book"))
                .Returns(new List<object> { BookWithoutEdition.Book() });

            var results = ((IEnumerable<SearchResource>)Subject.Search("book")).ToList();

            results.Should().HaveCount(1);
            results[0].Book.Title.Should().Be("Book");
            results[0].Book.Overview.Should().BeNull();
        }
    }

    [TestFixture]
    public class ManualImportResourceMapperFixture : TestBase
    {
        [Test]
        public void should_map_an_item_whose_book_has_no_editions()
        {
            var item = new ManualImportItem
            {
                Path = "/downloads/book.epub",
                Author = BookWithoutEdition.Author(),
                Book = BookWithoutEdition.Book(),
                Quality = new QualityModel(Quality.EPUB)
            };

            var resource = item.ToResource();

            resource.Book.Title.Should().Be("Book");
            resource.ForeignEditionId.Should().BeNull();
        }
    }
}
