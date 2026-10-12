using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles
{
    [TestFixture]
    public class CalibreProxyFixture : CoreTest<CalibreProxy>
    {
        private Mock<IHttpClient> _httpClient;
        private CalibreSettings _settings;

        [SetUp]
        public void Setup()
        {
            // CoreTest registers a real client; nothing here may reach a server.
            _httpClient = new Mock<IHttpClient>(MockBehavior.Strict);
            Mocker.SetConstant<IHttpClient>(_httpClient.Object);

            _settings = new CalibreSettings { Host = "localhost", Library = "books", OutputFormat = "epub,mobi" };
        }

        private static BookFile GivenFile(string fileName, Quality quality, int calibreId = 0)
        {
            return new BookFile
            {
                Path = Path.Combine(@"C:\Downloads".AsOsAgnostic(), fileName),
                Quality = new QualityModel(quality),
                CalibreId = calibreId
            };
        }

        // An audiobook used to be posted as a new Calibre book and then, with
        // output formats configured, offered to Calibre for conversion to an
        // ebook. Neither request may be made.
        [TestCase("book.m4b", 0)]
        [TestCase("book.mp3", 0)]
        [TestCase("book.m4b", 5)]
        public void should_not_send_an_audiobook_to_calibre(string fileName, int calibreId)
        {
            var file = GivenFile(fileName, MediaFileExtensions.GetQualityForExtension(Path.GetExtension(fileName)), calibreId);

            Assert.Throws<CalibreException>(() => Subject.AddAndConvert(file, _settings));

            _httpClient.VerifyNoOtherCalls();
            file.CalibreId.Should().Be(calibreId);
        }

        // The extension decides on its own: a file whose quality was never
        // detected reports as text.
        [Test]
        public void should_not_send_an_audio_file_of_unknown_quality_to_calibre()
        {
            var file = GivenFile("book.flac", Quality.Unknown);

            Assert.Throws<CalibreException>(() => Subject.AddAndConvert(file, _settings));

            _httpClient.VerifyNoOtherCalls();
        }

        [Test]
        public void should_not_call_calibre_when_there_are_no_books_to_delete()
        {
            Subject.DeleteBooks(new List<BookFile> { GivenFile("book.epub", Quality.EPUB) }, _settings);

            _httpClient.VerifyNoOtherCalls();
        }
    }
}
