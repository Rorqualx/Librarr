using System;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Metadata
{
    // Phase 9c. One row of the "needs manual review" page. Each maps a
    // Goodreads book ID to its current best-guess OpenLibrary work,
    // plus the source/confidence that led us there so the user can
    // gauge how seriously to take the existing mapping.
    //
    // Author mappings share the table (see LowConfidenceMappingKind), so
    // a row is one of two things and Kind says which. Only the fields for
    // that kind are populated: a book carries a work id and optionally an
    // edition id, an author carries an author id and nothing else. The
    // storage quirk -- the author OLID living in the edition column --
    // stops at the mapper below and is not part of the API.
    public class LowConfidenceMappingResource : RestResource
    {
        public string Kind { get; set; }
        public string GoodreadsId { get; set; }
        public string OpenLibraryWorkId { get; set; }
        public string OpenLibraryEditionId { get; set; }
        public string OpenLibraryAuthorId { get; set; }
        public double Confidence { get; set; }
        public string Source { get; set; }
        public DateTime CreatedUtc { get; set; }

        // Display-only join data — pulled at GET time so the UI doesn't
        // need a second roundtrip per row to render. PUT bodies can
        // leave these null.
        public int? BookId { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
    }

    public static class LowConfidenceMappingKind
    {
        public const string Book = "book";
        public const string Author = "author";

        // ReidentifyService.MapAuthor writes author rows with a null work
        // id and the author OLID in OpenLibraryEditionId, and
        // BookIdMappingRepository.FindByOpenLibraryAuthorId reads them back
        // on exactly that shape. Requiring the OL...A id as well as the
        // null keeps a book row that merely lost its work id from being
        // presented as an author.
        public static bool IsAuthorMapping(this BookIdMapping model)
        {
            return model.OpenLibraryWorkId.IsNullOrWhiteSpace() &&
                   OpenLibraryIdHelper.IsAuthorId(model.OpenLibraryEditionId);
        }
    }

    public static class LowConfidenceMappingResourceMapper
    {
        public static LowConfidenceMappingResource ToResource(this BookIdMapping model)
        {
            if (model == null)
            {
                return null;
            }

            var isAuthor = model.IsAuthorMapping();

            return new LowConfidenceMappingResource
            {
                Id = model.Id,
                Kind = isAuthor ? LowConfidenceMappingKind.Author : LowConfidenceMappingKind.Book,
                GoodreadsId = model.GoodreadsId,
                OpenLibraryWorkId = isAuthor ? null : model.OpenLibraryWorkId,
                OpenLibraryEditionId = isAuthor ? null : model.OpenLibraryEditionId,
                OpenLibraryAuthorId = isAuthor ? model.OpenLibraryEditionId : null,
                Confidence = model.Confidence,
                Source = model.Source,
                CreatedUtc = model.CreatedUtc
            };
        }
    }
}
