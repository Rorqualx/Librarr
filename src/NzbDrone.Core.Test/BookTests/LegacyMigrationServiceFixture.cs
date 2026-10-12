using System.Collections.Generic;
using FizzWare.NBuilder;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.BookTests
{
    [TestFixture]
    public class LegacyMigrationServiceFixture : CoreTest<LegacyMigrationService>
    {
        private const int CommandId = 7;

        private Author _legacyAuthor;

        [SetUp]
        public void Setup()
        {
            _legacyAuthor = Builder<Author>.CreateNew()
                .With(a => a.Metadata = Builder<AuthorMetadata>.CreateNew().With(m => m.ForeignAuthorId = "1825").Build())
                .With(a => a.MonitorNewItems = NewItemMonitorTypes.All)
                .Build();

            Mocker.GetMock<IAuthorService>()
                .Setup(s => s.GetAllAuthors())
                .Returns(new List<Author> { _legacyAuthor });

            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.All())
                .Returns(new List<BookIdMapping>());

            Mocker.GetMock<IManageCommandQueue>()
                .Setup(s => s.Push(It.IsAny<ReidentifyLibraryCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()))
                .Returns(new CommandModel { Id = CommandId });
        }

        private void GivenMappingRows()
        {
            Mocker.GetMock<IBookIdMappingRepository>()
                .Setup(s => s.All())
                .Returns(new List<BookIdMapping> { new BookIdMapping { GoodreadsId = "1825" } });
        }

        private void GivenMigrationStartedEarlier()
        {
            Mocker.GetMock<IConfigService>()
                .SetupGet(s => s.LegacyMigrationStarted)
                .Returns(true);
        }

        private void VerifyReidentifyQueued(Times times)
        {
            Mocker.GetMock<IManageCommandQueue>()
                .Verify(s => s.Push(It.IsAny<ReidentifyLibraryCommand>(), It.IsAny<CommandPriority>(), It.IsAny<CommandTrigger>()), times);
        }

        private void VerifyMarkedComplete(Times times)
        {
            Mocker.GetMock<IConfigService>()
                .VerifySet(s => s.LegacyMigrationCompleted = true, times);
        }

        [Test]
        public void should_start_reidentify_and_record_that_it_started()
        {
            Subject.RunMigration();

            VerifyReidentifyQueued(Times.Once());
            VerifyMarkedComplete(Times.Never());

            Mocker.GetMock<IConfigService>()
                .VerifySet(s => s.LegacyMigrationStarted = true, Times.Once());

            Mocker.GetMock<IAuthorService>()
                .Verify(s => s.UpdateAuthor(It.Is<Author>(a => a.MonitorNewItems == NewItemMonitorTypes.None)), Times.Once());
        }

        [Test]
        public void should_treat_existing_mappings_as_a_manual_reidentify_when_it_never_started_one()
        {
            GivenMappingRows();

            Subject.RunMigration();

            VerifyReidentifyQueued(Times.Never());
            VerifyMarkedComplete(Times.Once());
        }

        [Test]
        public void should_resume_rather_than_mark_complete_when_its_own_run_left_mappings_behind()
        {
            GivenMappingRows();
            GivenMigrationStartedEarlier();

            Subject.RunMigration();

            VerifyReidentifyQueued(Times.Once());
            VerifyMarkedComplete(Times.Never());
        }

        [Test]
        public void should_not_flip_monitoring_again_when_resuming()
        {
            GivenMigrationStartedEarlier();

            Subject.RunMigration();

            Mocker.GetMock<IAuthorService>()
                .Verify(s => s.UpdateAuthor(It.IsAny<Author>()), Times.Never());
        }

        [Test]
        public void should_mark_complete_when_reidentify_completes()
        {
            Subject.RunMigration();
            Subject.Handle(new CommandExecutedEvent(new CommandModel { Id = CommandId, Status = CommandStatus.Completed }));

            VerifyMarkedComplete(Times.Once());
        }

        [Test]
        public void should_not_mark_complete_when_reidentify_fails()
        {
            Subject.RunMigration();
            Subject.Handle(new CommandExecutedEvent(new CommandModel { Id = CommandId, Status = CommandStatus.Failed }));

            VerifyMarkedComplete(Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
