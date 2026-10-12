using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class CleanupOrphanedNarratorsFixture : DbTest<CleanupOrphanedNarrators, Narrator>
    {
        [Test]
        public void should_delete_orphaned_narrators()
        {
            var narrator = Builder<Narrator>.CreateNew()
                .BuildNew();

            Db.Insert(narrator);
            Subject.Clean();
            AllStoredModels.Should().BeEmpty();
        }

        [Test]
        public void should_not_delete_unorphaned_narrators()
        {
            var narrators = Builder<Narrator>.CreateListOfSize(2)
                .BuildListOfNew();

            Db.InsertMany(narrators);

            var links = Builder<EditionNarrator>.CreateListOfSize(2)
                .All()
                .With(l => l.NarratorId = narrators[0].Id)
                .BuildListOfNew();

            Db.InsertMany(links);
            Subject.Clean();
            AllStoredModels.Should().HaveCount(1);
            AllStoredModels.Should().Contain(n => n.Id == narrators[0].Id);
        }
    }
}
