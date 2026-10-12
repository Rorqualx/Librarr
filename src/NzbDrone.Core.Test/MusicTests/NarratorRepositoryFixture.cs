using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    // NarratorServiceFixture mocks both repositories, so until this fixture
    // nothing ran the migration-043 tables through a real database. They are
    // worth running for one reason in particular: EditionNarrators has a
    // column called "Order", a reserved word on both engines, so every
    // statement that names it has to quote it. As a DbTest this runs on
    // SQLite locally and on PostgreSQL in the "Unit tests (PostgreSQL)" job.
    [TestFixture]
    public class NarratorRepositoryFixture : DbTest<NarratorRepository, Narrator>
    {
        private EditionNarratorRepository _joinRepo;
        private List<Narrator> _narrators;

        [SetUp]
        public void Setup()
        {
            _joinRepo = Mocker.Resolve<EditionNarratorRepository>();

            _narrators = new List<Narrator>
            {
                new Narrator { Name = "George Guidall", CleanName = "georgeguidall", ForeignNarratorId = "B000APZOQA" },
                new Narrator { Name = "Frank Muller", CleanName = "frankmuller" },
                new Narrator { Name = "Kate Reading", CleanName = "katereading" }
            };

            Subject.InsertMany(_narrators);
        }

        private void GivenJoinRows(int editionId, params Narrator[] narrators)
        {
            _joinRepo.InsertMany(narrators
                .Select((n, i) => new EditionNarrator { EditionId = editionId, NarratorId = n.Id, Order = i })
                .ToList());
        }

        [Test]
        public void should_find_by_foreign_id()
        {
            Subject.FindByForeignId("B000APZOQA").Name.Should().Be("George Guidall");
            Subject.FindByForeignId("nope").Should().BeNull();
        }

        [Test]
        public void should_store_a_narrator_without_a_foreign_id()
        {
            Subject.FindByCleanName("frankmuller").ForeignNarratorId.Should().BeNull();
        }

        [Test]
        public void should_find_by_clean_name()
        {
            Subject.FindByCleanName("katereading").Name.Should().Be("Kate Reading");
            Subject.FindByCleanName("nope").Should().BeNull();
        }

        [Test]
        public void should_find_narrators_for_edition_through_the_join()
        {
            GivenJoinRows(10, _narrators[1], _narrators[0]);
            GivenJoinRows(11, _narrators[2]);

            Subject.FindForEdition(10).Select(n => n.Name)
                .Should().BeEquivalentTo("Frank Muller", "George Guidall");
            Subject.FindForEdition(11).Select(n => n.Name)
                .Should().BeEquivalentTo("Kate Reading");
            Subject.FindForEdition(12).Should().BeEmpty();
        }

        [Test]
        public void should_round_trip_the_order_column()
        {
            GivenJoinRows(10, _narrators[1], _narrators[0]);

            var rows = _joinRepo.FindByEditionId(10).OrderBy(x => x.Order).ToList();

            rows.Select(x => x.Order).Should().Equal(0, 1);
            rows.Select(x => x.NarratorId).Should().Equal(_narrators[1].Id, _narrators[0].Id);
        }

        [Test]
        public void should_update_the_order_column()
        {
            GivenJoinRows(10, _narrators[0]);

            var row = _joinRepo.FindByEditionId(10).Single();
            row.Order = 7;
            _joinRepo.Update(row);

            _joinRepo.FindByEditionId(10).Single().Order.Should().Be(7);
        }

        [Test]
        public void should_find_join_rows_by_narrator()
        {
            GivenJoinRows(10, _narrators[0]);
            GivenJoinRows(11, _narrators[0], _narrators[1]);

            _joinRepo.FindByNarratorId(_narrators[0].Id).Select(x => x.EditionId)
                .Should().BeEquivalentTo(new[] { 10, 11 });
        }

        [Test]
        public void should_delete_only_the_join_rows_of_the_edition()
        {
            GivenJoinRows(10, _narrators[0], _narrators[1]);
            GivenJoinRows(11, _narrators[2]);

            _joinRepo.DeleteByEditionId(10);

            _joinRepo.FindByEditionId(10).Should().BeEmpty();
            _joinRepo.FindByEditionId(11).Should().ContainSingle();
            AllStoredModels.Should().HaveCount(3);
        }

        [Test]
        public void should_lazy_load_narrators_on_an_edition()
        {
            // The LazyLoad registered for Edition.NarratorList in
            // TableMapping builds its own join over EditionNarrators.
            var edition = Builder<Edition>.CreateNew()
                .With(e => e.Id = 0)
                .With(e => e.BookId = 1)
                .Build();
            Db.Insert(edition);
            GivenJoinRows(edition.Id, _narrators[2], _narrators[0]);

            var db = Mocker.Resolve<IDatabase>();
            var loaded = db.Query<Edition>(new SqlBuilder(db.DatabaseType)).Single();

            loaded.NarratorList.IsLoaded.Should().BeFalse();
            loaded.NarratorList.Value.Select(n => n.Name)
                .Should().BeEquivalentTo("Kate Reading", "George Guidall");
        }
    }
}
