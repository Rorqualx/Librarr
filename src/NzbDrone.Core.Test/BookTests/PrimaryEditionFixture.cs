using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class PrimaryEditionFixture
    {
        private static Edition GivenEdition(int id, bool monitored, string foreignId = null)
        {
            return new Edition { Id = id, Monitored = monitored, ForeignEditionId = foreignId ?? $"OL{id}M" };
        }

        [Test]
        public void should_return_the_monitored_edition()
        {
            var editions = new List<Edition> { GivenEdition(1, false), GivenEdition(2, true), GivenEdition(3, false) };

            editions.PrimaryEdition().Id.Should().Be(2);
        }

        [Test]
        public void should_return_the_highest_id_when_several_are_monitored()
        {
            // The one FixMultipleMonitoredEditions leaves monitored.
            var editions = new List<Edition> { GivenEdition(5, true), GivenEdition(9, true), GivenEdition(7, true), GivenEdition(11, false) };

            editions.PrimaryEdition().Id.Should().Be(9);
        }

        [Test]
        public void should_not_depend_on_list_order_when_several_are_monitored()
        {
            var editions = new List<Edition> { GivenEdition(9, true), GivenEdition(5, true) };
            var reversed = new List<Edition> { GivenEdition(5, true), GivenEdition(9, true) };

            editions.PrimaryEdition().Id.Should().Be(reversed.PrimaryEdition().Id);
        }

        [Test]
        public void should_return_the_lowest_id_when_none_is_monitored()
        {
            var editions = new List<Edition> { GivenEdition(8, false), GivenEdition(3, false), GivenEdition(6, false) };

            editions.PrimaryEdition().Id.Should().Be(3);
        }

        [Test]
        public void should_return_null_when_there_are_no_editions()
        {
            new List<Edition>().PrimaryEdition().Should().BeNull();
        }

        [Test]
        public void should_return_null_for_a_null_list()
        {
            ((List<Edition>)null).PrimaryEdition().Should().BeNull();
        }

        [Test]
        public void should_keep_list_order_for_editions_that_are_not_saved_yet()
        {
            var monitored = new List<Edition> { GivenEdition(0, false, "a"), GivenEdition(0, true, "b"), GivenEdition(0, true, "c") };
            var unmonitored = new List<Edition> { GivenEdition(0, false, "a"), GivenEdition(0, false, "b") };

            monitored.PrimaryEdition().ForeignEditionId.Should().Be("b");
            unmonitored.PrimaryEdition().ForeignEditionId.Should().Be("a");
        }

        [Test]
        public void should_read_the_editions_of_a_book()
        {
            var book = new Book { Editions = new List<Edition> { GivenEdition(1, false), GivenEdition(2, true) } };

            book.PrimaryEdition().Id.Should().Be(2);
        }

        [Test]
        public void should_return_null_for_a_book_without_editions()
        {
            new Book { Editions = new List<Edition>() }.PrimaryEdition().Should().BeNull();
            new Book().PrimaryEdition().Should().BeNull();
            ((Book)null).PrimaryEdition().Should().BeNull();
        }
    }
}
