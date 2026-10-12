using System;
using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Download.Pending.PendingReleaseServiceTests
{
    [TestFixture]
    public class PendingQueueFixture : CoreTest<PendingReleaseService>
    {
        private List<PendingRelease> _pending;
        private Author _author;
        private Book _book;

        [SetUp]
        public void Setup()
        {
            _pending = new List<PendingRelease>();

            // The ebook profile ranks FLAC above MP3; the audiobook profile
            // ranks them the other way round, so the two disagree about
            // which audio release is best.
            _author = Builder<Author>.CreateNew()
                                     .With(a => a.QualityProfile = new QualityProfile { Items = Qualities.QualityFixture.GetDefaultQualities() })
                                     .With(a => a.AudiobookQualityProfileId = 2)
                                     .With(a => a.AudiobookQualityProfile = new QualityProfile { Id = 2, Items = Qualities.QualityFixture.GetDefaultQualities(Quality.FLAC, Quality.MP3) })
                                     .With(a => a.Tags = new HashSet<int>())
                                     .Build();

            _book = Builder<Book>.CreateNew()
                                 .Build();

            Mocker.GetMock<IPendingReleaseRepository>()
                  .Setup(s => s.WithoutFallback())
                  .Returns(_pending);

            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.GetAuthors(It.IsAny<IEnumerable<int>>()))
                  .Returns(new List<Author> { _author });

            Mocker.GetMock<IParsingService>()
                  .Setup(s => s.GetBooks(It.IsAny<ParsedBookInfo>(), It.IsAny<Author>(), null))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IDelayProfileService>()
                  .Setup(s => s.AllForTags(It.IsAny<HashSet<int>>()))
                  .Returns(new List<DelayProfile> { new DelayProfile() });

            Mocker.GetMock<IDelayProfileService>()
                  .Setup(s => s.BestForTags(It.IsAny<HashSet<int>>()))
                  .Returns(new DelayProfile());

            Mocker.GetMock<ITaskManager>()
                  .Setup(s => s.GetNextExecution(It.IsAny<Type>()))
                  .Returns(DateTime.UtcNow.AddMinutes(15));
        }

        private void AddPending(int id, Quality quality)
        {
            _pending.Add(new PendingRelease
            {
                Id = id,
                AuthorId = _author.Id,
                Title = "Author.Title-Book.Title-" + quality.Name,
                ParsedBookInfo = new ParsedBookInfo { BookTitle = "Book", Quality = new QualityModel(quality) },
                Release = Builder<ReleaseInfo>.CreateNew().Build()
            });
        }

        [Test]
        public void should_rank_pending_audio_releases_by_the_audiobook_profile()
        {
            AddPending(1, Quality.MP3);
            AddPending(2, Quality.FLAC);

            var queue = Subject.GetPendingQueue();

            queue.Should().ContainSingle();
            queue.Single().Quality.Quality.Should().Be(Quality.MP3);
        }

        [Test]
        public void should_rank_pending_text_releases_by_the_ebook_profile()
        {
            AddPending(1, Quality.AZW3);
            AddPending(2, Quality.MOBI);

            var queue = Subject.GetPendingQueue();

            queue.Should().ContainSingle();
            queue.Single().Quality.Quality.Should().Be(Quality.AZW3);
        }

        [Test]
        public void should_keep_a_pending_ebook_and_a_pending_audiobook_for_the_same_book()
        {
            AddPending(1, Quality.EPUB);
            AddPending(2, Quality.MP3);

            var queue = Subject.GetPendingQueue();

            queue.Select(q => q.Quality.Quality).Should().BeEquivalentTo(new[] { Quality.EPUB, Quality.MP3 });
        }
    }
}
