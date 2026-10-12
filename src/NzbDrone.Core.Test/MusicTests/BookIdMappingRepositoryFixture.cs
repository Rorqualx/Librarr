using System;
using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    // ReidentifyServiceFixture mocks this repository, so until this fixture
    // the migration-041 table had never been through a real database. As a
    // DbTest it runs on SQLite locally and on PostgreSQL in the
    // "Unit tests (PostgreSQL)" job.
    [TestFixture]
    public class BookIdMappingRepositoryFixture : DbTest<BookIdMappingRepository, BookIdMapping>
    {
        private static readonly DateTime Created = new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc);

        private BookIdMapping _book;
        private BookIdMapping _author;
        private BookIdMapping _lowConfidence;

        [SetUp]
        public void Setup()
        {
            _book = new BookIdMapping
            {
                GoodreadsId = "5907",
                OpenLibraryWorkId = "OL262758W",
                OpenLibraryEditionId = "OL26331930M",
                Confidence = 1.0,
                Source = BookIdMappingSource.Isbn,
                CreatedUtc = Created
            };

            // Author rows carry the author OLID in OpenLibraryEditionId and
            // no work id; see BookIdMappingRepository.FindByOpenLibraryAuthorId.
            _author = new BookIdMapping
            {
                GoodreadsId = "656983",
                OpenLibraryWorkId = null,
                OpenLibraryEditionId = "OL26320A",
                Confidence = 0.9,
                Source = BookIdMappingSource.TitleAuthor,
                CreatedUtc = Created
            };

            _lowConfidence = new BookIdMapping
            {
                GoodreadsId = "33",
                OpenLibraryWorkId = "OL27448W",
                OpenLibraryEditionId = null,
                Confidence = 0.4,
                Source = BookIdMappingSource.TitleAuthor,
                CreatedUtc = Created
            };

            Subject.InsertMany(new List<BookIdMapping> { _book, _author, _lowConfidence });
        }

        [Test]
        public void should_find_book_mapping_by_goodreads_id()
        {
            Subject.FindBookByGoodreadsId("5907").OpenLibraryWorkId.Should().Be("OL262758W");
            Subject.FindBookByGoodreadsId("nope").Should().BeNull();
        }

        [Test]
        public void should_find_by_open_library_work_id()
        {
            Subject.FindBooksByOpenLibraryWorkId("OL27448W").Should().ContainSingle()
                .Which.GoodreadsId.Should().Be("33");
            Subject.FindBooksByOpenLibraryWorkId("nope").Should().BeEmpty();
        }

        [Test]
        public void should_find_author_mapping_only_where_work_id_is_null()
        {
            Subject.FindAuthorByOpenLibraryAuthorId("OL26320A").GoodreadsId.Should().Be("656983");

            // A book row with the same value in OpenLibraryEditionId is not
            // an author mapping.
            Subject.FindAuthorByOpenLibraryAuthorId("OL26331930M").Should().BeNull();
        }

        [Test]
        public void should_get_low_confidence_mappings()
        {
            Subject.GetLowConfidence(0.5).Should().ContainSingle()
                .Which.GoodreadsId.Should().Be("33");
        }

        [Test]
        public void should_store_fractional_confidence()
        {
            Subject.FindAuthorByGoodreadsId("656983").Confidence.Should().BeApproximately(0.9, 0.0001);
        }

        [Test]
        public void should_round_trip_created_utc_as_the_same_instant()
        {
            // The column was created as `timestamp without time zone` on
            // PostgreSQL. The server converts the UTC instant it is sent to
            // its own zone's wall clock before dropping the zone, so on any
            // server not running in UTC this came back shifted by the offset.
            var stored = Subject.FindBookByGoodreadsId("5907");

            stored.CreatedUtc.ToUniversalTime().Should().Be(Created);
        }
    }
}
