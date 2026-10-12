using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Metadata;

namespace NzbDrone.Api.Test.MetadataTests
{
    // Phase 9c API surface coverage. The interesting axes:
    //   * GET uses the configurable threshold (default 0.70)
    //   * Decoration joins book + author when they exist locally, in a
    //     fixed number of queries however many rows there are
    //   * Author rows (null work id, author OLID in the edition column)
    //     are reported as authors and can only be saved as authors
    //   * PUT normalises what a user pastes into a canonical id, rejects
    //     what it cannot, and forces Source=Manual + Confidence=1.0
    //   * PUT against a missing id returns NotFound
    [TestFixture]
    public class LowConfidenceMappingControllerFixture : TestBase<LowConfidenceMappingController>
    {
        [SetUp]
        public void Setup()
        {
            // An empty local library unless a test says otherwise.
            GivenBooks();
            GivenAuthors();
        }

        private static BookIdMapping BookRow(int id = 7, string goodreadsId = "gr-100")
        {
            return new BookIdMapping
            {
                Id = id,
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = "OL200W",
                OpenLibraryEditionId = "OL300M",
                Confidence = 0.55,
                Source = BookIdMappingSource.TitleAuthor
            };
        }

        // The shape ReidentifyService.MapAuthor writes.
        private static BookIdMapping AuthorRow(int id = 8, string goodreadsId = "gr-author-1")
        {
            return new BookIdMapping
            {
                Id = id,
                GoodreadsId = goodreadsId,
                OpenLibraryWorkId = null,
                OpenLibraryEditionId = "OL498120A",
                Confidence = 0.5,
                Source = BookIdMappingSource.TitleAuthor
            };
        }

        private static Author LocalAuthor(string name, int metadataId, string foreignAuthorId)
        {
            return new Author
            {
                Name = name,
                AuthorMetadataId = metadataId,
                ForeignAuthorId = foreignAuthorId
            };
        }

        private void GivenLowConfidenceRows(params BookIdMapping[] rows)
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(r => r.GetLowConfidence(It.IsAny<double>()))
                .Returns(rows.ToList());
        }

        private void GivenBooks(params Book[] books)
        {
            Mocker.GetMock<IBookService>()
                .Setup(s => s.FindByIds(It.IsAny<IEnumerable<string>>()))
                .Returns<IEnumerable<string>>(ids => books.Where(b => ids.Contains(b.ForeignBookId)).ToList());
        }

        private void GivenAuthors(params Author[] authors)
        {
            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.GetAuthorsByMetadataId(It.IsAny<IEnumerable<int>>()))
                .Returns<IEnumerable<int>>(ids => authors.Where(a => ids.Contains(a.AuthorMetadataId)).ToList());
            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.FindByIds(It.IsAny<IEnumerable<string>>()))
                .Returns<IEnumerable<string>>(ids => authors.Where(a => ids.Contains(a.ForeignAuthorId)).ToList());
        }

        private void GivenStored(BookIdMapping existing)
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(r => r.Get(existing.Id)).Returns(existing);
        }

        private void VerifyNothingSaved()
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.IsAny<BookIdMapping>()), Times.Never);
        }

        [Test]
        public void GetLowConfidenceMappings_should_pass_default_threshold_when_unspecified()
        {
            GivenLowConfidenceRows();

            Subject.GetLowConfidenceMappings();

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.GetLowConfidence(0.70), Times.Once);
        }

        [Test]
        public void GetLowConfidenceMappings_should_respect_explicit_threshold()
        {
            GivenLowConfidenceRows();

            Subject.GetLowConfidenceMappings(0.5);

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.GetLowConfidence(0.5), Times.Once);
        }

        [Test]
        public void GetLowConfidenceMappings_should_decorate_with_book_title_and_author_when_present()
        {
            GivenLowConfidenceRows(BookRow());
            GivenBooks(new Book { Id = 42, ForeignBookId = "gr-100", Title = "Foundation", AuthorMetadataId = 11 });
            GivenAuthors(LocalAuthor("Isaac Asimov", 11, "gr-author-9"));

            var result = Subject.GetLowConfidenceMappings();

            result.Should().HaveCount(1);
            result[0].Kind.Should().Be(LowConfidenceMappingKind.Book);
            result[0].BookId.Should().Be(42);
            result[0].BookTitle.Should().Be("Foundation");
            result[0].AuthorName.Should().Be("Isaac Asimov");
            result[0].OpenLibraryWorkId.Should().Be("OL200W");
            result[0].OpenLibraryEditionId.Should().Be("OL300M");
            result[0].OpenLibraryAuthorId.Should().BeNull();
            result[0].Source.Should().Be(BookIdMappingSource.TitleAuthor);
        }

        [Test]
        public void GetLowConfidenceMappings_should_still_render_row_when_book_was_deleted()
        {
            GivenLowConfidenceRows(BookRow(goodreadsId: "gr-orphan"));
            GivenBooks();
            GivenAuthors();

            var result = Subject.GetLowConfidenceMappings();

            result.Should().HaveCount(1);
            result[0].BookTitle.Should().BeNull();
            result[0].AuthorName.Should().BeNull();
            result[0].GoodreadsId.Should().Be("gr-orphan");
        }

        [Test]
        public void GetLowConfidenceMappings_should_report_an_author_row_as_an_author()
        {
            GivenLowConfidenceRows(AuthorRow());
            GivenBooks();
            GivenAuthors(LocalAuthor("Roald Dahl", 21, "gr-author-1"));

            var result = Subject.GetLowConfidenceMappings();

            result.Should().HaveCount(1);
            result[0].Kind.Should().Be(LowConfidenceMappingKind.Author);
            result[0].OpenLibraryAuthorId.Should().Be("OL498120A");
            result[0].AuthorName.Should().Be("Roald Dahl");

            // The author OLID lives in the edition column on disk. It must
            // not be offered to the client as an edition, or the client
            // will dutifully send it back as one.
            result[0].OpenLibraryEditionId.Should().BeNull();
            result[0].OpenLibraryWorkId.Should().BeNull();
            result[0].BookId.Should().BeNull();
            result[0].BookTitle.Should().BeNull();
        }

        [Test]
        public void GetLowConfidenceMappings_should_not_look_an_author_row_up_as_a_book()
        {
            // Goodreads numbers authors and books independently, so an
            // author id can equal an unrelated book's id.
            GivenLowConfidenceRows(AuthorRow(goodreadsId: "3345"));
            GivenBooks(new Book { Id = 42, ForeignBookId = "3345", Title = "Not This One", AuthorMetadataId = 11 });
            GivenAuthors(LocalAuthor("Joseph Conrad", 21, "3345"), LocalAuthor("Someone Else", 11, "999"));

            var result = Subject.GetLowConfidenceMappings();

            result[0].BookTitle.Should().BeNull();
            result[0].AuthorName.Should().Be("Joseph Conrad");
        }

        [Test]
        public void GetLowConfidenceMappings_should_treat_a_row_with_no_ids_at_all_as_a_book()
        {
            var row = BookRow();
            row.OpenLibraryWorkId = null;
            row.OpenLibraryEditionId = null;
            GivenLowConfidenceRows(row);
            GivenBooks();
            GivenAuthors();

            Subject.GetLowConfidenceMappings()[0].Kind.Should().Be(LowConfidenceMappingKind.Book);
        }

        [Test]
        public void GetLowConfidenceMappings_should_not_query_per_row()
        {
            var rows = Enumerable.Range(1, 40)
                .Select(i => BookRow(i, "gr-book-" + i))
                .Concat(Enumerable.Range(41, 10).Select(i => AuthorRow(i, "gr-author-" + i)))
                .ToArray();

            GivenLowConfidenceRows(rows);
            GivenBooks(Enumerable.Range(1, 40)
                .Select(i => new Book { Id = i, ForeignBookId = "gr-book-" + i, Title = "Book " + i, AuthorMetadataId = 100 + (i % 5) })
                .ToArray());
            GivenAuthors(Enumerable.Range(0, 5)
                .Select(i => LocalAuthor("Author " + i, 100 + i, "gr-other-" + i))
                .Concat(Enumerable.Range(41, 10).Select(i => LocalAuthor("Mapped " + i, 200 + i, "gr-author-" + i)))
                .ToArray());

            var result = Subject.GetLowConfidenceMappings();

            result.Should().HaveCount(50);
            result.Where(r => r.Kind == LowConfidenceMappingKind.Book).Should().OnlyContain(r => r.BookTitle != null && r.AuthorName != null);
            result.Where(r => r.Kind == LowConfidenceMappingKind.Author).Should().OnlyContain(r => r.AuthorName.StartsWith("Mapped"));

            Mocker.GetMock<IBookService>().Verify(s => s.FindByIds(It.IsAny<IEnumerable<string>>()), Times.Once);
            Mocker.GetMock<IBookService>().Verify(s => s.FindById(It.IsAny<string>()), Times.Never);
            Mocker.GetMock<IAuthorService>().Verify(s => s.GetAuthorsByMetadataId(It.IsAny<IEnumerable<int>>()), Times.Once);
            Mocker.GetMock<IAuthorService>().Verify(s => s.FindByIds(It.IsAny<IEnumerable<string>>()), Times.Once);
            Mocker.GetMock<IAuthorService>().Verify(s => s.GetAuthorByMetadataId(It.IsAny<int>()), Times.Never);
        }

        [Test]
        public void OverrideMapping_should_force_manual_source_and_confidence_1()
        {
            GivenStored(BookRow());

            var resource = new LowConfidenceMappingResource
            {
                Id = 7,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryEditionId = "OL7353617M",

                // Caller's confidence + source should be ignored — the
                // controller pins them. Set bogus values to prove it.
                Confidence = 0.12,
                Source = "BogusFromClient"
            };

            Subject.OverrideMapping(resource);

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.Is<BookIdMapping>(m =>
                    m.Id == 7 &&
                    m.OpenLibraryWorkId == "OL45804W" &&
                    m.OpenLibraryEditionId == "OL7353617M" &&
                    m.Confidence == 1.0 &&
                    m.Source == BookIdMappingSource.Manual)), Times.Once);
        }

        // The page's own help text says to paste "/works/OLxxxxW". Stored
        // verbatim, that fails the ^OL\d+W$ check every consumer applies,
        // while Source=Manual hides the row from review and from reidentify.
        [TestCase("/works/OL45804W")]
        [TestCase("  OL45804W  ")]
        [TestCase("ol45804w")]
        [TestCase("https://openlibrary.org/works/OL45804W/Fantastic_Mr_Fox?edition=key%3A/books/OL7353617M")]
        public void OverrideMapping_should_store_the_canonical_work_id(string pasted)
        {
            GivenStored(BookRow());

            Subject.OverrideMapping(new LowConfidenceMappingResource { Id = 7, OpenLibraryWorkId = pasted });

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.Is<BookIdMapping>(m => m.OpenLibraryWorkId == "OL45804W")), Times.Once);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Fantastic Mr Fox")]
        [TestCase("45804")]
        [TestCase("OL498120A")]
        [TestCase("/authors/OL498120A")]
        [TestCase("https://example.com/works/OL45804W")]
        public void OverrideMapping_should_reject_a_book_save_without_a_valid_work_id(string pasted)
        {
            var stored = BookRow();
            GivenStored(stored);

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource { Id = 7, OpenLibraryWorkId = pasted });

            save.Should().Throw<ValidationException>()
                .Which.Errors.Should().Contain(e => e.PropertyName == "openLibraryWorkId");
            VerifyNothingSaved();

            // Rejected means untouched, not half-applied.
            stored.OpenLibraryWorkId.Should().Be("OL200W");
            stored.Source.Should().Be(BookIdMappingSource.TitleAuthor);
            stored.Confidence.Should().Be(0.55);
        }

        [TestCase("/books/OL7353617M", "OL7353617M")]
        [TestCase(" ol7353617m ", "OL7353617M")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void OverrideMapping_should_store_the_canonical_edition_id(string pasted, string expected)
        {
            GivenStored(BookRow());

            Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 7,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryEditionId = pasted
            });

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.Is<BookIdMapping>(m => m.OpenLibraryEditionId == expected)), Times.Once);
        }

        [TestCase("9780142410349")]
        [TestCase("OL45804W")]
        [TestCase("OL498120A")]
        public void OverrideMapping_should_reject_an_edition_id_that_is_not_one(string pasted)
        {
            GivenStored(BookRow());

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 7,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryEditionId = pasted
            });

            save.Should().Throw<ValidationException>()
                .Which.Errors.Should().Contain(e => e.PropertyName == "openLibraryEditionId");
            VerifyNothingSaved();
        }

        [Test]
        public void OverrideMapping_should_keep_an_unconventional_stored_edition_id_the_user_did_not_touch()
        {
            // The form is pre-filled with what is stored and sends it
            // straight back. Reidentify does not guarantee an OL...M id in
            // this column, and correcting the work must not be blocked by
            // an edition value the user never typed.
            var stored = BookRow();
            stored.OpenLibraryEditionId = "OL200W";
            GivenStored(stored);

            Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 7,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryEditionId = "OL200W"
            });

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.Is<BookIdMapping>(m =>
                    m.OpenLibraryWorkId == "OL45804W" &&
                    m.OpenLibraryEditionId == "OL200W")), Times.Once);
        }

        [TestCase("OL34184A")]
        [TestCase("/authors/OL34184A")]
        [TestCase(" https://openlibrary.org/authors/ol34184a/Roald_Dahl ")]
        public void OverrideMapping_should_correct_an_author_row_without_giving_it_a_work_id(string pasted)
        {
            GivenStored(AuthorRow());

            var result = Subject.OverrideMapping(new LowConfidenceMappingResource { Id = 8, OpenLibraryAuthorId = pasted });

            Mocker.GetMock<IBookIdMappingRepository>()
                .Verify(r => r.Update(It.Is<BookIdMapping>(m =>
                    m.Id == 8 &&
                    m.OpenLibraryWorkId == null &&
                    m.OpenLibraryEditionId == "OL34184A" &&
                    m.Confidence == 1.0 &&
                    m.Source == BookIdMappingSource.Manual)), Times.Once);

            var saved = (LowConfidenceMappingResource)((AcceptedResult)result.Result).Value;
            saved.Kind.Should().Be(LowConfidenceMappingKind.Author);
            saved.OpenLibraryAuthorId.Should().Be("OL34184A");
        }

        [Test]
        public void OverrideMapping_should_reject_a_work_id_on_an_author_row()
        {
            // The exact request that corrupted a live row: a work id and a
            // blank edition, sent by a client that thought this was a book.
            var stored = AuthorRow();
            GivenStored(stored);

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 8,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryEditionId = string.Empty
            });

            save.Should().Throw<ValidationException>()
                .Which.Errors.Should().Contain(e => e.PropertyName == "openLibraryWorkId");
            VerifyNothingSaved();
            stored.OpenLibraryWorkId.Should().BeNull();
            stored.OpenLibraryEditionId.Should().Be("OL498120A");
        }

        [Test]
        public void OverrideMapping_should_reject_a_work_id_on_an_author_row_even_with_a_valid_author_id()
        {
            GivenStored(AuthorRow());

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 8,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryAuthorId = "OL34184A"
            });

            save.Should().Throw<ValidationException>();
            VerifyNothingSaved();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Roald Dahl")]
        [TestCase("OL45804W")]
        [TestCase("/works/OL45804W")]
        [TestCase("OL7353617M")]
        public void OverrideMapping_should_reject_an_author_save_without_a_valid_author_id(string pasted)
        {
            GivenStored(AuthorRow());

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource { Id = 8, OpenLibraryAuthorId = pasted });

            save.Should().Throw<ValidationException>()
                .Which.Errors.Should().Contain(e => e.PropertyName == "openLibraryAuthorId");
            VerifyNothingSaved();
        }

        [Test]
        public void OverrideMapping_should_reject_an_author_id_on_a_book_row()
        {
            GivenStored(BookRow());

            Action save = () => Subject.OverrideMapping(new LowConfidenceMappingResource
            {
                Id = 7,
                OpenLibraryWorkId = "OL45804W",
                OpenLibraryAuthorId = "OL34184A"
            });

            save.Should().Throw<ValidationException>()
                .Which.Errors.Should().Contain(e => e.PropertyName == "openLibraryAuthorId");
            VerifyNothingSaved();
        }

        [Test]
        public void OverrideMapping_should_return_NotFound_for_missing_id()
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(r => r.Get(99)).Returns((BookIdMapping)null);

            var result = Subject.OverrideMapping(new LowConfidenceMappingResource { Id = 99 });

            result.Result.Should().BeOfType<NotFoundResult>();
            VerifyNothingSaved();
        }
    }
}
