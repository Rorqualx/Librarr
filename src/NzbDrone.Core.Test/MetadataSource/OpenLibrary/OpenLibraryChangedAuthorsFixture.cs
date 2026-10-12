using System;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    [TestFixture]
    public class OpenLibraryChangedAuthorsFixture : CoreTest<OpenLibraryProxy>
    {
        // RefreshAuthorService reads null as "no change feed, refresh whoever
        // is due" and an empty set as "nothing changed, refresh nobody".
        // OpenLibrary has no change feed, so an empty set here means the
        // scheduled refresh never refreshes any author.
        [Test]
        public void should_report_no_change_feed_rather_than_no_changes()
        {
            Subject.GetChangedAuthors(DateTime.UtcNow.AddDays(-1)).Should().BeNull();
        }
    }
}
