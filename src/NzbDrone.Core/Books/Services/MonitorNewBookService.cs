using System;
using System.Collections.Generic;
using System.Linq;
using NLog;

namespace NzbDrone.Core.Books
{
    public interface IMonitorNewBookService
    {
        // authorPreviouslySynced: whether the author had completed a full
        // refresh before the one that turned this book up. It is what tells a
        // work that has newly appeared at the source from the back catalogue
        // arriving for the first time.
        bool ShouldMonitorNewBook(Book addedBook, List<Book> existingBooks, NewItemMonitorTypes monitorNewItems, bool authorPreviouslySynced);
    }

    public class MonitorNewBookService : IMonitorNewBookService
    {
        private readonly Logger _logger;

        public MonitorNewBookService(Logger logger)
        {
            _logger = logger;
        }

        public bool ShouldMonitorNewBook(Book addedBook, List<Book> existingBooks, NewItemMonitorTypes monitorNewItems, bool authorPreviouslySynced)
        {
            if (monitorNewItems == NewItemMonitorTypes.None)
            {
                return false;
            }

            if (monitorNewItems == NewItemMonitorTypes.All)
            {
                return true;
            }

            if (monitorNewItems == NewItemMonitorTypes.New)
            {
                return IsNew(addedBook, existingBooks, authorPreviouslySynced);
            }

            throw new NotImplementedException($"Unknown new item monitor type {monitorNewItems}");
        }

        // What "new" means for an author set to monitor new books only.
        //
        // This used to be one comparison: the added book's release date
        // against the newest existing book's, with a missing date standing in
        // as DateTime.MinValue. That only works when books arrive dated. From
        // OpenLibrary they do not -- the author's works list carries no dates,
        // so a book is discovered with the placeholder (PlaceholderReleaseDate)
        // and learns its real date from a per-book fetch afterwards. Compared
        // as a date, the placeholder is 1 January 1900: older than everything,
        // new to nobody. The option could not fire, and because the answer was
        // saved before the real date arrived, it never got a second look.
        //
        // So the decision is made on what is actually known, and
        // RefreshAuthorService asks again once the per-book fetch has run:
        //
        //   The date is known and in the future
        //       New. An unreleased book is what the option is for, whatever
        //       else the author has announced.
        //
        //   The date is known, and the author was already synced
        //       New if it is at least as recent as the newest existing book
        //       with a real date -- the original rule, now comparing only
        //       dates that are dates. A newly catalogued 1985 reprint is not
        //       new just because OpenLibrary added the record this week.
        //
        //   The date is not known, and the author was already synced
        //       New. The work has just appeared at the source for an author
        //       whose list we had in full; that it was not there before is
        //       the one thing known about it. If the per-book fetch then
        //       produces a date, the rule above takes over and may withdraw
        //       this.
        //
        //   The author is being synced for the first time
        //       Not new, unless dated in the future. Every book of a first
        //       sync has "just appeared", and the whole back catalogue is not
        //       a new release. (What to monitor on first add is the Monitor
        //       option's business; BookMonitoredService applies it once the
        //       author has been scanned.)
        private bool IsNew(Book addedBook, List<Book> existingBooks, bool authorPreviouslySynced)
        {
            if (!PlaceholderReleaseDate.IsKnown(addedBook.ReleaseDate))
            {
                _logger.Trace("{0} has no known release date; new only by discovery, author previously synced: {1}", addedBook, authorPreviouslySynced);

                return authorPreviouslySynced;
            }

            var released = addedBook.ReleaseDate.Value;

            if (released > DateTime.UtcNow)
            {
                return true;
            }

            if (!authorPreviouslySynced)
            {
                return false;
            }

            // No existing book with a real date leaves nothing to be older
            // than, which was also the answer before.
            var newest = existingBooks
                .Where(x => PlaceholderReleaseDate.IsKnown(x.ReleaseDate))
                .Select(x => x.ReleaseDate.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            return released >= newest;
        }
    }
}
