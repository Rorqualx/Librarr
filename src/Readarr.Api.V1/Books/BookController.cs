using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore.Events;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;
using NzbDrone.SignalR;
using Readarr.Http;

namespace Readarr.Api.V1.Books
{
    [V1ApiController]
    public class BookController : BookControllerWithSignalR,
        IHandle<BookGrabbedEvent>,
        IHandle<BookEditedEvent>,
        IHandle<BookUpdatedEvent>,
        IHandle<BookDeletedEvent>,
        IHandle<BookImportedEvent>,
        IHandle<TrackImportedEvent>,
        IHandle<BookFileDeletedEvent>
    {
        protected readonly IAuthorService _authorService;
        protected readonly IEditionService _editionService;
        protected readonly IAddBookService _addBookService;
        protected readonly OpenLibraryProxy _openLibraryProxy;
        protected readonly MetadataSourceFactory _metadataSourceFactory;

        private const string PinnedCoverUrlMessage = "Must be an https jpg, png or gif image on " + PinnedCoverUrl.AllowedHost;

        public BookController(IAuthorService authorService,
                          IBookService bookService,
                          IAddBookService addBookService,
                          IEditionService editionService,
                          ISeriesBookLinkService seriesBookLinkService,
                          IAuthorStatisticsService authorStatisticsService,
                          IMapCoversToLocal coverMapper,
                          IUpgradableSpecification upgradableSpecification,
                          IBroadcastSignalRMessage signalRBroadcaster,
                          QualityProfileExistsValidator qualityProfileExistsValidator,
                          MetadataProfileExistsValidator metadataProfileExistsValidator,
                          OpenLibraryProxy openLibraryProxy,
                          MetadataSourceFactory metadataSourceFactory)

        : base(bookService, seriesBookLinkService, authorStatisticsService, coverMapper, upgradableSpecification, signalRBroadcaster)
        {
            _authorService = authorService;
            _editionService = editionService;
            _addBookService = addBookService;
            _openLibraryProxy = openLibraryProxy;
            _metadataSourceFactory = metadataSourceFactory;

            // The full-resource routes carry the pinned cover too, so it is held
            // to the same rule there as on the dedicated cover route.
            SharedValidator.RuleFor(s => s.PreferredCoverUrl).Must(PinnedCoverUrl.IsAllowedOrEmpty).WithMessage(PinnedCoverUrlMessage);

            PostValidator.RuleFor(s => s.ForeignBookId).NotEmpty();
            PostValidator.RuleFor(s => s.Author.QualityProfileId).SetValidator(qualityProfileExistsValidator);
            PostValidator.RuleFor(s => s.Author.MetadataProfileId).SetValidator(metadataProfileExistsValidator);
            PostValidator.RuleFor(s => s.Author.RootFolderPath).IsValidPath().When(s => s.Author.Path.IsNullOrWhiteSpace());
            PostValidator.RuleFor(s => s.Author.ForeignAuthorId).NotEmpty();
        }

        [HttpGet]
        public List<BookResource> GetBooks([FromQuery]int? authorId,
            [FromQuery]List<int> bookIds,
            [FromQuery]string titleSlug,
            [FromQuery]bool includeAllAuthorBooks = false)
        {
            if (!authorId.HasValue && !bookIds.Any() && titleSlug.IsNullOrWhiteSpace())
            {
                var editionTask = Task.Run(() => _editionService.GetAllMonitoredEditions());
                var metadataTask = Task.Run(() => _authorService.GetAllAuthors());
                var books = _bookService.GetAllBooks();

                var editions = editionTask.GetAwaiter().GetResult().GroupBy(x => x.BookId).ToDictionary(x => x.Key, y => y.ToList());

                var authors = metadataTask.GetAwaiter().GetResult().ToDictionary(x => x.AuthorMetadataId);

                foreach (var book in books)
                {
                    book.Author = authors[book.AuthorMetadataId];
                    if (editions.TryGetValue(book.Id, out var bookEditions))
                    {
                        book.Editions = bookEditions;
                    }
                    else
                    {
                        book.Editions = new List<Edition>();
                    }
                }

                return MapToResource(books, false);
            }

            if (authorId.HasValue)
            {
                var books = _bookService.GetBooksByAuthor(authorId.Value);

                var author = _authorService.GetAuthor(authorId.Value);
                var editions = _editionService.GetEditionsByAuthor(authorId.Value)
                    .GroupBy(x => x.BookId)
                    .ToDictionary(x => x.Key, y => y.ToList());

                foreach (var book in books)
                {
                    book.Author = author;
                    if (editions.TryGetValue(book.Id, out var bookEditions))
                    {
                        book.Editions = bookEditions;
                    }
                    else
                    {
                        book.Editions = new List<Edition>();
                    }
                }

                return MapToResource(books, false);
            }

            if (titleSlug.IsNotNullOrWhiteSpace())
            {
                var book = _bookService.FindBySlug(titleSlug);

                if (book == null)
                {
                    return MapToResource(new List<Book>(), false);
                }

                if (includeAllAuthorBooks)
                {
                    return MapToResource(_bookService.GetBooksByAuthor(book.AuthorId), false);
                }
                else
                {
                    return MapToResource(new List<Book> { book }, false);
                }
            }

            return MapToResource(_bookService.GetBooks(bookIds), false);
        }

        [HttpGet("{id:int}/overview")]
        public object Overview(int id)
        {
            // Empty, not a 500, for a book that has no editions yet
            var overview = _editionService.GetEditionsByBook(id).PrimaryEdition()?.Overview ?? string.Empty;
            return new
            {
                id,
                overview
            };
        }

        // Pin a specific cover URL from the cover-picker modal. Null
        // body clears the pin so the mapper default applies again.
        // Avoids the existing [RestPutById] route because that one
        // binds a full BookResource and would null out other fields
        // when the body is partial (model-binding gotcha verified
        // empirically). This route mutates only PreferredCoverUrl.
        // BookService.UpdateBook publishes BookEditedEvent, which
        // MediaCoverService.Handle picks up to trigger a re-download.
        [HttpPut("{id:int}/cover")]
        public ActionResult<BookResource> SetCover([FromRoute] int id, [FromBody] BookCoverPinResource resource)
        {
            var book = _bookService.GetBook(id);
            if (book == null)
            {
                return NotFound();
            }

            var preferredCoverUrl = resource?.PreferredCoverUrl;

            if (!PinnedCoverUrl.IsAllowedOrEmpty(preferredCoverUrl))
            {
                throw new ValidationException(new List<ValidationFailure>
                {
                    new ValidationFailure(nameof(BookCoverPinResource.PreferredCoverUrl), PinnedCoverUrlMessage, preferredCoverUrl)
                });
            }

            book.PreferredCoverUrl = preferredCoverUrl.IsNullOrWhiteSpace() ? null : preferredCoverUrl;
            _bookService.UpdateBook(book);
            BroadcastResourceChange(ModelAction.Updated, book.Id);
            return Accepted(book.Id);
        }

        // Cover-picker modal endpoint. Returns deduped cover candidates
        // for this book — OL work-level covers (editorial picks) first,
        // then every edition's cover_i with publisher + year captions.
        // Hits OpenLibraryProxy's 7-day cache for the work bundle, so
        // subsequent opens are sub-second.
        [HttpGet("{id:int}/covers")]
        public List<BookCoverResource> Covers(int id)
        {
            var book = _bookService.GetBook(id);
            if (book == null || book.ForeignBookId.IsNullOrWhiteSpace())
            {
                return new List<BookCoverResource>();
            }

            // A book migrated from Readarr still carries its Goodreads id, which
            // Open Library knows nothing about. Ask for the work it was mapped
            // to; a book that was never mapped has no Open Library covers.
            var workId = _metadataSourceFactory.GetOpenLibraryWorkId(book.ForeignBookId);
            if (workId == null)
            {
                return new List<BookCoverResource>();
            }

            var candidates = _openLibraryProxy.GetCoverCandidates(workId);
            return candidates.Select(c => new BookCoverResource
            {
                CoverId = c.CoverId,
                Url = c.Url,
                Source = c.Source,
                EditionTitle = c.EditionTitle,
                PublishDate = c.PublishDate,
                Publisher = c.Publisher
            }).ToList();
        }

        [RestPostById]
        public ActionResult<BookResource> AddBook([FromBody] BookResource bookResource)
        {
            var book = _addBookService.AddBook(bookResource.ToModel());

            return Created(book.Id);
        }

        [RestPutById]
        public ActionResult<BookResource> UpdateBook([FromBody] BookResource bookResource)
        {
            var book = _bookService.GetBook(bookResource.Id);
            var previouslyMonitored = book.Editions.Value.Where(x => x.Monitored).Select(x => x.Id).ToHashSet();

            var model = bookResource.ToModel(book);

            // Changing which edition is monitored is the user choosing one.
            // Mark it so a refresh keeps it instead of putting the metadata
            // source's own pick back.
            foreach (var chosen in model.Editions.Value.Where(x => x.Monitored && !previouslyMonitored.Contains(x.Id)))
            {
                chosen.ManualAdd = true;
            }

            _bookService.UpdateBook(model);
            _editionService.UpdateMany(model.Editions.Value);

            BroadcastResourceChange(ModelAction.Updated, model.Id);

            return Accepted(model.Id);
        }

        [RestDeleteById]
        public void DeleteBook(int id, bool deleteFiles = false, bool addImportListExclusion = false)
        {
            _bookService.DeleteBook(id, deleteFiles, addImportListExclusion);
        }

        [HttpPut("monitor")]
        public IActionResult SetBooksMonitored([FromBody]BooksMonitoredResource resource)
        {
            _bookService.SetMonitored(resource.BookIds, resource.Monitored);

            if (resource.BookIds.Count == 1)
            {
                _bookService.SetBookMonitored(resource.BookIds.First(), resource.Monitored);
            }
            else
            {
                _bookService.SetMonitored(resource.BookIds, resource.Monitored);
            }

            return Accepted(MapToResource(_bookService.GetBooks(resource.BookIds), false));
        }

        [NonAction]
        public void Handle(BookGrabbedEvent message)
        {
            foreach (var book in message.Book.Books)
            {
                var resource = book.ToResource();
                resource.Grabbed = true;

                BroadcastResourceChange(ModelAction.Updated, resource);
            }
        }

        [NonAction]
        public void Handle(BookEditedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, MapToResource(message.Book, true));
        }

        [NonAction]
        public void Handle(BookUpdatedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, MapToResource(message.Book, true));
        }

        [NonAction]
        public void Handle(BookDeletedEvent message)
        {
            BroadcastResourceChange(ModelAction.Deleted, message.Book.ToResource());
        }

        [NonAction]
        public void Handle(BookImportedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, MapToResource(message.Book, true));
        }

        [NonAction]
        public void Handle(TrackImportedEvent message)
        {
            BroadcastResourceChange(ModelAction.Updated, message.BookInfo.Book.ToResource());
        }

        [NonAction]
        public void Handle(BookFileDeletedEvent message)
        {
            if (message.Reason == DeleteMediaFileReason.Upgrade)
            {
                return;
            }

            BroadcastResourceChange(ModelAction.Updated, MapToResource(message.BookFile.Edition.Value.Book.Value, true));
        }
    }
}
