using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class RefreshBookServiceEditionFixture : CoreTest<RefreshBookService>
    {
        private const string WorkId = "OL1W";

        private Author _author;
        private Book _book;
        private List<Edition> _stored;
        private List<Edition> _remote;

        private List<Edition> _updated;
        private List<Edition> _deleted;
        private List<Edition> _upToDate;

        [SetUp]
        public void Setup()
        {
            _updated = null;
            _deleted = null;
            _upToDate = null;

            var metadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = "OL1A").Build();

            _author = Builder<Author>.CreateNew()
                .With(a => a.Metadata = metadata)
                .Build();

            _book = Builder<Book>.CreateNew()
                .With(b => b.ForeignBookId = WorkId)
                .With(b => b.AuthorMetadata = metadata)
                .With(b => b.Author = _author)
                .Build();

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.FindById(It.IsAny<string>()))
                .Returns(_author);

            Mocker.GetMock<IProvideAuthorInfo>()
                .Setup(s => s.GetAuthorInfo(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(() => Builder<Author>.CreateNew().With(a => a.Metadata = metadata).Build());

            Mocker.GetMock<IAugmentAudiobookInfo>()
                .Setup(s => s.Augment(It.IsAny<Book>()))
                .Returns((Book b) => b);

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(It.IsAny<int>()))
                .Returns(new List<BookFile>());

            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByEdition(It.IsAny<int>()))
                .Returns(new List<BookFile>());

            Mocker.GetMock<IRefreshEditionService>()
                .Setup(s => s.RefreshEditionInfo(It.IsAny<List<Edition>>(),
                                                 It.IsAny<List<Edition>>(),
                                                 It.IsAny<List<Tuple<Edition, Edition>>>(),
                                                 It.IsAny<List<Edition>>(),
                                                 It.IsAny<List<Edition>>(),
                                                 It.IsAny<List<Edition>>(),
                                                 It.IsAny<bool>()))
                .Callback((List<Edition> add, List<Edition> update, List<Tuple<Edition, Edition>> merge, List<Edition> delete, List<Edition> upToDate, List<Edition> remote, bool tags) =>
                {
                    _updated = update;
                    _deleted = delete;
                    _upToDate = upToDate;
                });
        }

        private static Edition NewEdition(int id, string foreignId, bool monitored, bool chosenByUser = false)
        {
            return Builder<Edition>.CreateNew()
                .With(e => e.Id = id)
                .With(e => e.BookId = 1)
                .With(e => e.ForeignEditionId = foreignId)
                .With(e => e.Title = "Title")
                .With(e => e.Monitored = monitored)
                .With(e => e.ManualAdd = chosenByUser)
                .Build();
        }

        private void GivenEditions(List<Edition> stored, List<Edition> remote)
        {
            _stored = stored;
            _remote = remote;

            // What the refresh works on, and what a later read of the
            // database returns, must be separate instances.
            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEditionsForRefresh(It.IsAny<int>(), It.IsAny<List<string>>()))
                .Returns(() => _stored.Select(e => NewEdition(e.Id, e.ForeignEditionId, e.Monitored, e.ManualAdd)).ToList());

            Mocker.GetMock<IEditionService>()
                .Setup(s => s.GetEditionsByBook(It.IsAny<int>()))
                .Returns(() => _stored.Select(e => NewEdition(e.Id, e.ForeignEditionId, e.Monitored, e.ManualAdd)).ToList());

            var remoteBook = Builder<Book>.CreateNew()
                .With(b => b.ForeignBookId = WorkId)
                .With(b => b.Title = _book.Title)
                .With(b => b.Editions = _remote)
                .Build();

            Mocker.GetMock<IProvideBookInfo>()
                .Setup(s => s.GetBookInfo(WorkId, It.IsAny<bool>()))
                .Returns(Tuple.Create("OL1A", remoteBook, new List<AuthorMetadata>()));
        }

        private void GivenFileOnEdition(int editionId)
        {
            Mocker.GetMock<IMediaFileService>()
                .Setup(s => s.GetFilesByBook(It.IsAny<int>()))
                .Returns(new List<BookFile> { new BookFile { Id = 1, EditionId = editionId } });
        }

        private void Refresh()
        {
            // The author's works list still names the book; without that a
            // fileless book is taken as removed and never reaches its editions.
            var listed = Builder<Book>.CreateNew().With(b => b.ForeignBookId = WorkId).Build();

            Subject.RefreshBookInfo(_book, new List<Book> { listed }, _author, false);
        }

        [Test]
        public void should_save_both_sides_when_the_monitored_edition_changes()
        {
            // The user's edition (2) is monitored locally; the source picks 1.
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", false), NewEdition(2, "OL2M", true) },
                          new List<Edition> { NewEdition(0, "OL1M", true), NewEdition(0, "OL2M", false) });

            Refresh();

            _updated.Select(e => e.Id).Should().BeEquivalentTo(new[] { 1, 2 });
            _updated.Where(e => e.Monitored).Select(e => e.Id).Should().Equal(1);
        }

        [Test]
        public void should_not_delete_an_edition_that_has_files_when_the_source_omits_it()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", true), NewEdition(3, "OL3M", false) },
                          new List<Edition> { NewEdition(0, "OL1M", true) });
            GivenFileOnEdition(3);

            Refresh();

            _deleted.Should().BeEmpty();
        }

        [Test]
        public void should_monitor_the_edition_that_has_the_file()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", true), NewEdition(3, "OL3M", false) },
                          new List<Edition> { NewEdition(0, "OL1M", true) });
            GivenFileOnEdition(3);

            Refresh();

            _updated.Where(e => e.Monitored).Select(e => e.Id).Should().Equal(3);
            _updated.Where(e => !e.Monitored).Select(e => e.Id).Should().Equal(1);
        }

        [Test]
        public void should_still_delete_an_edition_without_files_that_the_source_omits()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", true), NewEdition(3, "OL3M", false) },
                          new List<Edition> { NewEdition(0, "OL1M", true) });

            Refresh();

            _deleted.Select(e => e.Id).Should().Equal(3);
        }

        private IEnumerable<int> MonitoredAfterRefresh()
        {
            return _updated.Concat(_upToDate).Where(e => e.Monitored).Select(e => e.Id);
        }

        [Test]
        public void should_keep_the_edition_the_user_chose_over_the_sources_pick()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", false), NewEdition(2, "OL2M", true, chosenByUser: true) },
                          new List<Edition> { NewEdition(0, "OL1M", true), NewEdition(0, "OL2M", false) });

            Refresh();

            MonitoredAfterRefresh().Should().Equal(2);
        }

        [Test]
        public void should_keep_the_edition_the_user_chose_when_another_edition_has_the_file()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", false), NewEdition(2, "OL2M", true, chosenByUser: true) },
                          new List<Edition> { NewEdition(0, "OL1M", true), NewEdition(0, "OL2M", false) });
            GivenFileOnEdition(1);

            Refresh();

            MonitoredAfterRefresh().Should().Equal(2);
        }

        [Test]
        public void should_follow_the_sources_pick_when_the_users_edition_is_no_longer_the_monitored_one()
        {
            // An import moved monitoring to edition 1; the old choice (2)
            // is still marked as the user's but is not monitored any more.
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", true), NewEdition(2, "OL2M", false, chosenByUser: true), NewEdition(3, "OL3M", false) },
                          new List<Edition> { NewEdition(0, "OL1M", false), NewEdition(0, "OL2M", false), NewEdition(0, "OL3M", true) });

            Refresh();

            MonitoredAfterRefresh().Should().Equal(3);
        }

        // The work bundle is cached for a week. Only a refresh the user asked
        // for, of this one book, is worth a fresh fetch; everything else
        // would multiply requests across the library.
        private void GivenBookInLibrary()
        {
            GivenEditions(new List<Edition> { NewEdition(1, "OL1M", true) },
                          new List<Edition> { NewEdition(0, "OL1M", true) });

            Mocker.GetMock<IBookService>().Setup(s => s.GetBook(_book.Id)).Returns(_book);

            Mocker.GetMock<IBookService>()
                .Setup(s => s.GetBooks(It.IsAny<IEnumerable<int>>()))
                .Returns(new List<Book> { _book });
        }

        private void VerifyBookFetchedFresh(Times times)
        {
            Mocker.GetMock<IProvideBookInfo>().Verify(s => s.GetBookInfo(WorkId, false), times);
        }

        [Test]
        public void manual_refresh_of_one_book_should_bypass_the_cache()
        {
            GivenBookInLibrary();

            Subject.Execute(new RefreshBookCommand(_book.Id) { Trigger = CommandTrigger.Manual });

            // Once: the fresh copy replaces the cached one, so the second
            // read the refresh makes can take it from there.
            VerifyBookFetchedFresh(Times.Once());
            Mocker.GetMock<IProvideBookInfo>().Verify(s => s.GetBookInfo(WorkId, true), Times.Once());
        }

        [Test]
        public void manual_refresh_of_one_book_should_not_refetch_the_author()
        {
            GivenBookInLibrary();

            Subject.Execute(new RefreshBookCommand(_book.Id) { Trigger = CommandTrigger.Manual });

            Mocker.GetMock<IProvideAuthorInfo>()
                .Verify(s => s.GetAuthorInfo(It.IsAny<string>(), false), Times.Never());
        }

        [TestCase(CommandTrigger.Scheduled)]
        [TestCase(CommandTrigger.Unspecified)]
        public void refresh_of_one_book_that_the_user_did_not_ask_for_should_use_the_cache(CommandTrigger trigger)
        {
            GivenBookInLibrary();

            Subject.Execute(new RefreshBookCommand(_book.Id) { Trigger = trigger });

            VerifyBookFetchedFresh(Times.Never());
            Mocker.GetMock<IProvideBookInfo>().Verify(s => s.GetBookInfo(WorkId, true), Times.AtLeastOnce());
        }

        [Test]
        public void manual_bulk_refresh_should_use_the_cache()
        {
            GivenBookInLibrary();

            Subject.Execute(new BulkRefreshBookCommand(new List<int> { _book.Id }) { Trigger = CommandTrigger.Manual });

            VerifyBookFetchedFresh(Times.Never());
            Mocker.GetMock<IProvideBookInfo>().Verify(s => s.GetBookInfo(WorkId, true), Times.AtLeastOnce());
        }

        [Test]
        public void refresh_as_part_of_an_author_should_use_the_cache()
        {
            GivenBookInLibrary();

            Refresh();

            VerifyBookFetchedFresh(Times.Never());
            Mocker.GetMock<IProvideBookInfo>().Verify(s => s.GetBookInfo(WorkId, true), Times.Once());
        }
    }
}
