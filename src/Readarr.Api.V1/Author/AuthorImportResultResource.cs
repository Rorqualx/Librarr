using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace Readarr.Api.V1.Author
{
    public class AuthorImportResultResource
    {
        public List<AuthorResource> Imported { get; set; }
        public List<AuthorImportSkippedResource> Skipped { get; set; }
    }

    public class AuthorImportSkippedResource
    {
        // Echoed back exactly as requested: the wizard keys its rows by folder
        // path, and this is how it ties an outcome to a row.
        public string Path { get; set; }
        public string ForeignAuthorId { get; set; }
        public string AuthorName { get; set; }

        // "duplicate", "exists" or "failed". A string rather than the enum so
        // the contract doesn't depend on how enums happen to be serialized.
        public string Reason { get; set; }
        public string Message { get; set; }
    }

    public static class AuthorImportResultResourceMapper
    {
        public static AuthorImportResultResource ToResource(this AddAuthorsResult model)
        {
            return new AuthorImportResultResource
            {
                Imported = model.Added.ToResource(),
                Skipped = model.Skipped.Select(ToResource).ToList()
            };
        }

        private static AuthorImportSkippedResource ToResource(SkippedAuthor model)
        {
            return new AuthorImportSkippedResource
            {
                Path = model.Author.Path,
                ForeignAuthorId = model.Author.Metadata.Value.ForeignAuthorId,
                AuthorName = model.Author.Metadata.Value.Name,
                Reason = model.Reason switch
                {
                    AddAuthorSkipReason.Duplicate => "duplicate",
                    AddAuthorSkipReason.Exists => "exists",
                    _ => "failed"
                },
                Message = model.Message
            };
        }
    }
}
