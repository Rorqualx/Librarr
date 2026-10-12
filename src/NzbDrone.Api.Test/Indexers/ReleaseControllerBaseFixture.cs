using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Indexers;

namespace NzbDrone.Api.Test.Indexers
{
    [TestFixture]
    public class ReleaseControllerBaseFixture : TestBase
    {
        private Author _author;

        [SetUp]
        public void Setup()
        {
            // MP3 sits at index 0 of the ebook profile and index 1 of the
            // audiobook profile, so the weight shows which one was read.
            // (Index * 100, plus 1 for the default revision version.)
            _author = new Author
            {
                QualityProfile = new QualityProfile { Items = GivenItems(Quality.MP3, Quality.EPUB) },
                AudiobookQualityProfileId = 2,
                AudiobookQualityProfile = new QualityProfile { Id = 2, Items = GivenItems(Quality.FLAC, Quality.MP3) }
            };
        }

        private static List<QualityProfileQualityItem> GivenItems(params Quality[] qualities)
        {
            var items = new List<QualityProfileQualityItem>();

            foreach (var quality in qualities)
            {
                items.Add(new QualityProfileQualityItem { Quality = quality, Allowed = true });
            }

            return items;
        }

        private DownloadDecision GivenDecision(Quality quality)
        {
            return new DownloadDecision(new RemoteBook
            {
                Author = _author,
                Books = new List<Book> { new Book() },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality) },
                Release = new ReleaseInfo { Title = "Author - Book" },
                CustomFormats = new List<CustomFormat>()
            });
        }

        [Test]
        public void should_weigh_an_audio_release_by_the_audiobook_profile()
        {
            var resource = new TestReleaseController().Map(GivenDecision(Quality.MP3));

            resource.QualityWeight.Should().Be(101);
        }

        [Test]
        public void should_weigh_a_text_release_by_the_ebook_profile()
        {
            var resource = new TestReleaseController().Map(GivenDecision(Quality.EPUB));

            resource.QualityWeight.Should().Be(101);
        }

        [Test]
        public void should_weigh_an_audio_release_by_the_ebook_profile_for_a_single_format_author()
        {
            _author.AudiobookQualityProfileId = 0;

            var resource = new TestReleaseController().Map(GivenDecision(Quality.MP3));

            resource.QualityWeight.Should().Be(1);
        }

        private class TestReleaseController : ReleaseControllerBase
        {
            public ReleaseResource Map(DownloadDecision decision)
            {
                return MapDecision(decision, 0);
            }
        }
    }
}
