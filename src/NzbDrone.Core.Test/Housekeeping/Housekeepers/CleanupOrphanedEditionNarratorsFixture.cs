using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupOrphanedEditionNarratorsFixture : DbTest<CleanupOrphanedEditionNarrators, EditionNarrator>
    {
        [Test]
        public void should_delete_orphaned_edition_narrators()
        {
            var link = Builder<EditionNarrator>.CreateNew()
                .With(l => l.EditionId = 999999)
                .BuildNew();

            Db.Insert(link);
            Subject.Clean();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_delete_unorphaned_edition_narrators()
        {
            var edition = Builder<Edition>.CreateNew()
                .BuildNew();

            Db.Insert(edition);

            var links = Builder<EditionNarrator>.CreateListOfSize(2)
                .TheFirst(1)
                .With(l => l.EditionId = edition.Id)
                .TheNext(1)
                .With(l => l.EditionId = edition.Id + 100)
                .BuildListOfNew();

            Db.InsertMany(links);
            Subject.Clean();
            AllStoredModels.Should().HaveCount(1);
            AllStoredModels.Should().Contain(l => l.EditionId == edition.Id);
        }
    }
}
