using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Books
{
    public interface IBookIdMappingRepository : IBasicRepository<BookIdMapping>
    {
        BookIdMapping FindBookByGoodreadsId(string goodreadsId);
        BookIdMapping FindAuthorByGoodreadsId(string goodreadsId);
        List<BookIdMapping> FindBooksByOpenLibraryWorkId(string openLibraryWorkId);
        BookIdMapping FindAuthorByOpenLibraryAuthorId(string openLibraryAuthorId);
        List<BookIdMapping> GetLowConfidence(double threshold);
    }

    public class BookIdMappingRepository : BasicRepository<BookIdMapping>, IBookIdMappingRepository
    {
        public BookIdMappingRepository(IMainDatabase database, IEventAggregator eventAggregator)
            : base(database, eventAggregator)
        {
        }

        // Author ids and work ids were separate number spaces on Goodreads,
        // so one GoodreadsId can legitimately have an author row and a book
        // row. There is deliberately no lookup by GoodreadsId alone: the
        // caller always knows which kind it is asking about, and answering
        // with the other kind is how a book came to be skipped as "already
        // mapped" by its author's row, and an author's row came to be
        // overwritten with a book's.
        //
        // The kind is decided in memory (BookIdMapping.IsBookMapping /
        // IsAuthorMapping) rather than in SQL, because "no work id" is
        // stored as NULL by reidentify and as an empty string by the review
        // page. A GoodreadsId has a couple of rows at most.
        public BookIdMapping FindBookByGoodreadsId(string goodreadsId)
        {
            return Preferred(Query(x => x.GoodreadsId == goodreadsId).Where(x => x.IsBookMapping())).FirstOrDefault();
        }

        public BookIdMapping FindAuthorByGoodreadsId(string goodreadsId)
        {
            return Preferred(Query(x => x.GoodreadsId == goodreadsId).Where(x => x.IsAuthorMapping())).FirstOrDefault();
        }

        public List<BookIdMapping> FindBooksByOpenLibraryWorkId(string openLibraryWorkId)
        {
            // Reverse lookup used by MetadataSourceFactory to restore the
            // local GoodReads-shaped ForeignBookId on returned remote books
            // so RefreshAuthorService can still correlate them to the
            // existing DB rows (which carry the GoodReads numeric id).
            //
            // A list, not a single row: OpenLibrary has merged works that
            // Goodreads kept apart, so several legacy books can map to one
            // work. Returning only the first hid the others, and the
            // refresh deleted them as no longer listed.
            return Preferred(Query(x => x.OpenLibraryWorkId == openLibraryWorkId).Where(x => x.IsBookMapping())).ToList();
        }

        public BookIdMapping FindAuthorByOpenLibraryAuthorId(string openLibraryAuthorId)
        {
            // Author mappings are written by ReidentifyService.MapAuthor
            // with no work id and the OL author OLID stuffed into
            // OpenLibraryEditionId (awkward column reuse, but that's
            // the convention the migration locked in). A book row's
            // OpenLibraryEditionId holds an edition id, which never has an
            // author id's shape, and IsAuthorMapping checks the work id is
            // empty as well.
            return Preferred(Query(x => x.OpenLibraryEditionId == openLibraryAuthorId).Where(x => x.IsAuthorMapping())).FirstOrDefault();
        }

        // Nothing stops two rows of one kind sharing an id (the table has
        // no unique index), and SingleOrDefault turned that into an
        // exception on every lookup. Pick one instead, and pick the same
        // one every time: the user's own choice, then the best match, then
        // the oldest row.
        private static IEnumerable<BookIdMapping> Preferred(IEnumerable<BookIdMapping> rows)
        {
            return rows.OrderByDescending(x => x.Source == BookIdMappingSource.Manual)
                .ThenByDescending(x => x.Confidence)
                .ThenBy(x => x.Id);
        }

        public List<BookIdMapping> GetLowConfidence(double threshold)
        {
            // Surfaced by the Phase 5 wizard's "needs manual review" step.
            return Query(x => x.Confidence < threshold);
        }
    }
}
