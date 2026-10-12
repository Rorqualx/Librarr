using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;

namespace NzbDrone.Core.MediaCover
{
    public interface IMapCoversToLocal
    {
        void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<MediaCover> covers);
        string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null);
        void EnsureBookCovers(Book book);
    }

    public class MediaCoverService :
        IHandle<BookAddedEvent>,
        IHandle<AuthorAddedEvent>,
        IHandleAsync<AuthorRefreshCompleteEvent>,
        IHandleAsync<AuthorDeletedEvent>,
        IHandleAsync<BookDeletedEvent>,
        IHandle<BookEditedEvent>,
        IMapCoversToLocal
    {
        // Was a spoofed Android/Dalvik string inherited from upstream Readarr,
        // where it existed to get cover images past Goodreads/Amazon. Librarr
        // fetches covers from OpenLibrary, whose politeness policy grants a 3x
        // rate allowance to clients that identify themselves honestly — and
        // who ask not to be used as a third-party backend at all. Pretending to
        // be a phone forfeited the allowance and misrepresented us.
        private static string UserAgent => MetadataUserAgent.For("cover images");

        private readonly IMediaCoverProxy _mediaCoverProxy;
        private readonly IImageResizer _resizer;
        private readonly IBookService _bookService;
        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly ICoverExistsSpecification _coverExistsSpecification;
        private readonly IConfigFileProvider _configFileProvider;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        private readonly string _coverRootFolder;

        // ImageSharp is slow on ARM (no hardware acceleration on mono yet)
        // So limit the number of concurrent resizing tasks
        private static SemaphoreSlim _semaphore = new SemaphoreSlim((int)Math.Ceiling(Environment.ProcessorCount / 2.0));

        public MediaCoverService(IMediaCoverProxy mediaCoverProxy,
                                 IImageResizer resizer,
                                 IBookService bookService,
                                 IHttpClient httpClient,
                                 IDiskProvider diskProvider,
                                 IAppFolderInfo appFolderInfo,
                                 ICoverExistsSpecification coverExistsSpecification,
                                 IConfigFileProvider configFileProvider,
                                 IEventAggregator eventAggregator,
                                 Logger logger)
        {
            _mediaCoverProxy = mediaCoverProxy;
            _resizer = resizer;
            _bookService = bookService;
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _coverExistsSpecification = coverExistsSpecification;
            _configFileProvider = configFileProvider;
            _eventAggregator = eventAggregator;
            _logger = logger;

            _coverRootFolder = appFolderInfo.GetMediaCoverPath();
        }

        public string GetCoverPath(int entityId, MediaCoverEntity coverEntity, MediaCoverTypes coverType, string extension, int? height = null)
        {
            var heightSuffix = height.HasValue ? "-" + height.ToString() : "";

            if (coverEntity == MediaCoverEntity.Book)
            {
                return Path.Combine(GetBookCoverPath(entityId), coverType.ToString().ToLower() + heightSuffix + GetExtension(coverType, extension));
            }

            return Path.Combine(GetAuthorCoverPath(entityId), coverType.ToString().ToLower() + heightSuffix + GetExtension(coverType, extension));
        }

        public void ConvertToLocalUrls(int entityId, MediaCoverEntity coverEntity, IEnumerable<MediaCover> covers)
        {
            if (entityId == 0)
            {
                // Author isn't in Readarr yet, map via a proxy to circument referrer issues
                foreach (var mediaCover in covers)
                {
                    // Idempotency guard: a cover that has already been through
                    // here must not have its proxy URL taken for a remote one.
                    // Wrapping it again registers a path-only
                    // `/MediaCoverProxy/<oldhash>/...` as though it were
                    // fetchable, and .NET HttpClient throws "file scheme not
                    // supported" when the proxy tries.
                    //
                    // This used to be the only thing standing between a cached
                    // OL search result and that failure, and it did not hold:
                    // it tested for "/MediaCoverProxy/" at the start of the
                    // URL, while RegisterUrl returns UrlBase + that, so with a
                    // URL base configured it never matched. OpenLibraryProxy
                    // now maps search results per call, so the same MediaCover
                    // is no longer handed to two requests -- but the guard
                    // stays, and now compares against what RegisterUrl
                    // actually produces.
                    //
                    // An already-proxied cover is re-registered from its
                    // RemoteUrl rather than skipped. The hash is derived from
                    // the URL, so the result is the same string; what it buys
                    // is a fresh 24 hours on the registration, for any caller
                    // that has held on to a cover for longer than that.
                    if (IsProxyUrl(mediaCover.Url))
                    {
                        if (mediaCover.RemoteUrl.IsNullOrWhiteSpace())
                        {
                            continue;
                        }
                    }
                    else
                    {
                        mediaCover.RemoteUrl = mediaCover.Url;
                    }

                    mediaCover.Url = _mediaCoverProxy.RegisterUrl(mediaCover.RemoteUrl);
                }
            }
            else
            {
                foreach (var mediaCover in covers)
                {
                    if (mediaCover.CoverType == MediaCoverTypes.Unknown)
                    {
                        continue;
                    }

                    var filePath = GetCoverPath(entityId, coverEntity, mediaCover.CoverType, mediaCover.Extension, null);

                    mediaCover.RemoteUrl = mediaCover.Url;

                    if (coverEntity == MediaCoverEntity.Book)
                    {
                        mediaCover.Url = _configFileProvider.UrlBase + @"/MediaCover/Books/" + entityId + "/" + mediaCover.CoverType.ToString().ToLower() + GetExtension(mediaCover.CoverType, mediaCover.Extension);
                    }
                    else
                    {
                        mediaCover.Url = _configFileProvider.UrlBase + @"/MediaCover/" + entityId + "/" + mediaCover.CoverType.ToString().ToLower() + GetExtension(mediaCover.CoverType, mediaCover.Extension);
                    }

                    if (_diskProvider.FileExists(filePath))
                    {
                        var lastWrite = _diskProvider.FileGetLastWrite(filePath);
                        mediaCover.Url += "?lastWrite=" + lastWrite.Ticks;
                    }
                }
            }
        }

        // The shape MediaCoverProxy.RegisterUrl returns, URL base included.
        private bool IsProxyUrl(string url)
        {
            return url != null && url.StartsWith(_configFileProvider.UrlBase + "/MediaCoverProxy/", StringComparison.Ordinal);
        }

        private string GetAuthorCoverPath(int authorId)
        {
            return Path.Combine(_coverRootFolder, authorId.ToString());
        }

        private string GetBookCoverPath(int bookId)
        {
            return Path.Combine(_coverRootFolder, "Books", bookId.ToString());
        }

        private void EnsureAuthorCovers(Author author)
        {
            var toResize = new List<Tuple<MediaCover, bool>>();

            foreach (var cover in author.Metadata.Value.Images)
            {
                if (cover.CoverType == MediaCoverTypes.Unknown)
                {
                    continue;
                }

                var fileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
                var alreadyExists = false;

                try
                {
                    var serverFileHeaders = GetServerHeaders(cover.Url);

                    alreadyExists = _coverExistsSpecification.AlreadyExists(serverFileHeaders.LastModified, GetContentLength(serverFileHeaders), fileName);

                    if (!alreadyExists)
                    {
                        DownloadCover(author, cover, serverFileHeaders.LastModified ?? DateTime.Now);
                    }
                }
                catch (HttpException e)
                {
                    _logger.Warn("Couldn't download media cover for {0}. {1}", author, e.Message);
                }
                catch (WebException e)
                {
                    _logger.Warn("Couldn't download media cover for {0}. {1}", author, e.Message);
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't download media cover for {0}", author);
                }

                toResize.Add(Tuple.Create(cover, alreadyExists));
            }

            try
            {
                _semaphore.Wait();

                foreach (var tuple in toResize)
                {
                    EnsureResizedCovers(author, tuple.Item1, !tuple.Item2);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void EnsureBookCovers(Book book)
        {
            // Some OL works return zero editions (e.g. "Hainish"
            // OL29311776W is a stub work with no edition records). The
            // mapper leaves Monitored unset across the empty list, so
            // book.Editions.Value.Single(Monitored) throws "Sequence
            // contains no matching element" and aborts the per-author
            // cover-download loop in HandleAsync. Skip these defensively
            // — there's no cover to download when there's no edition.
            var monitoredEdition = book.Editions.Value.FirstOrDefault(x => x.Monitored);

            // The API refuses a pin that is not an Open Library cover image, but
            // this is where the URL gets fetched and its extension becomes a file
            // name, so check again rather than trust whatever is in the database.
            var pinnedCoverUrl = book.PreferredCoverUrl;
            if (!pinnedCoverUrl.IsNullOrWhiteSpace() && !PinnedCoverUrl.IsAllowed(pinnedCoverUrl))
            {
                _logger.Warn("Ignoring pinned cover for {0}, it is not an image on {1}", book, PinnedCoverUrl.AllowedHost);
                pinnedCoverUrl = null;
            }

            if (monitoredEdition == null && pinnedCoverUrl.IsNullOrWhiteSpace())
            {
                return;
            }

            // User-pinned cover (cover-picker modal) takes priority over
            // whatever the mapper put on the monitored edition. Mapper
            // default (work.covers[0]) is the second choice; the edition
            // cover fallback URLs follow that in the Images list.
            var coverSources = !pinnedCoverUrl.IsNullOrWhiteSpace()
                ? new List<MediaCover> { new MediaCover(MediaCoverTypes.Cover, pinnedCoverUrl) }
                : monitoredEdition.Images.Where(e => e.CoverType == MediaCoverTypes.Cover).ToList();

            foreach (var cover in coverSources)
            {
                if (cover.CoverType == MediaCoverTypes.Unknown)
                {
                    continue;
                }

                var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);
                var alreadyExists = false;

                try
                {
                    var serverFileHeaders = GetServerHeaders(cover.Url);

                    alreadyExists = _coverExistsSpecification.AlreadyExists(serverFileHeaders.LastModified, GetContentLength(serverFileHeaders), fileName);

                    if (!alreadyExists)
                    {
                        DownloadBookCover(book, cover, serverFileHeaders.LastModified ?? DateTime.Now);
                    }
                }
                catch (HttpException e)
                {
                    _logger.Warn("Couldn't download media cover for {0}. {1}", book, e.Message);
                }
                catch (WebException e)
                {
                    _logger.Warn("Couldn't download media cover for {0}. {1}", book, e.Message);
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't download media cover for {0}", book);
                }
            }
        }

        private void DownloadCover(Author author, MediaCover cover, DateTime lastModified)
        {
            var fileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);

            _logger.Info("Downloading {0} for {1} {2}", cover.CoverType, author, cover.Url);
            _httpClient.DownloadFile(cover.Url, fileName, UserAgent, OpenLibraryCoverUrls.RateLimitFor(cover.Url));

            try
            {
                _diskProvider.FileSetLastWriteTime(fileName, lastModified);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to set modified date for {0} image for author {1}", cover.CoverType, author);
            }
        }

        private void DownloadBookCover(Book book, MediaCover cover, DateTime lastModified)
        {
            var fileName = GetCoverPath(book.Id, MediaCoverEntity.Book, cover.CoverType, cover.Extension, null);

            _logger.Info("Downloading {0} for {1} {2}", cover.CoverType, book, cover.Url);
            _httpClient.DownloadFile(cover.Url, fileName, UserAgent, OpenLibraryCoverUrls.RateLimitFor(cover.Url));

            try
            {
                _diskProvider.FileSetLastWriteTime(fileName, lastModified);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to set modified date for {0} image for book {1}", cover.CoverType, book);
            }
        }

        private void EnsureResizedCovers(Author author, MediaCover cover, bool forceResize, Book book = null)
        {
            var heights = GetDefaultHeights(cover.CoverType);

            foreach (var height in heights)
            {
                var mainFileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension);
                var resizeFileName = GetCoverPath(author.Id, MediaCoverEntity.Author, cover.CoverType, cover.Extension, height);

                if (forceResize || !_diskProvider.FileExists(resizeFileName) || _diskProvider.GetFileSize(resizeFileName) == 0)
                {
                    _logger.Debug("Resizing {0}-{1} for {2}", cover.CoverType, height, author);

                    try
                    {
                        _resizer.Resize(mainFileName, resizeFileName, height);
                    }
                    catch
                    {
                        _logger.Debug("Couldn't resize media cover {0}-{1} for author {2}, using full size image instead.", cover.CoverType, height, author);
                    }
                }
            }
        }

        private int[] GetDefaultHeights(MediaCoverTypes coverType)
        {
            switch (coverType)
            {
                default:
                    return new int[] { };

                case MediaCoverTypes.Poster:
                case MediaCoverTypes.Disc:
                case MediaCoverTypes.Cover:
                case MediaCoverTypes.Logo:
                case MediaCoverTypes.Headshot:
                    return new[] { 500, 250 };

                case MediaCoverTypes.Banner:
                    return new[] { 70, 35 };

                case MediaCoverTypes.Fanart:
                case MediaCoverTypes.Screenshot:
                    return new[] { 360, 180 };
            }
        }

        private string GetExtension(MediaCoverTypes coverType, string defaultExtension)
        {
            return coverType switch
            {
                MediaCoverTypes.Clearlogo => ".png",
                _ => defaultExtension
            };
        }

        private HttpHeader GetServerHeaders(string url)
        {
            // Goodreads doesn't allow a HEAD, so request a zero byte range instead
            var request = new HttpRequest(url)
            {
                AllowAutoRedirect = true,
            };

            request.Headers.Add("Range", "bytes=0-0");
            request.Headers.Add("User-Agent", UserAgent);

            return _httpClient.Get(request).Headers;
        }

        private long? GetContentLength(HttpHeader headers)
        {
            var range = headers.Get("content-range");

            if (range == null)
            {
                return null;
            }

            var split = range.Split('/');
            if (split.Length == 2 && long.TryParse(split[1], out var length))
            {
                return length;
            }

            return null;
        }

        public void Handle(BookAddedEvent message)
        {
            // Pull the cover for the explicitly-added book immediately,
            // before the user navigates back to the Books page. The
            // AuthorRefreshCompleteEvent handler below covers all books
            // at the end of a per-author refresh, but that fires
            // minutes after Add (the per-author refresh processes the
            // author's full works list). Downloading just one cover
            // here is sub-second and is idempotent (the same handler
            // will be called again at refresh completion and the
            // AlreadyExists spec will short-circuit it).
            try
            {
                EnsureBookCovers(message.Book);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't pre-download cover for {0}", message.Book);
            }
        }

        public void Handle(BookEditedEvent message)
        {
            // Cover-picker flow: when the user pins a cover via the
            // modal, BookController.UpdateResource fires BookEditedEvent.
            // Re-run EnsureBookCovers so AlreadyExistsSpecification's
            // URL-change check (CoverAlreadyExistsSpecification.cs:30)
            // sees the new URL and triggers a redownload.
            try
            {
                EnsureBookCovers(message.Book);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't refresh cover for {0}", message.Book);
            }
        }

        public void Handle(AuthorAddedEvent message)
        {
            // Same eager-download pattern for newly-added authors so
            // the photo lands on disk before the user navigates to the
            // Authors page. AuthorRefreshCompleteEvent does the same
            // job at the end of the per-author refresh, but with
            // MonitorNewItems=None the refresh may complete without
            // inserting any books (and may still take a while to walk
            // the empty-Added path). EnsureAuthorCovers is idempotent
            // via the AlreadyExists spec.
            try
            {
                EnsureAuthorCovers(message.Author);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Couldn't pre-download cover for {0}", message.Author);
            }
        }

        public void HandleAsync(AuthorRefreshCompleteEvent message)
        {
            EnsureAuthorCovers(message.Author);

            var books = _bookService.GetBooksByAuthor(message.Author.Id);
            foreach (var book in books)
            {
                try
                {
                    EnsureBookCovers(book);
                }
                catch (Exception ex)
                {
                    // Defense in depth — a single book that surprises
                    // EnsureBookCovers must not abort the rest of the
                    // author's cover-download pass. Without this, an
                    // exception 30 books into a 250-book Le Guin refresh
                    // leaves the remaining 220 books permanently
                    // coverless until another refresh fires.
                    _logger.Warn(ex, "Couldn't ensure covers for {0}", book);
                }
            }

            _eventAggregator.PublishEvent(new MediaCoversUpdatedEvent(message.Author));
        }

        public void HandleAsync(AuthorDeletedEvent message)
        {
            var path = GetAuthorCoverPath(message.Author.Id);
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }

        public void HandleAsync(BookDeletedEvent message)
        {
            var path = GetBookCoverPath(message.Book.Id);
            if (_diskProvider.FolderExists(path))
            {
                _diskProvider.DeleteFolder(path, true);
            }
        }
    }
}
