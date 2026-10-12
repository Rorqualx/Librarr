using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaFiles.MediaFileDeletionService
{
    // Deleting one file used to delete the whole Calibre book. A Calibre
    // record holds every format of a book, so that took formats Librarr was
    // never asked about along with the one it was.
    [TestFixture]
    public class DeleteCalibreFileFixture : CoreTest<Core.MediaFiles.MediaFileDeletionService>
    {
        private const int CalibreId = 5;

        private static readonly string BookFolder = @"C:\Calibre\Author Name\Book (5)".AsOsAgnostic();

        private CalibreSettings _settings;
        private BookFile _bookFile;
        private List<BookFile> _filesForEdition;

        [SetUp]
        public void Setup()
        {
            _settings = new CalibreSettings { Host = "localhost", Library = "books" };

            _bookFile = GivenFile(1, "epub", Quality.EPUB);
            _filesForEdition = new List<BookFile> { _bookFile };

            Mocker.GetMock<IRootFolderService>()
                  .Setup(x => x.GetBestRootFolder(It.IsAny<string>()))
                  .Returns(new RootFolder { IsCalibreLibrary = true, CalibreSettings = _settings });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IMediaFileService>()
                  .Setup(s => s.GetFilesByEdition(It.IsAny<int>()))
                  .Returns(() => _filesForEdition);
        }

        private static BookFile GivenFile(int id, string extension, Quality quality)
        {
            return new BookFile
            {
                Id = id,
                Path = System.IO.Path.Combine(BookFolder, "Book - Author Name." + extension),
                Quality = new QualityModel(quality),
                CalibreId = CalibreId,
                EditionId = 1
            };
        }

        private void GivenCalibreRecord(params string[] formats)
        {
            Mocker.GetMock<ICalibreProxy>()
                  .Setup(c => c.GetBook(CalibreId, _settings))
                  .Returns(new CalibreBook
                  {
                      Id = CalibreId,
                      Formats = formats.ToDictionary(x => x, x => new CalibreBookFormat())
                  });
        }

        private void VerifyOnlyRemoved(params string[] formats)
        {
            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.RemoveFormats(CalibreId, It.Is<IEnumerable<string>>(f => f.OrderBy(x => x).SequenceEqual(formats.OrderBy(x => x))), _settings), Times.Once());

            VerifyRecordKept();
        }

        private void VerifyRecordKept()
        {
            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.DeleteBook(It.IsAny<BookFile>(), It.IsAny<CalibreSettings>()), Times.Never());

            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.DeleteBooks(It.IsAny<List<BookFile>>(), It.IsAny<CalibreSettings>()), Times.Never());
        }

        private void VerifyRecordDeleted()
        {
            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.DeleteBook(_bookFile, _settings), Times.Once());

            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.RemoveFormats(It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CalibreSettings>()), Times.Never());
        }

        [Test]
        public void should_remove_only_the_deleted_files_format_when_other_formats_remain()
        {
            GivenCalibreRecord("epub", "pdf");

            Subject.DeleteTrackFile(_bookFile);

            VerifyOnlyRemoved("epub");
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_bookFile, DeleteMediaFileReason.Manual), Times.Once());
        }

        [Test]
        public void should_delete_the_calibre_record_when_no_format_remains()
        {
            GivenCalibreRecord("epub");

            Subject.DeleteTrackFile(_bookFile);

            VerifyRecordDeleted();
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(_bookFile, DeleteMediaFileReason.Manual), Times.Once());
        }

        // The conversions Librarr asked Calibre for are not tracked as files of
        // their own, so they go with the file they were made from. Otherwise
        // deleting a book would always leave its MOBI behind.
        [Test]
        public void should_delete_the_calibre_record_when_only_configured_output_formats_remain()
        {
            _settings.OutputFormat = "mobi, azw3";
            GivenCalibreRecord("epub", "mobi");

            Subject.DeleteTrackFile(_bookFile);

            VerifyRecordDeleted();
        }

        [Test]
        public void should_keep_a_format_another_book_file_points_at()
        {
            _settings.OutputFormat = "mobi";
            _filesForEdition.Add(GivenFile(2, "mobi", Quality.MOBI));
            GivenCalibreRecord("epub", "mobi");

            Subject.DeleteTrackFile(_bookFile);

            VerifyOnlyRemoved("epub");
        }

        [Test]
        public void should_keep_an_audio_format_when_deleting_an_ebook()
        {
            _settings.OutputFormat = "mp3";
            GivenCalibreRecord("epub", "m4b", "mp3");

            Subject.DeleteTrackFile(_bookFile);

            VerifyOnlyRemoved("epub");
        }

        [Test]
        public void should_keep_the_calibre_record_when_the_deleted_files_format_is_already_gone()
        {
            GivenCalibreRecord("pdf");

            Subject.DeleteTrackFile(_bookFile);

            VerifyRecordKept();

            Mocker.GetMock<ICalibreProxy>()
                  .Verify(c => c.RemoveFormats(It.IsAny<int>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CalibreSettings>()), Times.Never());
        }

        // Audio is never Calibre's to manage, whatever id the row carries.
        [Test]
        public void should_not_send_an_audio_file_to_calibre()
        {
            var audioFile = GivenFile(3, "m4b", Quality.M4B);

            Subject.DeleteTrackFile(audioFile, "Author Name");

            Mocker.GetMock<ICalibreProxy>().VerifyNoOtherCalls();
            Mocker.GetMock<IRecycleBinProvider>().Verify(v => v.DeleteFile(audioFile.Path, "Author Name"), Times.Once());
            Mocker.GetMock<IMediaFileService>().Verify(v => v.Delete(audioFile, DeleteMediaFileReason.Manual), Times.Once());
        }
    }
}
