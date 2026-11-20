using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Integration.Test.Client;
using Readarr.Api.V1.Books;
using RestSharp;

namespace NzbDrone.Integration.Test.ApiTests
{
    [TestFixture]
    public class CalendarFixture : IntegrationTest
    {
        public ClientBase<BookResource> Calendar;

        protected override void InitRestClients()
        {
            base.InitRestClients();

            Calendar = new ClientBase<BookResource>(RestClient, ApiKey, "calendar");
        }

        // These used to ask for February 2020 and expect "The Last Day", which
        // worked when Goodreads supplied an exact publication date. It cannot
        // work against OpenLibrary: none of this author's works carry a
        // first_publish_date at all, so Book.ReleaseDate falls back to an
        // edition's publish_date, and those are mostly bare years ("2020",
        // "2021", "2023") spread across seven editions of the same work.
        //
        // Pinning any fixed window would be a bet on which edition
        // OpenLibrary considers primary this month. What these tests are
        // actually for is the calendar endpoint's behaviour -- does a window
        // containing a book's release date return it, and is the unmonitored
        // flag respected -- so take the date from the book and build the
        // window around it. The assertion gets stronger, not weaker: it now
        // says "the calendar returns the book whose date is in range"
        // rather than "OpenLibrary agrees with Goodreads about February".
        private (DateTime Start, DateTime End, string Title) DatedBookWindow(int authorId)
        {
            var dated = Books.GetBooksInAuthor(authorId)
                .Where(b => b.ReleaseDate.HasValue)
                .OrderBy(b => b.ReleaseDate.Value)
                .ToList();

            dated.Should().NotBeEmpty(
                "the calendar can only be tested with a book that has a release date; " +
                "if this fails, OpenLibrary has stopped dating any of this author's books");

            var book = dated.First();
            var date = book.ReleaseDate.Value.Date;

            return (date.AddDays(-1), date.AddDays(1), book.Title);
        }

        private List<BookResource> GetCalendar(DateTime start, DateTime end, int authorId, string unmonitored = null)
        {
            var request = Calendar.BuildRequest();
            request.AddQueryParameter("start", start.ToString("s") + "Z");
            request.AddQueryParameter("end", end.ToString("s") + "Z");

            if (unmonitored != null)
            {
                request.AddQueryParameter("unmonitored", unmonitored);
            }

            return Calendar.Get<List<BookResource>>(request)
                .Where(v => v.AuthorId == authorId)
                .ToList();
        }

        [Test]
        public void should_be_able_to_get_books()
        {
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);

            var (start, end, title) = DatedBookWindow(author.Id);

            var items = GetCalendar(start, end, author.Id);

            items.Should().NotBeEmpty();
            items.Should().Contain(v => v.Title == title);
        }

        [Test]
        public void should_not_be_able_to_get_unmonitored_books()
        {
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);

            var (start, end, _) = DatedBookWindow(author.Id);

            // Unmonitor only after the window is known: an unmonitored author's
            // books still have to be readable to work out which dates to ask
            // for, and this is the state the assertion is about.
            EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, false);

            var items = GetCalendar(start, end, author.Id, "false");

            items.Should().BeEmpty();
        }

        [Test]
        public void should_be_able_to_get_unmonitored_books()
        {
            var author = EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, true);

            var (start, end, title) = DatedBookWindow(author.Id);

            EnsureAuthor(OpenLibraryFixtureData.AndrewHunterMurrayId, OpenLibraryFixtureData.AndrewHunterMurrayName, false);

            var items = GetCalendar(start, end, author.Id, "true");

            items.Should().NotBeEmpty();
            items.Should().Contain(v => v.Title == title);
        }
    }
}
