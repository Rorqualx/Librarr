using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    [TestFixture]
    public class EditionRepositoryFixture : DbTest<EditionRepository, Edition>
    {
        [Test]
        public void find_by_ids_should_skip_ids_with_no_edition()
        {
            var editions = Builder<Edition>.CreateListOfSize(3)
                .BuildListOfNew();

            Db.InsertMany(editions);

            var result = Subject.FindByIds(new[] { editions[0].Id, editions[2].Id, 999999 });

            result.Select(e => e.Id).Should().BeEquivalentTo(new[] { editions[0].Id, editions[2].Id });
        }

        [Test]
        public void find_by_ids_should_return_empty_when_no_ids_given()
        {
            Db.Insert(Builder<Edition>.CreateNew().BuildNew());

            Subject.FindByIds(Enumerable.Empty<int>()).Should().BeEmpty();
        }
    }
}
