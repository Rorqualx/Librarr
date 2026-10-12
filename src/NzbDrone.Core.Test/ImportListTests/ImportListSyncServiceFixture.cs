using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.Goodreads;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.ImportListTests
{
    public class ImportListSyncServiceFixture : CoreTest<ImportListSyncService>
    {
        private List<ImportListItemInfo> _importListReports;
        private MemoryTarget _logTarget;
        private LoggingRule _logRule;

        [SetUp]
        public void SetUp()
        {
            var importListItem1 = new ImportListItemInfo
            {
                Author = "Linkin Park"
            };

            _importListReports = new List<ImportListItemInfo> { importListItem1 };

            var mockImportList = new Mock<IImportList>();

            Mocker.GetMock<IFetchAndParseImportList>()
                .Setup(v => v.Fetch())
                .Returns(_importListReports);

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Setup(v => v.Search(It.IsAny<string>()))
                .Returns(new List<SearchJsonResource>());

            Mocker.GetMock<IGoodreadsProxy>()
                .Setup(v => v.GetBookInfo(It.IsAny<string>(), true))
                .Returns<string, bool>((id, useCache) => Builder<Book>
                .CreateNew()
                .With(b => b.AuthorMetadata = Builder<AuthorMetadata>.CreateNew().Build())
                .With(b => b.ForeignBookId = "4321")
                .With(b => b.Editions = Builder<Edition>
                    .CreateListOfSize(1)
                    .TheFirst(1)
                    .With(e => e.ForeignEditionId = id.ToString())
                    .With(e => e.Monitored = true)
                    .BuildList())
                .Build());

            Mocker.GetMock<IImportListFactory>()
                .Setup(v => v.Get(It.IsAny<int>()))
                .Returns(new ImportListDefinition { ShouldMonitor = ImportListMonitorType.SpecificBook });

            Mocker.GetMock<IImportListFactory>()
                .Setup(v => v.AutomaticAddEnabled(It.IsAny<bool>()))
                .Returns(new List<IImportList> { mockImportList.Object });

            Mocker.GetMock<IFetchAndParseImportList>()
                .Setup(v => v.Fetch())
                .Returns(_importListReports);

            Mocker.GetMock<IImportListExclusionService>()
                .Setup(v => v.All())
                .Returns(new List<ImportListExclusion>());

            Mocker.GetMock<IAddBookService>()
                .Setup(v => v.AddBooks(It.IsAny<List<Book>>(), false))
                .Returns<List<Book>, bool>((x, y) => x);

            Mocker.GetMock<IAddAuthorService>()
                .Setup(v => v.AddAuthors(It.IsAny<List<Author>>(), false))
                .Returns<List<Author>, bool>((x, y) => x);
        }

        [TearDown]
        public void TearDown()
        {
            if (_logTarget != null)
            {
                LogManager.Configuration.LoggingRules.Remove(_logRule);
                LogManager.Configuration.RemoveTarget(_logTarget.Name);
                LogManager.ReconfigExistingLoggers();
                _logTarget = null;
            }
        }

        private void WithCapturedLog()
        {
            _logTarget = new MemoryTarget { Name = "ImportListSyncServiceFixture", Layout = "${message}" };
            _logRule = new LoggingRule("*", LogLevel.Info, _logTarget);

            LogManager.Configuration.AddTarget(_logTarget.Name, _logTarget);
            LogManager.Configuration.LoggingRules.Add(_logRule);
            LogManager.ReconfigExistingLoggers();
        }

        // What the three OpenLibrary lists emit: work and author keys, and
        // no edition id, because OpenLibrary has no Goodreads edition ids.
        private void WithOpenLibraryItem()
        {
            _importListReports.Clear();
            _importListReports.Add(new ImportListItemInfo
            {
                Author = "Ursula K. Le Guin",
                AuthorGoodreadsId = "OL31353A",
                Book = "The Dispossessed",
                BookGoodreadsId = "OL26561399W",
                EditionGoodreadsId = null
            });
        }

        private void WithBook()
        {
            _importListReports.First().Book = "Meteora";
        }

        private void WithAuthorId()
        {
            _importListReports.First().AuthorGoodreadsId = "f59c5520-5f46-4d2c-b2c4-822eabf53419";
        }

        private void WithBookId()
        {
            _importListReports.First().EditionGoodreadsId = "1234";
        }

        private void WithSecondBook()
        {
            var importListItem2 = new ImportListItemInfo
            {
                Author = "Linkin Park",
                AuthorGoodreadsId = "f59c5520-5f46-4d2c-b2c4-822eabf53419",
                Book = "Meteora 2",
                EditionGoodreadsId = "5678",
                BookGoodreadsId = "8765"
            };
            _importListReports.Add(importListItem2);
        }

        private void WithExistingAuthor()
        {
            Mocker.GetMock<IAuthorService>()
                .Setup(v => v.FindById(_importListReports.First().AuthorGoodreadsId))
                .Returns(new Author { Id = 1, ForeignAuthorId = _importListReports.First().AuthorGoodreadsId });
        }

        private void WithExistingBook()
        {
            Mocker.GetMock<IBookService>()
                .Setup(v => v.FindById("4321"))
                .Returns(new Book { Id = 1, ForeignBookId = _importListReports.First().BookGoodreadsId });
        }

        private void WithExcludedAuthor()
        {
            Mocker.GetMock<IImportListExclusionService>()
                .Setup(v => v.All())
                .Returns(new List<ImportListExclusion>
                {
                    new ImportListExclusion
                    {
                        ForeignId = "f59c5520-5f46-4d2c-b2c4-822eabf53419"
                    }
                });
        }

        private void WithExcludedBook()
        {
            Mocker.GetMock<IImportListExclusionService>()
                .Setup(v => v.All())
                .Returns(new List<ImportListExclusion>
                {
                    new ImportListExclusion
                    {
                        ForeignId = "4321"
                    }
                });
        }

        private void WithMonitorType(ImportListMonitorType monitor)
        {
            Mocker.GetMock<IImportListFactory>()
                .Setup(v => v.Get(It.IsAny<int>()))
                .Returns(new ImportListDefinition { ShouldMonitor = monitor });
        }

        [Test]
        public void should_search_if_author_title_and_no_author_id()
        {
            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_not_search_if_author_title_and_author_id()
        {
            WithAuthorId();
            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<ISearchForNewAuthor>()
                .Verify(v => v.SearchForNewAuthor(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_search_if_book_title_and_no_book_id()
        {
            WithBook();
            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Once());
        }

        [Test]
        public void should_not_search_if_book_title_and_book_id()
        {
            WithAuthorId();
            WithBookId();
            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_not_search_if_all_info()
        {
            WithAuthorId();
            WithBook();
            WithBookId();
            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Never());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Never());
        }

        [Test]
        public void should_not_add_if_existing_author()
        {
            WithAuthorId();
            WithExistingAuthor();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 0), false));
        }

        [Test]
        public void should_not_add_if_existing_book()
        {
            WithBookId();
            WithExistingBook();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 0), false));
        }

        [Test]
        public void should_add_if_existing_author_but_new_book()
        {
            WithBookId();
            WithAuthorId();
            WithExistingAuthor();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 1), false));
        }

        [TestCase(ImportListMonitorType.None, false)]
        [TestCase(ImportListMonitorType.SpecificBook, true)]
        [TestCase(ImportListMonitorType.EntireAuthor, true)]
        public void should_add_if_not_existing_author(ImportListMonitorType monitor, bool expectedAuthorMonitored)
        {
            WithAuthorId();
            WithMonitorType(monitor);

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 1 && t.First().Monitored == expectedAuthorMonitored), false));
        }

        [TestCase(ImportListMonitorType.None, false)]
        [TestCase(ImportListMonitorType.SpecificBook, true)]
        [TestCase(ImportListMonitorType.EntireAuthor, true)]
        public void should_add_if_not_existing_book(ImportListMonitorType monitor, bool expectedBookMonitored)
        {
            WithBookId();
            WithMonitorType(monitor);

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 1 && t.First().Monitored == expectedBookMonitored), false));
        }

        [Test]
        public void should_not_add_author_if_excluded_author()
        {
            WithAuthorId();
            WithExcludedAuthor();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 0), false));
        }

        [Test]
        public void should_not_add_book_if_excluded_book()
        {
            WithBookId();
            WithExcludedBook();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 0), false));
        }

        [Test]
        public void should_not_add_book_if_excluded_author()
        {
            WithBookId();
            WithAuthorId();
            WithExcludedAuthor();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 0), false));
        }

        [TestCase(ImportListMonitorType.None, 0, false)]
        [TestCase(ImportListMonitorType.SpecificBook, 2, true)]
        [TestCase(ImportListMonitorType.EntireAuthor, 0, true)]
        public void should_add_two_books(ImportListMonitorType monitor, int expectedBooksMonitored, bool expectedAuthorMonitored)
        {
            WithBook();
            WithBookId();
            WithSecondBook();
            WithAuthorId();
            WithMonitorType(monitor);

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 2), false));
            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 1 &&
                                                                   t.First().AddOptions.BooksToMonitor.Count == expectedBooksMonitored &&
                                                                   t.First().Monitored == expectedAuthorMonitored), false));
        }

        [Test]
        public void should_add_book_that_has_no_edition_id()
        {
            WithOpenLibraryItem();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 1 &&
                                                               t.First().ForeignBookId == "OL26561399W" &&
                                                               t.First().Monitored &&
                                                               t.First().Editions.Value.Count == 0), false));
            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 1 &&
                                                                   t.First().AddOptions.BooksToMonitor.Single() == "OL26561399W"), false));
        }

        [Test]
        public void should_not_map_through_goodreads_if_book_and_author_id_but_no_edition_id()
        {
            WithOpenLibraryItem();

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IGoodreadsSearchProxy>()
                .Verify(v => v.Search(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IGoodreadsProxy>()
                .Verify(v => v.GetBookInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());
        }

        // An author keeps its AddOptions until its first scan has run, so a
        // sync that arrives before then finds an existing author that still
        // carries them. They belong to that earlier add, not to this sync.
        [Test]
        public void should_not_change_add_options_of_existing_author_that_has_not_been_scanned_yet()
        {
            WithOpenLibraryItem();

            var existingAuthor = new Author
            {
                Id = 1,
                ForeignAuthorId = "OL31353A",
                Monitored = true,
                AddOptions = new AddAuthorOptions { BooksToMonitor = new List<string> { "OL1W" } }
            };

            Mocker.GetMock<IAuthorService>()
                .Setup(v => v.FindById("OL31353A"))
                .Returns(existingAuthor);

            Subject.Execute(new ImportListSyncCommand());

            existingAuthor.AddOptions.BooksToMonitor.Should().Equal("OL1W");

            Mocker.GetMock<IAddAuthorService>()
                .Verify(v => v.AddAuthors(It.Is<List<Author>>(t => t.Count == 0), false));
            Mocker.GetMock<IAddBookService>()
                .Verify(v => v.AddBooks(It.Is<List<Book>>(t => t.Count == 1 && t.First().Author.Value == existingAuthor), false));
        }

        [Test]
        public void should_report_books_and_authors_that_were_added_not_the_ones_attempted()
        {
            WithOpenLibraryItem();
            WithCapturedLog();

            Mocker.GetMock<IAddBookService>()
                .Setup(v => v.AddBooks(It.IsAny<List<Book>>(), false))
                .Returns(new List<Book>());

            Mocker.GetMock<IAddAuthorService>()
                .Setup(v => v.AddAuthors(It.IsAny<List<Author>>(), false))
                .Returns(new List<Author>());

            Subject.Execute(new ImportListSyncCommand());

            _logTarget.Logs.Should().Contain("Import List Sync Completed. Items found: 1, Authors added: 0, Books added: 0");
        }

        [Test]
        public void should_not_fetch_if_no_lists_are_enabled()
        {
            Mocker.GetMock<IImportListFactory>()
                .Setup(v => v.AutomaticAddEnabled(It.IsAny<bool>()))
                .Returns(new List<IImportList>());

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IFetchAndParseImportList>()
                .Verify(v => v.Fetch(), Times.Never);
        }

        [Test]
        public void should_not_process_if_no_items_are_returned()
        {
            Mocker.GetMock<IFetchAndParseImportList>()
                .Setup(v => v.Fetch())
                .Returns(new List<ImportListItemInfo>());

            Subject.Execute(new ImportListSyncCommand());

            Mocker.GetMock<IImportListExclusionService>()
                .Verify(v => v.All(), Times.Never);
        }
    }
}
