using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using Readarr.Http.Extensions;
using Readarr.Http.Frontend.Mappers;

namespace Readarr.Http.Frontend
{
    // [ApiController] makes the ASP.NET Core 10 cookie handler answer an
    // unauthenticated request with 401 rather than a redirect to /login.
    // These are pages a browser navigates to, so opt back in to the redirect.
    [Authorize(Policy="UI")]
    [ApiController]
    [AllowCookieRedirect]
    public class StaticResourceController : Controller
    {
        private readonly IEnumerable<IMapHttpRequestsToDisk> _requestMappers;
        private readonly Logger _logger;

        public StaticResourceController(IEnumerable<IMapHttpRequestsToDisk> requestMappers,
            Logger logger)
        {
            _requestMappers = requestMappers;
            _logger = logger;
        }

        [AllowAnonymous]
        [HttpGet("login")]
        public IActionResult LoginPage()
        {
            return MapResource("login");
        }

        [EnableCors("AllowGet")]
        [AllowAnonymous]
        [HttpGet("content/{**path:regex(^(?!/*api/).*)}")]
        public IActionResult IndexContent([FromRoute] string path)
        {
            return MapResource("Content/" + path);
        }

        [HttpGet("")]
        [HttpGet("/{**path:regex(^(?!(api|feed)/).*)}")]
        public IActionResult Index([FromRoute] string path)
        {
            return MapResource(path);
        }

        private IActionResult MapResource(string path)
        {
            path = "/" + (path ?? "");

            var mapper = _requestMappers.SingleOrDefault(m => m.CanHandle(path));

            if (mapper != null)
            {
                var result = mapper.GetResponse(path);

                if (result != null)
                {
                    if ((result as FileResult)?.ContentType == "text/html")
                    {
                        Response.Headers.DisableCache();
                    }

                    return result;
                }

                return NotFound();
            }

            _logger.Warn("Couldn't find handler for {0}", path);

            return NotFound();
        }
    }
}
