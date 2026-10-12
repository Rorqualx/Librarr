using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MusicTests
{
    // "Monitor new books" through a whole author refresh, with the real
    // MonitorNewBookService and books shaped the way OpenLibrary delivers
    // them: the author's works list yields slim records on the placeholder
    // date, and the per-book fetch that follows (RefreshBookService, mocked
    // here to do just that part) fills in whatever date OL has.
    //
    // The existing books in these tests are dated, which makes them differ
    // from their slim remote counterparts -- so the refresh classes them as
    // Updated rather than UpToDate, as it does for every fetched book in a
    // real library. The comparison list used to be UpToDate alone.
    [TestFixture]
    public class RefreshAuthorNewBookMonitoringFixture : CoreTest<RefreshAuthorService>
    {
        private Author _author;
        private List<Book> _stored;
        private List<Book> _remote;
        private Dictionary<string, DateTime?> _fetchedDates;
        private Dictionary<string, bool> _monitoredWhenInserted;
        private int _nextId;

        [SetUp]
        public void Setup()
        {
            var metadata = Builder<AuthorMetadata>.CreateNew().Build();

            _author = Builder<Author>.CreateNew()
                .With(a => a.Metadata = metadata)
                .With(a => a.Series = new List<Series>())
                .With(a => a.Monitored = true)
                .With(a => a.MonitorNewItems = NewItemMonitorTypes.New)
                .With(a => a.LastInfoSync = DateTime.UtcNow.AddDays(-3))
                .Build();

            _stored = new List<Book>
            {
                Stored(1, "OL1W", new DateTime(2010, 8, 31, 0, 0, 0, DateTimeKind.Utc)),
                Stored(2, "OL2W", new DateTime(2020, 11, 17, 0, 0, 0, DateTimeKind.Utc))
            };

            _remote = _stored.Select(b => Slim(b.ForeignBookId)).ToList();
            _fetchedDates = new Dictionary<string, DateTime?>();
            _monitoredWhenInserted = new Dictionary<string, bool>();
            _nextId = 100;

            Mocker.SetConstant<IMonitorNewBookService>(Mocker.Resolve<MonitorNewBookService>());

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.GetAuthors(It.IsAny<List<int>>()))
                .Returns(() => new List<Author> { _author });

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.UpdateAuthor(It.IsAny<Author>()))
                .Returns((Author a) => a);

            Mocker.GetMock<IProvideAuthorInfo>()
                .Setup(s => s.GetAuthorInfo(It.IsAny<string>(), true))
                .Returns(() =>
                {
                    var remoteAuthor = _author.JsonClone();
                    remoteAuthor.Metadata = _author.Metadata.Value.JsonClone();
                    remoteAuthor.Series = new List<Series>();
                    remoteAuthor.Books = _remote;
                    return remoteAuthor;
                });

            Mocker.GetMock<IMetadataProfileService>()
                .Setup(s => s.FilterBooks(It.IsAny<Author>(), It.IsAny<int>()))
                .Returns(() => _remote);

            Mocker.GetMock<IImportListExclusionService>()
                .Setup(s => s.FindByForeignId(It.IsAny<List<string>>()))
                .Returns(new List<ImportListExclusion>());

            Mocker.GetMock<IRootFolderService>()
                .Setup(s => s.All())
                .Returns(new List<RootFolder>());

            Mocker.GetMock<IBookService>()
                .Setup(s => s.GetBooksForRefresh(It.IsAny<int>(), It.IsAny<List<string>>()))
                .Returns(() => _stored);

            Mocker.GetMock<IBookService>()
                .Setup(s => s.InsertMany(It.IsAny<List<Book>>()))
                .Callback((List<Book> books) =>
                {
                    foreach (var book in books)
                    {
                        book.Id = _nextId++;
                        _monitoredWhenInserted[book.ForeignBookId] = book.Monitored;
                    }
                });

            // The part of RefreshBookService that matters here: it updates the
            // Book instances it is given, in place, with the fetched date.
            Mocker.GetMock<IRefreshBookService>()
                .Setup(s => s.RefreshBookInfo(It.IsAny<List<Book>>(), It.IsAny<List<Book>>(), It.IsAny<Author>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<DateTime?>()))
                .Callback((List<Book> books, List<Book> remoteBooks, Author remoteData, bool force, bool tags, DateTime? lastUpdate) =>
                {
                    foreach (var book in books)
                    {
                        if (_fetchedDates.TryGetValue(book.ForeignBookId, out var date))
                        {
                            book.ReleaseDate = date;
                        }
                    }
                })
                .Returns(true);
        }

        private static Book Stored(int id, string foreignId, DateTime? releaseDate)
        {
            return new Book
            {
                Id = id,
                ForeignBookId = foreignId,
                Title = foreignId,
                ReleaseDate = releaseDate,
                Monitored = true
            };
        }

        // What OpenLibraryAuthorMapper.ToSlimBooks produces for a work with no
        // first_publish_date, which is nearly all of them.
        private static Book Slim(string foreignId)
        {
            return new Book
            {
                ForeignBookId = foreignId,
                Title = foreignId,
                ReleaseDate = PlaceholderReleaseDate.Value,
                Monitored = true,
                Editions = new List<Edition> { new Edition { ForeignEditionId = foreignId, Monitored = true } }
            };
        }

        private Book GivenNewWorkAtTheSource(string foreignId)
        {
            var book = Slim(foreignId);
            _remote.Add(book);
            return book;
        }

        private void GivenFirstSync()
        {
            _author.LastInfoSync = null;
            _stored = new List<Book>();
            _remote = new List<Book>();
        }

        private void Refresh()
        {
            Subject.Execute(new RefreshAuthorCommand(_author.Id));
        }

        private void VerifyNoSecondWrite()
        {
            Mocker.GetMock<IBookService>()
                .Verify(s => s.UpdateMany(It.IsAny<List<Book>>()), Times.Never());
        }

        // The defect: the placeholder compared as 1900 against the 2020 book,
        // and the new work was inserted unmonitored for good.
        [Test]
        public void new_work_for_a_synced_author_should_be_monitored()
        {
            var discovered = GivenNewWorkAtTheSource("OL3W");

            Refresh();

            _monitoredWhenInserted["OL3W"].Should().BeTrue();
            discovered.Monitored.Should().BeTrue("the per-book fetch found no date, so discovery is all there is to go on");
            VerifyNoSecondWrite();
        }

        [Test]
        public void new_work_that_turns_out_to_be_recent_should_stay_monitored()
        {
            var discovered = GivenNewWorkAtTheSource("OL3W");
            _fetchedDates["OL3W"] = new DateTime(2024, 12, 6, 0, 0, 0, DateTimeKind.Utc);

            Refresh();

            discovered.Monitored.Should().BeTrue();
            VerifyNoSecondWrite();
        }

        // OpenLibrary gains records for long-published books constantly. Once
        // the fetch says 1985, the book is not a new release, and the answer
        // given at discovery is withdrawn and saved.
        [Test]
        public void new_work_that_turns_out_to_be_old_should_be_unmonitored_again()
        {
            var discovered = GivenNewWorkAtTheSource("OL3W");
            _fetchedDates["OL3W"] = new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Refresh();

            _monitoredWhenInserted["OL3W"].Should().BeTrue("at discovery nothing was known but that it had appeared");
            discovered.Monitored.Should().BeFalse();

            Mocker.GetMock<IBookService>()
                .Verify(s => s.UpdateMany(It.Is<List<Book>>(l => l.Count == 1 && l[0] == discovered && !l[0].Monitored)), Times.Once());
        }

        [Test]
        public void new_work_announced_for_the_future_should_be_monitored()
        {
            var discovered = GivenNewWorkAtTheSource("OL3W");
            _fetchedDates["OL3W"] = DateTime.UtcNow.AddMonths(4);

            Refresh();

            discovered.Monitored.Should().BeTrue();
            VerifyNoSecondWrite();
        }

        [Test]
        public void first_sync_should_not_monitor_the_back_catalogue()
        {
            GivenFirstSync();

            var books = Enumerable.Range(1, 5).Select(i => GivenNewWorkAtTheSource($"OL{i}W")).ToList();
            _fetchedDates["OL1W"] = new DateTime(2010, 8, 31, 0, 0, 0, DateTimeKind.Utc);
            _fetchedDates["OL2W"] = new DateTime(2020, 11, 17, 0, 0, 0, DateTimeKind.Utc);

            Refresh();

            _monitoredWhenInserted.Values.Should().OnlyContain(m => !m);
            books.Should().OnlyContain(b => !b.Monitored);
            VerifyNoSecondWrite();
        }

        // The one thing in a first sync that *is* a new book.
        [Test]
        public void first_sync_should_monitor_a_book_that_is_not_out_yet()
        {
            GivenFirstSync();

            var books = Enumerable.Range(1, 3).Select(i => GivenNewWorkAtTheSource($"OL{i}W")).ToList();
            _fetchedDates["OL1W"] = new DateTime(2010, 8, 31, 0, 0, 0, DateTimeKind.Utc);
            _fetchedDates["OL3W"] = DateTime.UtcNow.AddMonths(4);

            Refresh();

            books.Select(b => b.Monitored).Should().Equal(false, false, true);

            Mocker.GetMock<IBookService>()
                .Verify(s => s.UpdateMany(It.Is<List<Book>>(l => l.Count == 1 && l[0].ForeignBookId == "OL3W" && l[0].Monitored)), Times.Once());
        }

        // LastInfoSync is only saved when a refresh completes. An author whose
        // first refresh died part-way has its books but has never been synced,
        // and whatever the retry adds is still back catalogue.
        [Test]
        public void retry_of_a_failed_first_sync_should_still_count_as_a_first_sync()
        {
            _author.LastInfoSync = null;

            var discovered = GivenNewWorkAtTheSource("OL3W");

            Refresh();

            discovered.Monitored.Should().BeFalse();
        }

        [TestCase(NewItemMonitorTypes.All, true)]
        [TestCase(NewItemMonitorTypes.None, false)]
        public void other_monitor_types_should_not_depend_on_dates_or_get_a_second_look(NewItemMonitorTypes type, bool expected)
        {
            _author.MonitorNewItems = type;

            var discovered = GivenNewWorkAtTheSource("OL3W");
            _fetchedDates["OL3W"] = new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Refresh();

            discovered.Monitored.Should().Be(expected);
            VerifyNoSecondWrite();
        }
    }
}
