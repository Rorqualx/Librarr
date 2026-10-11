using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using Readarr.Http.Frontend;

namespace NzbDrone.Api.Test.Frontend
{
    [TestFixture]
    public class StaticResourceControllerFixture
    {
        // Issue #17. On ASP.NET Core 10 the cookie handler replaces the login
        // redirect with a bare 401 for any [ApiController] endpoint unless the
        // endpoint opts back in. Without the opt-in a logged-out browser gets a
        // blank 401 page instead of the login form.
        [Test]
        public void should_allow_cookie_redirect_so_logged_out_browsers_reach_login()
        {
            var attributes = typeof(StaticResourceController).GetCustomAttributes(true);

            attributes.OfType<ApiControllerAttribute>().Should().NotBeEmpty("the opt-in only matters while this is an [ApiController]");
            attributes.OfType<IAllowCookieRedirectMetadata>().Should().NotBeEmpty();
        }
    }
}
