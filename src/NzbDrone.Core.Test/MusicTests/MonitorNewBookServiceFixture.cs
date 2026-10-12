using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class MonitorNewBookServiceFixture : CoreTest<MonitorNewBookService>
    {
        private List<Book> _books;

        [SetUp]
        public void Setup()
        {
            _books = Builder<Book>.CreateListOfSize(4)
                .All()
                .With(e => e.Monitored = true)
                .With(e => e.ReleaseDate = DateTime.UtcNow.AddDays(-7))

                //Future
                .TheFirst(1)
                .With(e => e.ReleaseDate = DateTime.UtcNow.AddDays(7))

                //Future/TBA
                .TheNext(1)
                .With(e => e.ReleaseDate = null)
                .Build()
                .ToList();
        }

        private static Book BookReleased(DateTime? releaseDate)
        {
            return Builder<Book>.CreateNew()
                .With(e => e.ReleaseDate = releaseDate)
                .Build();
        }

        // An author's catalogue as it sits in the database after a normal
        // OpenLibrary sync: most books dated by their per-book fetch, a few
        // that OL has no date for at all, and one still on the placeholder
        // because its fetch has not succeeded yet.
        private static List<Book> SyncedCatalogue()
        {
            return new List<Book>
            {
                BookReleased(new DateTime(2010, 8, 31, 0, 0, 0, DateTimeKind.Utc)),
                BookReleased(new DateTime(2020, 11, 17, 0, 0, 0, DateTimeKind.Utc)),
                BookReleased(null),
                BookReleased(PlaceholderReleaseDate.Value)
            };
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_monitor_with_all(bool authorPreviouslySynced)
        {
            foreach (var book in _books)
            {
                Subject.ShouldMonitorNewBook(book, _books, NewItemMonitorTypes.All, authorPreviouslySynced).Should().BeTrue();
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void should_not_monitor_with_none(bool authorPreviouslySynced)
        {
            foreach (var book in _books)
            {
                Subject.ShouldMonitorNewBook(book, _books, NewItemMonitorTypes.None, authorPreviouslySynced).Should().BeFalse();
            }
        }

        // The upstream test, for an author that has been synced before. One
        // expectation changed: the undated book (index 1, "TBA") was "not
        // new", because a missing date was compared as DateTime.MinValue. A
        // book that has just appeared with no date is now new by discovery.
        [Test]
        public void should_only_monitor_new_with_new()
        {
            Subject.ShouldMonitorNewBook(_books[0], _books, NewItemMonitorTypes.New, true).Should().BeTrue();
            Subject.ShouldMonitorNewBook(_books[1], _books, NewItemMonitorTypes.New, true).Should().BeTrue();

            foreach (var book in _books.Skip(2))
            {
                Subject.ShouldMonitorNewBook(book, _books, NewItemMonitorTypes.New, true).Should().BeFalse();
            }
        }

        // The defect. Every book OpenLibrary's works list turns up arrives on
        // the placeholder date, and as a date the placeholder is 1900: older
        // than any real book the author has, so never new. "Monitor new books"
        // could not fire.
        [Test]
        public void placeholder_dated_book_found_for_a_synced_author_should_be_new()
        {
            var discovered = BookReleased(PlaceholderReleaseDate.Value);

            Subject.ShouldMonitorNewBook(discovered, SyncedCatalogue(), NewItemMonitorTypes.New, true).Should().BeTrue();
        }

        [Test]
        public void undated_book_found_for_a_synced_author_should_be_new()
        {
            Subject.ShouldMonitorNewBook(BookReleased(null), SyncedCatalogue(), NewItemMonitorTypes.New, true).Should().BeTrue();
        }

        // ...but not on the first sync, where every book has "just appeared".
        [Test]
        public void first_sync_should_not_mark_the_back_catalogue_as_new()
        {
            var slimBooks = Enumerable.Range(0, 44).Select(i => BookReleased(PlaceholderReleaseDate.Value)).ToList();

            foreach (var book in slimBooks)
            {
                Subject.ShouldMonitorNewBook(book, new List<Book>(), NewItemMonitorTypes.New, false).Should().BeFalse();
            }

            Subject.ShouldMonitorNewBook(BookReleased(null), new List<Book>(), NewItemMonitorTypes.New, false).Should().BeFalse();
        }

        // Before: with no existing books to compare against, the newest
        // existing date was DateTime.MinValue and every dated book of a first
        // sync was "new".
        [Test]
        public void first_sync_should_not_mark_dated_past_books_as_new()
        {
            var published = BookReleased(new DateTime(1965, 8, 1, 0, 0, 0, DateTimeKind.Utc));

            Subject.ShouldMonitorNewBook(published, new List<Book>(), NewItemMonitorTypes.New, false).Should().BeFalse();
        }

        [TestCase(true)]
        [TestCase(false)]
        public void unreleased_book_should_always_be_new(bool authorPreviouslySynced)
        {
            var announced = BookReleased(DateTime.UtcNow.AddMonths(6));

            Subject.ShouldMonitorNewBook(announced, SyncedCatalogue(), NewItemMonitorTypes.New, authorPreviouslySynced).Should().BeTrue();
        }

        // What the second look is for (RefreshAuthorService.ReconsiderNewBooks):
        // once the per-book fetch has supplied a real date, the date decides.
        // OpenLibrary gains records for old books all the time.
        [Test]
        public void newly_found_book_with_an_old_real_date_should_not_be_new()
        {
            var reprint = BookReleased(new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            Subject.ShouldMonitorNewBook(reprint, SyncedCatalogue(), NewItemMonitorTypes.New, true).Should().BeFalse();
        }

        [Test]
        public void newly_found_book_newer_than_everything_dated_should_be_new()
        {
            var latest = BookReleased(new DateTime(2024, 12, 6, 0, 0, 0, DateTimeKind.Utc));

            Subject.ShouldMonitorNewBook(latest, SyncedCatalogue(), NewItemMonitorTypes.New, true).Should().BeTrue();
        }

        // The other half of the same mistake: placeholders among the
        // *existing* books must not be compared as dates either. Here they are
        // the only existing books, so there is no real date to be older than.
        [Test]
        public void existing_placeholders_should_not_count_as_the_newest_existing_date()
        {
            var unfetched = Enumerable.Range(0, 3).Select(i => BookReleased(PlaceholderReleaseDate.Value)).ToList();
            var dated = BookReleased(new DateTime(1850, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // 1850 is before the placeholder's 1900. It is still not "older
            // than the newest existing book", because none has a date.
            Subject.ShouldMonitorNewBook(dated, unfetched, NewItemMonitorTypes.New, true).Should().BeTrue();
        }

        [Test]
        public void placeholder_should_be_recognised_whatever_the_datetime_kind()
        {
            // Written as UTC, read back from the database as whatever the
            // provider says. The comparison is on the instant.
            var roundTripped = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

            PlaceholderReleaseDate.IsKnown(roundTripped).Should().BeFalse();
            PlaceholderReleaseDate.IsKnown(null).Should().BeFalse();
            PlaceholderReleaseDate.IsKnown(new DateTime(1900, 1, 2, 0, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        }
    }
}
