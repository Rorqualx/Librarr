using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.Books
{
    public interface IAddBookService
    {
        Book AddBook(Book book, bool doRefresh = true);
        List<Book> AddBooks(List<Book> books, bool doRefresh = true);
    }

    public class AddBookService : IAddBookService
    {
        private readonly IAuthorService _authorService;
        private readonly IAddAuthorService _addAuthorService;
        private readonly IBookService _bookService;
        private readonly IProvideBookInfo _bookInfo;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly Logger _logger;

        public AddBookService(IAuthorService authorService,
                               IAddAuthorService addAuthorService,
                               IBookService bookService,
                               IProvideBookInfo bookInfo,
                               IImportListExclusionService importListExclusionService,
                               Logger logger)
        {
            _authorService = authorService;
            _addAuthorService = addAuthorService;
            _bookService = bookService;
            _bookInfo = bookInfo;
            _importListExclusionService = importListExclusionService;
            _logger = logger;
        }

        public Book AddBook(Book book, bool doRefresh = true)
        {
            _logger.Debug($"Adding book {book}");

            book = AddSkyhookData(book);

            // we allow adding extra editions, so check if the book already exists
            var dbBook = _bookService.FindById(book.ForeignBookId);
            if (dbBook != null)
            {
                book.UseDbFieldsFrom(dbBook);
            }

            // Remove any import list exclusions preventing addition
            _importListExclusionService.Delete(book.ForeignBookId);
            _importListExclusionService.Delete(book.AuthorMetadata.Value.ForeignAuthorId);

            // Note it's a manual addition so it's not deleted on next refresh
            book.AddOptions.AddType = BookAddType.Manual;
            book.Editions.Value.Single(x => x.Monitored).ManualAdd = true;

            // Add the author if necessary
            var dbAuthor = _authorService.FindById(book.AuthorMetadata.Value.ForeignAuthorId);
            if (dbAuthor == null)
            {
                var author = book.Author.Value;

                author.Metadata.Value.ForeignAuthorId = book.AuthorMetadata.Value.ForeignAuthorId;

                // Cascade-add through Add Book: the user only asked to
                // add THIS book, not the author's full discography. The
                // post-add RefreshAuthorCommand still pulls in the
                // author's works list (so the author detail page shows
                // the full bibliography), but with MonitorNewItems=None
                // those other books arrive unmonitored ('Missing'
                // status). Only the explicitly-added book stays
                // Monitored=true. Users opt into more books per-row
                // from the author page.
                author.MonitorNewItems = NewItemMonitorTypes.None;

                dbAuthor = _addAuthorService.AddAuthor(author, false);
            }

            book.Author = dbAuthor;
            book.AuthorMetadataId = dbAuthor.AuthorMetadataId;
            _bookService.AddBook(book, doRefresh);

            return book;
        }

        public List<Book> AddBooks(List<Book> books, bool doRefresh = true)
        {
            var added = DateTime.UtcNow;
            var addedBooks = new List<Book>();

            foreach (var a in books)
            {
                a.Added = added;
                try
                {
                    addedBooks.Add(AddBook(a, doRefresh));
                }
                catch (Exception ex)
                {
                    // Could be a bad id from an import list
                    _logger.Error(ex, "Failed to import id: {0} - {1}", a.ForeignBookId, a.Title);
                }
            }

            return addedBooks;
        }

        private Book AddSkyhookData(Book newBook)
        {
            // A book can arrive naming no edition at all: the OpenLibrary
            // import lists only know the work. That is the same situation
            // as an edition id that matches nothing, handled below.
            var editionId = newBook.Editions?.Value?.SingleOrDefault(x => x.Monitored)?.ForeignEditionId;

            Tuple<string, Book, List<AuthorMetadata>> tuple = null;
            try
            {
                tuple = _bookInfo.GetBookInfo(newBook.ForeignBookId);
            }
            catch (BookNotFoundException)
            {
                _logger.Error("Book with Foreign Id {0} was not found, it may have been removed from Goodreads.", newBook.ForeignBookId);

                throw new ValidationException(new List<ValidationFailure>
                                              {
                                                  new ValidationFailure("GoodreadsId", "A book with this ID was not found", newBook.ForeignBookId)
                                              });
            }

            newBook.UseMetadataFrom(tuple.Item2);
            newBook.Added = DateTime.UtcNow;

            newBook.Editions = tuple.Item2.Editions.Value;
            var sourcePick = newBook.Editions.Value.FirstOrDefault(x => x.Monitored);
            newBook.Editions.Value.ForEach(x => x.Monitored = false);

            // Pick the previously-monitored edition by foreign id when
            // the slim search result actually carried a real edition
            // key. The OL search path uses the work key as a placeholder
            // ForeignEditionId (no edition keys are exposed in the
            // search response), so the slim id never matches any of the
            // real M-keyed editions returned by GetBookInfo. Fall back
            // to the edition the mapper chose in that case — the user is
            // adding the work, and AddBook marks this edition as theirs,
            // so a refresh keeps it. Taking whichever edition the source
            // happened to list first would lock in a translation or a
            // coverless printing.
            var monitoredEdition = (editionId == null ? null : newBook.Editions.Value.FirstOrDefault(x => x.ForeignEditionId == editionId))
                ?? sourcePick
                ?? newBook.Editions.Value.FirstOrDefault();
            if (monitoredEdition != null)
            {
                monitoredEdition.Monitored = true;
            }

            var metadata = tuple.Item3.FirstOrDefault(x => x.ForeignAuthorId == tuple.Item1);
            newBook.AuthorMetadata = metadata;

            return newBook;
        }
    }
}
