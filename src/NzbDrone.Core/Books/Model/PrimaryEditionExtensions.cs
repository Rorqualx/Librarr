using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Books
{
    public static class PrimaryEditionExtensions
    {
        // The edition to use for a book wherever one edition has to stand
        // for it: its title for a search, its id in a notification, its
        // overview on the calendar.
        //
        // That is meant to be "the monitored one", and the callers used to
        // say so with Single(x => x.Monitored). But exactly one monitored
        // edition is an intention, not a guarantee, and Single throws in
        // each of the three states that break it:
        //
        //   * no editions at all - a book has none until its first per-book
        //     fetch, and some OpenLibrary works never get any;
        //   * editions but none monitored;
        //   * several monitored, until FixMultipleMonitoredEditions next runs.
        //
        // So: the monitored edition; of several, the one with the highest id,
        // which is the one that housekeeper keeps; with none monitored, the
        // edition with the lowest id; and null only when there are no
        // editions. Editions not yet saved all have id 0 and keep the order
        // they were listed in. Every caller has to decide what null means
        // for it.
        public static Edition PrimaryEdition(this IEnumerable<Edition> editions)
        {
            var all = editions?.Where(x => x != null).ToList();

            if (all == null || all.Count == 0)
            {
                return null;
            }

            var monitored = all.Where(x => x.Monitored).ToList();

            return monitored.Any()
                ? monitored.OrderByDescending(x => x.Id).First()
                : all.OrderBy(x => x.Id).First();
        }

        public static Edition PrimaryEdition(this Book book)
        {
            return book?.Editions?.Value.PrimaryEdition();
        }
    }
}
