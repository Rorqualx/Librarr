using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    // Migration-043 service tests. Three axes:
    //   1. Idempotent overwrite — DeleteByEditionId always fires before insert.
    //   2. Dedup by CleanName (case-insensitive) — duplicate aliases collapse.
    //   3. Existing Narrator rows are reused (no duplicate inserts).
    [TestFixture]
    public class NarratorServiceFixture : CoreTest<NarratorService>
    {
        // StyleCop SA1107 forbids the obvious `n => { n.Id = …; return n; }`
        // lambda body on one line. Pre-baking it as a delegate keeps each
        // Setup readable without splitting the brace block per call site.
        private static System.Func<Narrator, Narrator> InsertWithId(int id) =>
            n =>
            {
                n.Id = id;
                return n;
            };

        [Test]
        public void SetNarratorsForEdition_should_skip_when_edition_id_is_zero()
        {
            Subject.SetNarratorsForEdition(0, new[] { "George Guidall" });

            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.DeleteByEditionId(It.IsAny<int>()), Times.Never);
            Mocker.GetMock<INarratorRepository>()
                .Verify(r => r.Insert(It.IsAny<Narrator>()), Times.Never);
        }

        [Test]
        public void SetNarratorsForEdition_should_always_clear_join_rows_first()
        {
            // Even when the incoming name list is empty, the wipe still
            // happens — that's how the "no narrators on this edition any
            // more" case propagates.
            Subject.SetNarratorsForEdition(42, new List<string>());

            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.DeleteByEditionId(42), Times.Once);
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.IsAny<IList<EditionNarrator>>()), Times.Never);
        }

        [Test]
        public void SetNarratorsForEdition_should_insert_new_narrator_when_clean_name_missing()
        {
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.FindByCleanName(It.IsAny<string>()))
                .Returns((Narrator)null);
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.Insert(It.IsAny<Narrator>()))
                .Returns<Narrator>(InsertWithId(100));

            Subject.SetNarratorsForEdition(7, new[] { "George Guidall" });

            Mocker.GetMock<INarratorRepository>()
                .Verify(r => r.Insert(It.Is<Narrator>(n => n.Name == "George Guidall")), Times.Once);
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.Is<IList<EditionNarrator>>(rows =>
                    rows.Count == 1 &&
                    rows[0].EditionId == 7 &&
                    rows[0].NarratorId == 100 &&
                    rows[0].Order == 0)), Times.Once);
        }

        [Test]
        public void SetNarratorsForEdition_should_reuse_existing_narrator_when_clean_name_matches()
        {
            // Existing narrator in the DB; new edition references it.
            // Should not call Insert on the narrator repo.
            var existing = new Narrator { Id = 5, Name = "George Guidall", CleanName = "georgeguidall" };
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.FindByCleanName(It.IsAny<string>()))
                .Returns(existing);

            Subject.SetNarratorsForEdition(8, new[] { "George Guidall" });

            Mocker.GetMock<INarratorRepository>()
                .Verify(r => r.Insert(It.IsAny<Narrator>()), Times.Never);
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.Is<IList<EditionNarrator>>(rows =>
                    rows.Count == 1 && rows[0].NarratorId == 5)), Times.Once);
        }

        [Test]
        public void SetNarratorsForEdition_should_dedup_by_clean_name_case_insensitive()
        {
            // "George Guidall" and "george guidall" should collapse to one row.
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.FindByCleanName(It.IsAny<string>()))
                .Returns((Narrator)null);
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.Insert(It.IsAny<Narrator>()))
                .Returns<Narrator>(InsertWithId(100));

            Subject.SetNarratorsForEdition(9, new[] { "George Guidall", "george guidall" });

            // Exactly one Narrator row, exactly one join row.
            Mocker.GetMock<INarratorRepository>()
                .Verify(r => r.Insert(It.IsAny<Narrator>()), Times.Once);
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.Is<IList<EditionNarrator>>(rows => rows.Count == 1)), Times.Once);
        }

        [Test]
        public void SetNarratorsForEdition_should_skip_null_and_blank_names()
        {
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.FindByCleanName(It.IsAny<string>()))
                .Returns((Narrator)null);
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.Insert(It.IsAny<Narrator>()))
                .Returns<Narrator>(InsertWithId(100));

            Subject.SetNarratorsForEdition(10, new[] { "Real Person", null, "  ", string.Empty });

            Mocker.GetMock<INarratorRepository>()
                .Verify(r => r.Insert(It.Is<Narrator>(n => n.Name == "Real Person")), Times.Once);
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.Is<IList<EditionNarrator>>(rows => rows.Count == 1)), Times.Once);
        }

        [Test]
        public void SetNarratorsForEdition_should_preserve_order_for_multi_cast()
        {
            // Dual-cast title — the Order column matters for credited billing.
            // Both Insert calls return id=100; the test asserts on Order, not
            // NarratorId, so distinct ids aren't required here.
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.FindByCleanName(It.IsAny<string>()))
                .Returns((Narrator)null);
            Mocker.GetMock<INarratorRepository>()
                .Setup(r => r.Insert(It.IsAny<Narrator>()))
                .Returns<Narrator>(InsertWithId(100));

            Subject.SetNarratorsForEdition(11, new[] { "George Guidall", "Frank Muller" });

            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.InsertMany(It.Is<IList<EditionNarrator>>(rows =>
                    rows.Count == 2 &&
                    rows[0].Order == 0 &&
                    rows[1].Order == 1)), Times.Once);
        }

        // Phase 12.4 — backing service for the per-narrator detail page.
        // The walk is EditionNarrators → Editions → Books, deduped by
        // BookId so a narrator who appears on the abridged + unabridged
        // editions of the same book still shows up once.
        [Test]
        public void GetBooksForNarrator_should_return_empty_when_id_is_zero()
        {
            var result = Subject.GetBooksForNarrator(0);

            result.Should().NotBeNull();
            result.Should().BeEmpty();
            Mocker.GetMock<IEditionNarratorRepository>()
                .Verify(r => r.FindByNarratorId(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void GetBooksForNarrator_should_return_empty_when_no_join_rows()
        {
            Mocker.GetMock<IEditionNarratorRepository>()
                .Setup(r => r.FindByNarratorId(42))
                .Returns(new List<EditionNarrator>());

            var result = Subject.GetBooksForNarrator(42);

            result.Should().NotBeNull();
            result.Should().BeEmpty();
            Mocker.GetMock<IEditionService>()
                .Verify(s => s.FindEditions(It.IsAny<IEnumerable<int>>()), Times.Never);
            Mocker.GetMock<IBookService>()
                .Verify(s => s.GetBooks(It.IsAny<IEnumerable<int>>()), Times.Never);
        }

        [Test]
        public void GetBooksForNarrator_should_dedup_books_across_editions()
        {
            // Narrator 5 appears on editions 100 and 101, both of which
            // belong to book 7. The result should pass `[7]` to
            // IBookService.GetBooks — not `[7, 7]`.
            Mocker.GetMock<IEditionNarratorRepository>()
                .Setup(r => r.FindByNarratorId(5))
                .Returns(new List<EditionNarrator>
                {
                    new EditionNarrator { EditionId = 100, NarratorId = 5 },
                    new EditionNarrator { EditionId = 101, NarratorId = 5 }
                });

            Mocker.GetMock<IEditionService>()
                .Setup(s => s.FindEditions(It.Is<IEnumerable<int>>(ids =>
                    ids.OrderBy(id => id).SequenceEqual(new[] { 100, 101 }))))
                .Returns(new List<Edition>
                {
                    new Edition { Id = 100, BookId = 7 },
                    new Edition { Id = 101, BookId = 7 }
                });

            var expected = new List<Book> { new Book { Id = 7 } };
            Mocker.GetMock<IBookService>()
                .Setup(s => s.GetBooks(It.Is<IEnumerable<int>>(ids =>
                    ids.Count() == 1 && ids.First() == 7)))
                .Returns(expected);

            var result = Subject.GetBooksForNarrator(5);

            result.Should().BeEquivalentTo(expected);
            Mocker.GetMock<IBookService>()
                .Verify(s => s.GetBooks(It.Is<IEnumerable<int>>(ids => ids.Count() == 1)), Times.Once);
        }

        [Test]
        public void GetBooksForNarrator_should_skip_missing_editions()
        {
            // Join row 200 outlived its edition. The lookup is a single
            // set query that simply returns no row for it, so the
            // surviving book still comes back. GetEdition must not be
            // used here: it throws ModelNotFoundException for a missing
            // id rather than returning null.
            Mocker.GetMock<IEditionNarratorRepository>()
                .Setup(r => r.FindByNarratorId(5))
                .Returns(new List<EditionNarrator>
                {
                    new EditionNarrator { EditionId = 100, NarratorId = 5 },
                    new EditionNarrator { EditionId = 200, NarratorId = 5 }
                });

            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEdition(200))
                .Throws(new ModelNotFoundException(typeof(Edition), 200));
            Mocker.GetMock<IEditionService>()
                .Setup(s => s.FindEditions(It.Is<IEnumerable<int>>(ids =>
                    ids.OrderBy(id => id).SequenceEqual(new[] { 100, 200 }))))
                .Returns(new List<Edition> { new Edition { Id = 100, BookId = 7 } });

            Mocker.GetMock<IBookService>()
                .Setup(s => s.GetBooks(It.Is<IEnumerable<int>>(ids =>
                    ids.Count() == 1 && ids.First() == 7)))
                .Returns(new List<Book> { new Book { Id = 7 } });

            var result = Subject.GetBooksForNarrator(5);

            result.Should().HaveCount(1);
            result[0].Id.Should().Be(7);
            Mocker.GetMock<IEditionService>()
                .Verify(s => s.GetEdition(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void GetBooksForNarrator_should_return_empty_when_every_edition_is_missing()
        {
            Mocker.GetMock<IEditionNarratorRepository>()
                .Setup(r => r.FindByNarratorId(5))
                .Returns(new List<EditionNarrator>
                {
                    new EditionNarrator { EditionId = 200, NarratorId = 5 }
                });

            Mocker.GetMock<IEditionService>()
                .Setup(s => s.FindEditions(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Edition>());

            var result = Subject.GetBooksForNarrator(5);

            result.Should().BeEmpty();
            Mocker.GetMock<IBookService>()
                .Verify(s => s.GetBooks(It.IsAny<IEnumerable<int>>()), Times.Never);
        }
    }
}
