using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CalibreFormatsFixture
    {
        private static BookFile GivenFile(string path, Quality quality)
        {
            return new BookFile { Path = path, Quality = new QualityModel(quality) };
        }

        private static CalibreBook GivenRecord(params string[] formats)
        {
            return new CalibreBook { Formats = formats.ToDictionary(x => x, x => new CalibreBookFormat()) };
        }

        [TestCase("/books/a.m4b", 12, true)]
        [TestCase("/books/a.MP3", 0, true)]
        [TestCase("/books/a.epub", 12, true)]
        [TestCase("/books/a.epub", 3, false)]
        [TestCase("/books/a.pdf", 0, false)]
        public void should_treat_a_file_as_audio_by_quality_or_extension(string path, int qualityId, bool expected)
        {
            CalibreFormats.IsAudio(GivenFile(path, Quality.FindById(qualityId))).Should().Be(expected);
        }

        [Test]
        public void should_own_only_its_own_format_when_nothing_is_configured()
        {
            var owned = CalibreFormats.OwnedBy(GivenFile("/books/a.epub", Quality.EPUB), GivenRecord("epub", "mobi", "pdf"), new CalibreSettings());

            owned.Should().BeEquivalentTo("epub");
        }

        // Handed back as Calibre spelled them, whatever case the file's
        // extension or the settings use.
        [Test]
        public void should_match_formats_ignoring_case_and_return_calibres_spelling()
        {
            var settings = new CalibreSettings { OutputFormat = " mobi ,AZW3" };

            var owned = CalibreFormats.OwnedBy(GivenFile("/books/a.EPUB", Quality.EPUB), GivenRecord("epub", "MOBI", "azw3", "pdf"), settings);

            owned.Should().BeEquivalentTo("epub", "MOBI", "azw3");
        }

        [Test]
        public void should_not_own_a_format_another_file_points_at()
        {
            var settings = new CalibreSettings { OutputFormat = "mobi" };
            var siblings = new[] { GivenFile("/books/a.mobi", Quality.MOBI) };

            var owned = CalibreFormats.OwnedBy(GivenFile("/books/a.epub", Quality.EPUB), GivenRecord("epub", "mobi"), settings, siblings);

            owned.Should().BeEquivalentTo("epub");
        }

        [Test]
        public void should_never_own_an_audio_format()
        {
            var settings = new CalibreSettings { OutputFormat = "mp3,m4b" };

            var owned = CalibreFormats.OwnedBy(GivenFile("/books/a.m4b", Quality.M4B), GivenRecord("epub", "mp3", "m4b"), settings);

            owned.Should().BeEmpty();
        }

        [Test]
        public void should_own_nothing_on_a_record_without_formats()
        {
            CalibreFormats.OwnedBy(GivenFile("/books/a.epub", Quality.EPUB), new CalibreBook(), null).Should().BeEmpty();
        }
    }
}
