using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Housekeeping.Housekeepers;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Housekeeping.Housekeepers
{
    [TestFixture]
    public class FixMultipleMonitoredEditionsFixture : DbTest<FixMultipleMonitoredEditions, Edition>
    {
        private List<Edition> GivenEditions(int bookId, params bool[] monitored)
        {
            var editions = monitored
                .Select((m, i) => Builder<Edition>.CreateNew()
                    .With(e => e.Id = 0)
                    .With(e => e.BookId = bookId)
                    .With(e => e.ForeignEditionId = $"book{bookId}-edition{i}")
                    .With(e => e.Monitored = m)
                    .Build())
                .ToList();

            Db.InsertMany(editions);

            return editions;
        }

        private List<Edition> MonitoredFor(int bookId)
        {
            return AllStoredModels.Where(e => e.BookId == bookId && e.Monitored).ToList();
        }

        [Test]
        public void should_leave_one_monitored_edition_when_two_are_monitored()
        {
            var editions = GivenEditions(1, true, true);

            Subject.Clean();

            MonitoredFor(1).Should().ContainSingle()
                .Which.Id.Should().Be(editions.Max(e => e.Id));
        }

        [Test]
        public void should_leave_one_monitored_edition_when_many_are_monitored()
        {
            // The case the old SQLite branch got wrong in a single pass: it
            // unmonitored only the lowest id, leaving two of these three.
            var editions = GivenEditions(1, true, true, false, true);

            Subject.Clean();

            MonitoredFor(1).Should().ContainSingle()
                .Which.Id.Should().Be(editions.Max(e => e.Id));
            AllStoredModels.Should().HaveCount(4);
        }

        [Test]
        public void should_not_touch_a_book_with_one_monitored_edition()
        {
            var editions = GivenEditions(1, false, true, false);

            Subject.Clean();

            MonitoredFor(1).Should().ContainSingle()
                .Which.Id.Should().Be(editions[1].Id);
        }

        [Test]
        public void should_not_monitor_anything_for_a_book_with_no_monitored_edition()
        {
            GivenEditions(1, false, false);

            Subject.Clean();

            MonitoredFor(1).Should().BeEmpty();
        }

        [Test]
        public void should_fix_each_book_independently()
        {
            GivenEditions(1, true, true, true);
            var untouched = GivenEditions(2, true, false);
            GivenEditions(3, true, true);

            Subject.Clean();

            MonitoredFor(1).Should().ContainSingle();
            MonitoredFor(2).Should().ContainSingle().Which.Id.Should().Be(untouched[0].Id);
            MonitoredFor(3).Should().ContainSingle();
        }
    }
}
