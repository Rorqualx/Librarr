using System.Collections.Generic;
using System.Collections.Specialized;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Processes;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.CustomScript;
using NzbDrone.Core.Notifications.Goodreads;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.NotificationTests
{
    // A book has no editions until its first per-book fetch, and can end up
    // with none monitored. A notification about such a book goes out without
    // the edition's details; it does not throw and take the rest of the
    // event's handlers with it.
    internal static class BookWithoutEdition
    {
        public static Author Author()
        {
            return new Author
            {
                Id = 1,
                Path = "/books/author",
                Metadata = new AuthorMetadata { Name = "Author", ForeignAuthorId = "OL1A" }
            };
        }

        public static Book Book()
        {
            return new Book
            {
                Id = 2,
                Title = "Book",
                ForeignBookId = "OL2W",
                AuthorMetadata = new AuthorMetadata { Name = "Author", ForeignAuthorId = "OL1A" },
                Editions = new List<Edition>()
            };
        }

        public static BookDownloadMessage DownloadMessage()
        {
            return new BookDownloadMessage
            {
                Author = Author(),
                Book = Book(),
                BookFiles = new List<BookFile>(),
                OldFiles = new List<BookFile>()
            };
        }
    }

    [TestFixture]
    public class WebhookBookFixture
    {
        [Test]
        public void should_leave_the_edition_out_when_the_book_has_none()
        {
            var webhookBook = new WebhookBook(BookWithoutEdition.Book());

            webhookBook.Title.Should().Be("Book");
            webhookBook.GoodreadsId.Should().Be("OL2W");
            webhookBook.Edition.Should().BeNull();
        }

        [Test]
        public void should_describe_the_monitored_edition()
        {
            var book = BookWithoutEdition.Book();
            book.Editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "OL1M" },
                new Edition { Id = 2, ForeignEditionId = "OL2M", Monitored = true }
            };

            new WebhookBook(book).Edition.GoodreadsId.Should().Be("OL2M");
        }

        [Test]
        public void should_not_throw_when_several_editions_are_monitored()
        {
            var book = BookWithoutEdition.Book();
            book.Editions = new List<Edition>
            {
                new Edition { Id = 1, ForeignEditionId = "OL1M", Monitored = true },
                new Edition { Id = 2, ForeignEditionId = "OL2M", Monitored = true }
            };

            new WebhookBook(book).Edition.GoodreadsId.Should().Be("OL2M");
        }
    }

    [TestFixture]
    public class CustomScriptFixture : CoreTest<CustomScript>
    {
        private StringDictionary _environment;

        [SetUp]
        public void Setup()
        {
            _environment = null;

            Subject.Definition = new NotificationDefinition
            {
                Settings = new CustomScriptSettings { Path = "/scripts/notify.sh" }
            };

            Mocker.GetMock<IProcessProvider>()
                .Setup(s => s.StartAndCapture(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StringDictionary>()))
                .Callback((string path, string args, StringDictionary environment) => _environment = environment)
                .Returns(new ProcessOutput());
        }

        [Test]
        public void should_run_the_script_on_grab_when_a_book_has_no_editions()
        {
            var withEdition = BookWithoutEdition.Book();
            withEdition.Editions = new List<Edition> { new Edition { ForeignEditionId = "OL9M", Monitored = true } };

            Subject.OnGrab(new GrabMessage
            {
                Author = BookWithoutEdition.Author(),
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book> { BookWithoutEdition.Book(), withEdition },
                    Release = new ReleaseInfo { Title = "Author - Book" },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) }
                }
            });

            // One slot per book, so the ids still line up with Readarr_Release_BookIds.
            _environment["Readarr_Release_GRIds"].Should().Be("|OL9M");
            _environment["Readarr_Release_BookIds"].Should().Be("2|2");
        }

        [Test]
        public void should_run_the_script_on_import_when_the_book_has_no_editions()
        {
            Subject.OnReleaseImport(BookWithoutEdition.DownloadMessage());

            _environment["Readarr_Book_Title"].Should().Be("Book");
            _environment["Readarr_Book_GRId"].Should().BeEmpty();
        }

        [Test]
        public void should_run_the_script_on_retag_when_the_book_has_no_editions()
        {
            Subject.OnBookRetag(new BookRetagMessage
            {
                Author = BookWithoutEdition.Author(),
                Book = BookWithoutEdition.Book(),
                BookFile = new BookFile { Id = 3, Path = "/books/author/book.epub", Quality = new QualityModel(Quality.EPUB) }
            });

            _environment["Readarr_Book_Title"].Should().Be("Book");
            _environment["Readarr_Book_GRId"].Should().BeEmpty();
        }
    }

    [TestFixture]
    public class GoodreadsOwnedBooksFixture : CoreTest<GoodreadsOwnedBooks>
    {
        [Test]
        public void should_not_report_a_book_that_has_no_editions()
        {
            Subject.Definition = new NotificationDefinition { Settings = new GoodreadsOwnedBooksNotificationSettings() };

            Subject.OnReleaseImport(BookWithoutEdition.DownloadMessage());

            Mocker.GetMock<IHttpClient>().Verify(v => v.Execute(It.IsAny<HttpRequest>()), Times.Never());
        }
    }

    [TestFixture]
    public class GoodreadsBookshelfFixture : CoreTest<GoodreadsBookshelf>
    {
        [Test]
        public void should_not_shelve_a_book_that_has_no_editions()
        {
            Subject.Definition = new NotificationDefinition
            {
                Settings = new GoodreadsBookshelfNotificationSettings
                {
                    RemoveIds = new List<string>(),
                    AddIds = new[] { "read" }
                }
            };

            Subject.OnReleaseImport(BookWithoutEdition.DownloadMessage());

            Mocker.GetMock<IHttpClient>().Verify(v => v.Execute(It.IsAny<HttpRequest>()), Times.Never());
        }
    }
}
