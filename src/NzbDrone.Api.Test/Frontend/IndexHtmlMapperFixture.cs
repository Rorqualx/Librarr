using System;
using System.IO;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Readarr.Http.Frontend.Mappers;

namespace NzbDrone.Api.Test.Frontend
{
    // index.html is rewritten at request time so every asset carries the URL
    // base and a cache breaker. The rewrite must not depend on how the build
    // happened to quote attributes: webpack 5.111's HTML minifier dropped the
    // quotes, nothing was rewritten, and a reverse proxy under a URL base got a
    // blank page (issue #67).
    [TestFixture]
    public class IndexHtmlMapperFixture : TestBase
    {
        private string _uiFolder;

        [SetUp]
        public void Setup()
        {
            _uiFolder = Path.Combine(TempFolder, "app", "UI");
            Directory.CreateDirectory(_uiFolder);

            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.StartUpFolder).Returns(Path.Combine(TempFolder, "app"));
            Mocker.GetMock<IConfigFileProvider>().SetupGet(c => c.UiFolder).Returns("UI");
            Mocker.GetMock<IConfigFileProvider>().SetupGet(c => c.UrlBase).Returns("/readarr");

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>(), It.IsAny<StringComparison>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(c => c.ReadAllText(It.IsAny<string>())).Returns<string>(File.ReadAllText);

            Mocker.GetMock<ICacheBreakerProvider>()
                  .Setup(c => c.AddCacheBreakerToPath(It.IsAny<string>()))
                  .Returns<string>(p => p + "?h=hash");
        }

        private string GetIndex(string html)
        {
            File.WriteAllText(Path.Combine(_uiFolder, "index.html"), html);

            var mapper = new IndexHtmlMapper(Mocker.Resolve<IAppFolderInfo>(),
                                             Mocker.Resolve<IDiskProvider>(),
                                             Mocker.Resolve<IConfigFileProvider>(),
                                             new Lazy<ICacheBreakerProvider>(() => Mocker.Resolve<ICacheBreakerProvider>()),
                                             LogManager.GetCurrentClassLogger());

            var result = (FileStreamResult)mapper.GetResponse("/");

            using var reader = new StreamReader(result.FileStream);

            return reader.ReadToEnd();
        }

        [TestCase("<link rel=\"stylesheet\" href=\"/Content/Fonts/fonts.css\">")]
        [TestCase("<link rel='stylesheet' href='/Content/Fonts/fonts.css'>")]
        [TestCase("<link rel=stylesheet href=/Content/Fonts/fonts.css>")]
        [TestCase("<link rel=stylesheet href=/Content/Fonts/fonts.css data-foo>")]
        public void should_add_url_base_and_cache_breaker_however_the_attribute_is_quoted(string html)
        {
            GetIndex(html).Should().Contain("href=\"/readarr/Content/Fonts/fonts.css?h=hash\"");
        }

        // The marker is only for the server, so it is removed with the rewrite.
        [TestCase("<script src=\"/index-b217fae9ba6ff7f4b66f.js\" data-no-hash></script>")]
        [TestCase("<script src=/index-b217fae9ba6ff7f4b66f.js data-no-hash></script>")]
        public void should_add_url_base_without_cache_breaker_when_marked_no_hash(string html)
        {
            GetIndex(html).Should().Contain("src=\"/readarr/index-b217fae9ba6ff7f4b66f.js\"></script>");
        }

        [Test]
        public void should_not_cut_an_unquoted_value_short_at_a_shorter_extension()
        {
            GetIndex("<link rel=manifest href=/Content/Images/Icons/manifest.json crossorigin=use-credentials>")
                .Should().Contain("href=\"/readarr/Content/Images/Icons/manifest.json?h=hash\" crossorigin");
        }

        // The head of the index.html that 1.2.4-beta shipped.
        [Test]
        public void should_rewrite_every_asset_in_the_minified_index()
        {
            var html = "<link rel=apple-touch-icon sizes=180x180 href=/Content/Images/Icons/apple-touch-icon.png>" +
                       "<link rel=\"shortcut icon\" type=image/ico href=/favicon.ico data-no-hash>" +
                       "<link rel=stylesheet href=/Content/Fonts/fonts.css>" +
                       "<script>window.Readarr={urlBase:\"__URL_BASE__\"};</script>" +
                       "<script src=/index-b217fae9ba6ff7f4b66f.js data-no-hash></script>" +
                       "<link href=/Content/styles.css rel=stylesheet>";

            var text = GetIndex(html);

            text.Should().Contain("href=\"/readarr/Content/Images/Icons/apple-touch-icon.png?h=hash\"");
            text.Should().Contain("href=\"/readarr/favicon.ico\">");
            text.Should().Contain("href=\"/readarr/Content/Fonts/fonts.css?h=hash\"");
            text.Should().Contain("urlBase:\"/readarr\"");
            text.Should().Contain("src=\"/readarr/index-b217fae9ba6ff7f4b66f.js\">");
            text.Should().Contain("href=\"/readarr/Content/styles.css?h=hash\"");
            text.Should().NotContain("=/Content");
        }
    }
}
