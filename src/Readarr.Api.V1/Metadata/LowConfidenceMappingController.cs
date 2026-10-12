using System.Collections.Generic;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using NzbDrone.Http.REST.Attributes;
using Readarr.Http;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Metadata
{
    // Phase 9c. Surfaces BookIdMapping rows whose Confidence falls
    // below the threshold MetadataSwitchWizard considers "good enough".
    // PUT lets the user overwrite a row with their own OL work ID and
    // marks it as Manual — which the file-tag pass and any future
    // reidentify runs will then leave alone.
    //
    // The table holds author mappings as well as book mappings (see
    // LowConfidenceMappingKind). Which one a row is gets decided here,
    // from what is stored, never from the request: the two are read back
    // by different lookups, and a row saved as the wrong kind is simply
    // never found again.
    [V1ApiController("metadata/lowconfidencemapping")]
    public class LowConfidenceMappingController : RestController<LowConfidenceMappingResource>
    {
        // Same threshold as ReidentifyService.MediumConfidence. Kept in
        // sync by convention — diverging would silently mean the wizard
        // and this page disagree about which rows need review.
        private const double DefaultThreshold = 0.70;

        private readonly IBookIdMappingRepository _mappingRepo;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;

        public LowConfidenceMappingController(IBookIdMappingRepository mappingRepo,
                                              IBookService bookService,
                                              IAuthorService authorService)
        {
            _mappingRepo = mappingRepo;
            _bookService = bookService;
            _authorService = authorService;
        }

        protected override LowConfidenceMappingResource GetResourceById(int id)
        {
            return Decorate(new List<BookIdMapping> { _mappingRepo.Get(id) }).SingleOrDefault();
        }

        [HttpGet]
        public List<LowConfidenceMappingResource> GetLowConfidenceMappings([FromQuery] double? threshold = null)
        {
            var cutoff = threshold ?? DefaultThreshold;
            var rows = _mappingRepo.GetLowConfidence(cutoff);
            return Decorate(rows);
        }

        [RestPutById]
        public ActionResult<LowConfidenceMappingResource> OverrideMapping([FromBody] LowConfidenceMappingResource resource)
        {
            // Manual override: trust the user's chosen OL ID, lock
            // confidence at 1.0, and stamp Source=Manual so subsequent
            // reidentify passes (including the file-tag one) will leave
            // this row untouched.
            //
            // "Trust" covers which work or author they picked, not the
            // shape of what they typed. A Manual row is hidden from this
            // page and skipped by reidentify, so an id no consumer can
            // read would be locked in with nothing left to flag it. Every
            // check therefore runs before the row is touched.
            var existing = _mappingRepo.Get(resource.Id);
            if (existing == null)
            {
                return NotFound();
            }

            if (existing.IsAuthorMapping())
            {
                var authorId = ValidateAuthorOverride(resource);

                existing.OpenLibraryWorkId = null;
                existing.OpenLibraryEditionId = authorId;
            }
            else
            {
                var workId = ValidateBookOverride(resource, existing, out var editionId);

                existing.OpenLibraryWorkId = workId;
                existing.OpenLibraryEditionId = editionId;
            }

            existing.Confidence = 1.0;
            existing.Source = BookIdMappingSource.Manual;

            _mappingRepo.Update(existing);
            return Accepted(Decorate(new List<BookIdMapping> { existing }).Single());
        }

        private static string ValidateAuthorOverride(LowConfidenceMappingResource resource)
        {
            // An author row that gains a work id stops matching
            // FindByOpenLibraryAuthorId, so a client that mistook it for a
            // book is refused outright rather than half-obeyed.
            if (resource.OpenLibraryWorkId.IsNotNullOrWhiteSpace())
            {
                throw Invalid(
                    nameof(resource.OpenLibraryWorkId),
                    "This is an author mapping and cannot be given a work ID. Enter the author's Open Library ID (OLxxxxA) instead.",
                    resource.OpenLibraryWorkId);
            }

            if (!OpenLibraryIdHelper.TryNormalizeAuthorId(resource.OpenLibraryAuthorId, out var authorId))
            {
                throw Invalid(
                    nameof(resource.OpenLibraryAuthorId),
                    "Enter an Open Library author ID such as OL34184A, or paste the author's openlibrary.org link.",
                    resource.OpenLibraryAuthorId);
            }

            return authorId;
        }

        private static string ValidateBookOverride(LowConfidenceMappingResource resource, BookIdMapping existing, out string editionId)
        {
            if (resource.OpenLibraryAuthorId.IsNotNullOrWhiteSpace())
            {
                throw Invalid(
                    nameof(resource.OpenLibraryAuthorId),
                    "This is a book mapping and cannot be given an author ID. Enter the book's Open Library work ID (OLxxxxW) instead.",
                    resource.OpenLibraryAuthorId);
            }

            if (!OpenLibraryIdHelper.TryNormalizeWorkId(resource.OpenLibraryWorkId, out var workId))
            {
                throw Invalid(
                    nameof(resource.OpenLibraryWorkId),
                    "Enter an Open Library work ID such as OL45804W, or paste the work's openlibrary.org link.",
                    resource.OpenLibraryWorkId);
            }

            var edition = resource.OpenLibraryEditionId?.Trim();

            if (edition.IsNullOrWhiteSpace())
            {
                // Optional, and blank has always meant "clear it".
                editionId = null;
            }
            else if (edition == existing.OpenLibraryEditionId)
            {
                // Sent back exactly as stored, i.e. not something the user
                // typed. Reidentify does not promise an OL...M id in this
                // column, and an inherited value must not block the work
                // being corrected.
                editionId = existing.OpenLibraryEditionId;
            }
            else if (!OpenLibraryIdHelper.TryNormalizeEditionId(edition, out editionId))
            {
                throw Invalid(
                    nameof(resource.OpenLibraryEditionId),
                    "Enter an Open Library edition ID such as OL7353617M, or leave the edition blank.",
                    resource.OpenLibraryEditionId);
            }

            return workId;
        }

        // Property names are camel-cased to match the JSON the client sent,
        // so it can pin the message to the field that caused it.
        private static ValidationException Invalid(string propertyName, string message, string attemptedValue)
        {
            var name = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);

            return new ValidationException(new List<ValidationFailure> { new (name, message, attemptedValue) });
        }

        private List<LowConfidenceMappingResource> Decorate(List<BookIdMapping> mappings)
        {
            // Best-effort join: if we can find the Book (or, for an author
            // row, the Author) for this Goodreads ID, surface title +
            // author so the UI doesn't show bare opaque IDs to the user.
            // If the local row has been deleted, the mapping still
            // renders — just blanker.
            //
            // Three lookups however many rows there are. This used to be
            // two per row, and the list is unpaged: a freshly migrated
            // library can put thousands of rows on this page at once.
            //
            // Book and author ids are looked up separately because
            // Goodreads numbers them independently — an author id can be
            // some unrelated book's id.
            mappings = mappings.Where(m => m != null).ToList();

            var withGoodreadsId = mappings.Where(m => m.GoodreadsId.IsNotNullOrWhiteSpace()).ToList();
            var bookIds = withGoodreadsId.Where(m => !m.IsAuthorMapping()).Select(m => m.GoodreadsId).Distinct().ToList();
            var authorIds = withGoodreadsId.Where(m => m.IsAuthorMapping()).Select(m => m.GoodreadsId).Distinct().ToList();

            var books = new Dictionary<string, Book>();
            if (bookIds.Any())
            {
                foreach (var book in _bookService.FindByIds(bookIds))
                {
                    books.TryAdd(book.ForeignBookId, book);
                }
            }

            var authorsByMetadataId = new Dictionary<int, NzbDrone.Core.Books.Author>();
            var metadataIds = books.Values.Select(b => b.AuthorMetadataId).Where(id => id > 0).Distinct().ToList();
            if (metadataIds.Any())
            {
                foreach (var author in _authorService.GetAuthorsByMetadataId(metadataIds))
                {
                    authorsByMetadataId.TryAdd(author.AuthorMetadataId, author);
                }
            }

            var authorsByForeignId = new Dictionary<string, NzbDrone.Core.Books.Author>();
            if (authorIds.Any())
            {
                foreach (var author in _authorService.FindByIds(authorIds))
                {
                    authorsByForeignId.TryAdd(author.ForeignAuthorId, author);
                }
            }

            var resources = new List<LowConfidenceMappingResource>(mappings.Count);

            foreach (var mapping in mappings)
            {
                var resource = mapping.ToResource();
                resources.Add(resource);

                if (mapping.GoodreadsId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (mapping.IsAuthorMapping())
                {
                    if (authorsByForeignId.TryGetValue(mapping.GoodreadsId, out var mappedAuthor))
                    {
                        resource.AuthorName = mappedAuthor.Name;
                    }
                }
                else if (books.TryGetValue(mapping.GoodreadsId, out var book))
                {
                    resource.BookId = book.Id;
                    resource.BookTitle = book.Title;

                    if (authorsByMetadataId.TryGetValue(book.AuthorMetadataId, out var author))
                    {
                        resource.AuthorName = author.Name;
                    }
                }
            }

            return resources;
        }
    }
}
