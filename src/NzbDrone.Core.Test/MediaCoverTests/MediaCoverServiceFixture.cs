using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MediaCoverTests
{
    [TestFixture]
    public class MediaCoverServiceFixture : CoreTest<MediaCoverService>
    {
        private Author _author;
        private Book _book;
        private Edition _edition;
        private HttpResponse _httpResponse;

        [SetUp]
        public void Setup()
        {
            Mocker.SetConstant<IAppFolderInfo>(new AppFolderInfo(Mocker.Resolve<IStartupContext>()));

            _author = Builder<Author>.CreateNew()
                .With(v => v.Id = 2)
                .With(v => v.Metadata.Value.Images = new List<MediaCover.MediaCover> { new MediaCover.MediaCover(MediaCoverTypes.Poster, "") })
                .Build();

            _edition = Builder<Edition>.CreateNew()
                .With(v => v.Id = 8)
                .With(v => v.Images = new List<MediaCover.MediaCover> { new MediaCover.MediaCover(MediaCoverTypes.Cover, "") })
                .With(v => v.Monitored = true)
                .Build();

            _book = Builder<Book>.CreateNew()
                .With(v => v.Id = 4)
                .With(v => v.Editions = new List<Edition> { _edition })
                .With(v => v.PreferredCoverUrl = null)
                .Build();

            _httpResponse = new HttpResponse(null, new HttpHeader(), "");
            Mocker.GetMock<IHttpClient>().Setup(c => c.Get(It.IsAny<HttpRequest>())).Returns(_httpResponse);
        }

        [Test]
        public void should_download_pinned_open_library_cover()
        {
            const string pinned = "https://covers.openlibrary.org/b/id/8231856-L.jpg";

            _book.PreferredCoverUrl = pinned;

            Subject.EnsureBookCovers(_book);

            Mocker.GetMock<IHttpClient>()
                  .Verify(c => c.DownloadFile(pinned, It.Is<string>(f => f.EndsWith("cover.jpg")), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Once());
        }

        // A pin that was stored before the API started refusing these, or that
        // was written straight into the database, must not be fetched.
        [TestCase("http://127.0.0.1:8787/initialize.svg")]
        [TestCase("https://localhost/cover.svg")]
        [TestCase("https://example.com/cover.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.svg")]
        [TestCase("file:///etc/passwd")]
        public void should_ignore_pinned_cover_that_is_not_an_open_library_image(string pinned)
        {
            const string editionCover = "https://covers.openlibrary.org/b/id/42-L.jpg";

            _edition.Images = new List<MediaCover.MediaCover> { new MediaCover.MediaCover(MediaCoverTypes.Cover, editionCover) };
            _book.PreferredCoverUrl = pinned;

            Subject.EnsureBookCovers(_book);

            Mocker.GetMock<IHttpClient>()
                  .Verify(c => c.Get(It.Is<HttpRequest>(r => r.Url.FullUri != editionCover)), Times.Never());

            Mocker.GetMock<IHttpClient>()
                  .Verify(c => c.DownloadFile(It.Is<string>(u => u != editionCover), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never());

            Mocker.GetMock<IHttpClient>()
                  .Verify(c => c.DownloadFile(editionCover, It.Is<string>(f => f.EndsWith("cover.jpg")), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Once());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_not_fetch_anything_for_unusable_pin_when_no_edition_is_monitored()
        {
            _edition.Monitored = false;
            _book.PreferredCoverUrl = "http://127.0.0.1:8787/initialize.svg";

            Subject.EnsureBookCovers(_book);

            Mocker.GetMock<IHttpClient>().Verify(c => c.Get(It.IsAny<HttpRequest>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [TestCase(".png")]
        [TestCase(".jpg")]
        public void should_convert_cover_urls_to_local(string extension)
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        Url = "http://dummy.com/test" + extension,
                        CoverType = MediaCoverTypes.Banner
                    }
                };

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(1234));

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);

            covers.Single().Url.Should().Be("/MediaCover/12/banner" + extension + "?lastWrite=1234");
        }

        [TestCase(".png")]
        [TestCase(".jpg")]
        public void convert_to_local_url_should_not_change_extension(string extension)
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        Url = "http://dummy.com/test" + extension,
                        CoverType = MediaCoverTypes.Banner
                    }
                };

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(1234));

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);

            covers.Single().Extension.Should().Be(extension);
        }

        [TestCase(".png")]
        [TestCase(".jpg")]
        public void should_convert_book_cover_urls_to_local(string extension)
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        Url = "http://dummy.com/test" + extension,
                        CoverType = MediaCoverTypes.Disc
                    }
                };

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileGetLastWrite(It.IsAny<string>()))
                  .Returns(new DateTime(1234));

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.ConvertToLocalUrls(6, MediaCoverEntity.Book, covers);

            covers.Single().Url.Should().Be("/MediaCover/Books/6/disc" + extension + "?lastWrite=1234");
        }

        [TestCase(".png")]
        [TestCase(".jpg")]
        public void should_convert_media_urls_to_local_without_time_if_file_doesnt_exist(string extension)
        {
            var covers = new List<MediaCover.MediaCover>
                {
                    new MediaCover.MediaCover
                    {
                        Url = "http://dummy.com/test" + extension,
                        CoverType = MediaCoverTypes.Banner
                    }
                };

            var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Media", "NonExistant.mp4");
            var fileInfo = new FileInfo(path);

            Mocker.GetMock<IDiskProvider>()
                .Setup(c => c.GetFileInfo(It.IsAny<string>()))
                .Returns((FileInfoBase)fileInfo);

            Subject.ConvertToLocalUrls(12, MediaCoverEntity.Author, covers);

            covers.Single().Url.Should().Be("/MediaCover/12/banner" + extension);
        }

        // Entity id 0 is a lookup result that is not in the library yet: its
        // cover is served through MediaCoverProxy, and the URL handed back is
        // "{UrlBase}/MediaCoverProxy/{hash}/{file}".
        //
        // The guard against wrapping a URL a second time compared against
        // "/MediaCoverProxy/" alone, so on an install with a URL base it never
        // matched: converting the same cover twice registered the proxy's own
        // path as a remote URL, and the cover it then pointed at could not be
        // fetched. OpenLibraryProxy no longer hands the same instances to two
        // callers, which was how a cover came to be converted twice -- but a
        // guard that only works without a URL base is not a guard.
        [TestCase("")]
        [TestCase("/librarr")]
        public void should_not_wrap_a_proxied_cover_url_a_second_time(string urlBase)
        {
            const string Remote = "https://covers.openlibrary.org/b/id/11481354-L.jpg?default=false";

            Mocker.GetMock<NzbDrone.Core.Configuration.IConfigFileProvider>()
                  .SetupGet(c => c.UrlBase)
                  .Returns(urlBase);

            Mocker.GetMock<IMediaCoverProxy>()
                  .Setup(c => c.RegisterUrl(It.IsAny<string>()))
                  .Returns((string url) => urlBase + "/MediaCoverProxy/abc123/" + Path.GetFileName(url));

            var covers = new List<MediaCover.MediaCover>
            {
                new MediaCover.MediaCover(MediaCoverTypes.Cover, Remote)
            };

            Subject.ConvertToLocalUrls(0, MediaCoverEntity.Book, covers);

            var proxied = covers.Single().Url;
            proxied.Should().StartWith(urlBase + "/MediaCoverProxy/");

            Subject.ConvertToLocalUrls(0, MediaCoverEntity.Book, covers);

            covers.Single().Url.Should().Be(proxied);
            covers.Single().RemoteUrl.Should().Be(Remote, "the second pass must not overwrite the real remote URL with the proxy path");

            // Registered again, which renews the proxy's 24-hour entry -- but
            // only ever under the remote URL, never the proxy's own path.
            Mocker.GetMock<IMediaCoverProxy>()
                  .Verify(c => c.RegisterUrl(Remote), Times.Exactly(2));
            Mocker.GetMock<IMediaCoverProxy>()
                  .Verify(c => c.RegisterUrl(It.Is<string>(u => u != Remote)), Times.Never());
        }

        [Test]
        public void should_resize_covers_if_main_downloaded()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>()))
                  .Returns(false);

            Mocker.GetMock<IBookService>()
                  .Setup(v => v.GetBooksByAuthor(It.IsAny<int>()))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Subject.HandleAsync(new AuthorRefreshCompleteEvent(_author));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_resize_covers_if_missing()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IBookService>()
                  .Setup(v => v.GetBooksByAuthor(It.IsAny<int>()))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(false);

            Subject.HandleAsync(new AuthorRefreshCompleteEvent(_author));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_not_resize_covers_if_exists()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IBookService>()
                  .Setup(v => v.GetBooksByAuthor(It.IsAny<int>()))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.GetFileSize(It.IsAny<string>()))
                  .Returns(1000);

            Subject.HandleAsync(new AuthorRefreshCompleteEvent(_author));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Never());
        }

        [Test]
        public void should_resize_covers_if_existing_is_empty()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IBookService>()
                  .Setup(v => v.GetBooksByAuthor(It.IsAny<int>()))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.GetFileSize(It.IsAny<string>()))
                  .Returns(0);

            Subject.HandleAsync(new AuthorRefreshCompleteEvent(_author));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }

        [Test]
        public void should_log_error_if_resize_failed()
        {
            Mocker.GetMock<ICoverExistsSpecification>()
                  .Setup(v => v.AlreadyExists(It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>()))
                  .Returns(true);

            Mocker.GetMock<IDiskProvider>()
                  .Setup(v => v.FileExists(It.IsAny<string>()))
                  .Returns(false);

            Mocker.GetMock<IBookService>()
                  .Setup(v => v.GetBooksByAuthor(It.IsAny<int>()))
                  .Returns(new List<Book> { _book });

            Mocker.GetMock<IImageResizer>()
                  .Setup(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
                  .Throws<ApplicationException>();

            Subject.HandleAsync(new AuthorRefreshCompleteEvent(_author));

            Mocker.GetMock<IImageResizer>()
                  .Verify(v => v.Resize(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()), Times.Exactly(2));
        }
    }
}
