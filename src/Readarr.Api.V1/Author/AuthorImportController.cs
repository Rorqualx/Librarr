using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using Readarr.Http;
using Readarr.Http.REST;

namespace Readarr.Api.V1.Author
{
    // Bulk endpoint behind the Library Import wizard. The wizard resolves a
    // whole root folder's worth of unmapped folders to OpenLibrary authors in
    // one pass, and adding them one POST /author at a time would mean a refresh
    // per author and a partial library if the browser navigated away mid-run.
    //
    // ImportAuthors never lets one row abort the rest (see AddAuthorService):
    // a row whose author is already in the library, that picks the same author
    // as an earlier row, or that can't be resolved is skipped. The response
    // lists what was imported and, for each skipped row, why it was not, which
    // is what the UI reports back.
    [V1ApiController("author/import")]
    public class AuthorImportController : Controller
    {
        private readonly IAddAuthorService _addAuthorService;

        public AuthorImportController(IAddAuthorService addAuthorService)
        {
            _addAuthorService = addAuthorService;
        }

        [HttpPost]
        public AuthorImportResultResource Import([FromBody] List<AuthorResource> resource)
        {
            // An empty body binds to null rather than failing model validation.
            if (resource == null)
            {
                throw new BadRequestException("A list of authors to import is required");
            }

            var newAuthors = resource.Where(x => x != null).ToModel();

            return _addAuthorService.ImportAuthors(newAuthors).ToResource();
        }
    }
}
