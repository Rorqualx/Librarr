using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Playwright;
using NLog;
using NUnit.Framework;
using NzbDrone.Playwright.Test.PageModel;
using NzbDrone.Test.Common;

namespace NzbDrone.Playwright.Test
{
    // The UI must load under a URL base, the way it is served behind a
    // reverse proxy at e.g. example.net/readarr/. Issue #67: 1.2.4-beta's
    // webpack started minifying index.html and dropped the attribute quotes,
    // the server's URL-base rewrite only knew quoted attributes, and the page
    // asked for every script and stylesheet at the site root. The result was
    // a blank page and no error -- invisible to every other test here, which
    // all run without a URL base, where a missing prefix still resolves.
    //
    // This runs its own instance rather than reconfiguring the shared one:
    // the URL base is bootstrap config, read once at startup. It stops that
    // instance with Kill(), never KillAll(), which would take AssemblyGate's
    // down with it.
    [TestFixture]
    public class UrlBaseTest
    {
        private const int Port = 8788;
        private const string UrlBase = "/readarr";

        private NzbDroneRunner _runner;
        private IBrowserContext _context;
        private IPage _page;

        [OneTimeSetUp]
        public async Task StartUnderUrlBase()
        {
            _runner = new NzbDroneRunner(LogManager.GetCurrentClassLogger(), null, Port, UrlBase);
            _runner.Start(true);

            _context = await AssemblyGate.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
            });

            _page = await _context.NewPageAsync();
        }

        [OneTimeTearDown]
        public async Task Stop()
        {
            if (_context != null)
            {
                await _context.CloseAsync();
            }

            _runner?.Kill();
        }

        [Test]
        public async Task ui_loads_with_every_request_under_the_url_base()
        {
            var outsideUrlBase = new List<string>();
            var failed = new List<string>();

            _page.Request += (_, request) =>
            {
                var uri = new Uri(request.Url);

                if (uri.Port == Port && !uri.AbsolutePath.StartsWith(UrlBase + "/", StringComparison.Ordinal))
                {
                    outsideUrlBase.Add(request.Url);
                }
            };

            _page.Response += (_, response) =>
            {
                if (response.Status >= 400)
                {
                    failed.Add($"{response.Status} {response.Url}");
                }
            };

            _page.RequestFailed += (_, request) => failed.Add($"{request.Failure} {request.Url}");

            await _page.GotoAsync($"http://localhost:{Port}{UrlBase}/");

            // A blank page never shows the spinner, and WaitForNoSpinner takes
            // that as "ready", so wait for the app shell itself.
            var pageModel = new PageBase(_page);
            await pageModel.LibraryNavIcon.WaitForAsync(new LocatorWaitForOptions
            {
                Timeout = (float)TimeSpan.FromSeconds(60).TotalMilliseconds
            });
            await pageModel.WaitForNoSpinner();

            (await _page.EvaluateAsync<string>("() => window.Readarr.urlBase")).Should().Be(UrlBase);

            outsideUrlBase.Should().BeEmpty("every request must carry the URL base, or a reverse proxy cannot route it");
            failed.Should().BeEmpty();
            (await _page.Locator("#errors div").AllInnerTextsAsync()).Should().BeEmpty();
        }
    }
}
