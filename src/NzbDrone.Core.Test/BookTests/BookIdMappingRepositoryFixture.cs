using System;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.BookTests
{
    // Goodreads numbered authors and works separately, so author 3389 and
    // work 3389 are different things with the same GoodreadsId. The table
    // tells them apart only by which OpenLibrary columns are filled in.
    [TestFixture]
    public class BookIdMappingRepositoryFixture : DbTest<BookIdMappingRepository, BookIdMapping>
    {
        private const string SharedId = "3389";
        private const string AuthorOlid = "OL19981A";
        private const string WorkOlid = "OL81626W";

        private static BookIdMapping AuthorRow(string goodreadsId = SharedId, string olid = AuthorOlid, double confidence = 0.90, string workId = null)
        {
            return new BookIdMapping
            {
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = workId,
                OpenLibraryEditionId = olid,
                Confidence = confidence,
                Source = BookIdMappingSource.TitleAuthor,
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static BookIdMapping BookRow(string goodreadsId = SharedId, string workId = WorkOlid, double confidence = 0.95, string source = BookIdMappingSource.Isbn)
        {
            return new BookIdMapping
            {
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = workId,
                OpenLibraryEditionId = "OL7353617M",
                Confidence = confidence,
                Source = source,
                CreatedUtc = DateTime.UtcNow
            };
        }

        [Test]
        public void should_keep_an_author_and_a_book_with_the_same_goodreads_id_apart()
        {
            Subject.Insert(AuthorRow());
            Subject.Insert(BookRow());

            Subject.FindAuthorByGoodreadsId(SharedId).OpenLibraryEditionId.Should().Be(AuthorOlid);
            Subject.FindBookByGoodreadsId(SharedId).OpenLibraryWorkId.Should().Be(WorkOlid);
        }

        [Test]
        public void should_not_answer_for_a_book_with_its_authors_row()
        {
            Subject.Insert(AuthorRow());

            Subject.FindBookByGoodreadsId(SharedId).Should().BeNull();
        }

        [Test]
        public void should_not_answer_for_an_author_with_a_books_row()
        {
            Subject.Insert(BookRow());

            Subject.FindAuthorByGoodreadsId(SharedId).Should().BeNull();
        }

        // The review page saves the work id of an author row back as an
        // empty string, not null.
        [Test]
        public void should_still_see_an_author_row_saved_with_an_empty_work_id()
        {
            Subject.Insert(AuthorRow(workId: string.Empty));

            Subject.FindAuthorByGoodreadsId(SharedId).Should().NotBeNull();
            Subject.FindAuthorByOpenLibraryAuthorId(AuthorOlid).Should().NotBeNull();
            Subject.FindBookByGoodreadsId(SharedId).Should().BeNull();
        }

        [Test]
        public void should_find_an_author_by_olid_and_never_a_book()
        {
            Subject.Insert(AuthorRow());
            Subject.Insert(BookRow(goodreadsId: "77", workId: "OL1W"));

            Subject.FindAuthorByOpenLibraryAuthorId(AuthorOlid).GoodreadsId.Should().Be(SharedId);
            Subject.FindAuthorByOpenLibraryAuthorId("OL7353617M").Should().BeNull();
        }

        [Test]
        public void should_return_every_book_mapped_to_a_work_best_first()
        {
            Subject.Insert(BookRow(goodreadsId: "3275003", confidence: 0.85, source: BookIdMappingSource.TitleAuthor));
            Subject.Insert(BookRow(goodreadsId: "3275001", confidence: 0.95));
            Subject.Insert(BookRow(goodreadsId: "3275009", workId: "OL2W"));

            Subject.FindBooksByOpenLibraryWorkId(WorkOlid)
                .Select(x => x.GoodreadsId)
                .Should().Equal("3275001", "3275003");
        }

        [Test]
        public void should_prefer_the_users_own_mapping_over_a_better_scored_one()
        {
            Subject.Insert(BookRow(goodreadsId: "3275001", confidence: 0.97, source: BookIdMappingSource.FileTag));
            Subject.Insert(BookRow(goodreadsId: "3275003", confidence: 1.0, source: BookIdMappingSource.Manual));
            Subject.Insert(BookRow(goodreadsId: "3275005", confidence: 1.0, source: BookIdMappingSource.Isbn));

            Subject.FindBooksByOpenLibraryWorkId(WorkOlid)
                .Select(x => x.GoodreadsId)
                .Should().Equal("3275003", "3275005", "3275001");
        }

        // Nothing in the table stops this, and SingleOrDefault made every
        // lookup of the id throw once it had happened.
        [Test]
        public void should_not_throw_when_two_rows_of_one_kind_share_an_id()
        {
            Subject.Insert(BookRow(workId: "OL1W", confidence: 0.55));
            Subject.Insert(BookRow(workId: "OL2W", confidence: 0.95));

            Subject.FindBookByGoodreadsId(SharedId).OpenLibraryWorkId.Should().Be("OL2W");
        }
    }
}
