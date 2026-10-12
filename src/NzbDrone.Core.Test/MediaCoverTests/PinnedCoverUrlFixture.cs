using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;

namespace NzbDrone.Core.Test.MediaCoverTests
{
    [TestFixture]
    public class PinnedCoverUrlFixture
    {
        // The shapes OpenLibraryProxy.GetCoverCandidates and OpenLibraryCoverUrls produce.
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-M.jpg")]
        [TestCase("https://covers.openlibrary.org/b/olid/OL7353617M-L.jpg")]
        [TestCase("https://covers.openlibrary.org/b/isbn/9780441478125-L.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.png")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.gif")]
        public void should_allow_open_library_cover_image(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeTrue();
            PinnedCoverUrl.IsAllowedOrEmpty(url).Should().BeTrue();
        }

        [TestCase("http://covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("ftp://covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("file:///etc/passwd")]
        [TestCase("//covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("/b/id/8231856-L.jpg")]
        public void should_refuse_anything_but_absolute_https(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeFalse();
        }

        [TestCase("https://localhost/cover.jpg")]
        [TestCase("https://127.0.0.1/cover.jpg")]
        [TestCase("https://127.0.0.1:8787/cover.jpg")]
        [TestCase("https://[::1]/cover.jpg")]
        [TestCase("https://192.168.1.1/cover.jpg")]
        [TestCase("https://169.254.169.254/latest/cover.jpg")]
        [TestCase("https://openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://example.com/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org.example.com/b/id/8231856-L.jpg")]
        [TestCase("https://example.com/covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org@example.com/b/id/8231856-L.jpg")]
        [TestCase("https://user:pass@covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org:8443/b/id/8231856-L.jpg")]
        [TestCase(@"https://covers.openlibrary.org\@example.com/b/id/8231856-L.jpg")]
        [TestCase("https://COVERS.OPENLIBRARY.ORG.evil.example/b/id/1-L.jpg")]
        public void should_refuse_any_other_host(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeFalse();
        }

        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.svg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.html")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.js")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg.exe")]
        [TestCase("https://covers.openlibrary.org/")]
        [TestCase("https://covers.openlibrary.org")]
        public void should_refuse_anything_but_an_image_extension(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeFalse();
        }

        // The saved file's extension is cut from the URL, so nothing may follow it.
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg?default=false")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.svg?x=.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.svg#.jpg")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg/")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg\n")]
        [TestCase("https://covers.openlibrary.org/b/id/8231856-L.jpg ")]
        [TestCase(" https://covers.openlibrary.org/b/id/8231856-L.jpg")]
        [TestCase("https://covers.openlibrary.org/b/../../x.jpg")]
        [TestCase("https://covers.openlibrary.org/b/%2e%2e/x.jpg")]
        [TestCase(@"https://covers.openlibrary.org/b\id\1.jpg")]
        [TestCase("https://covers.openlibrary.org//example.com/x.jpg")]
        public void should_refuse_url_with_anything_beyond_a_plain_path(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeFalse();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void no_pin_should_be_acceptable_but_is_not_a_pin(string url)
        {
            PinnedCoverUrl.IsAllowed(url).Should().BeFalse();
            PinnedCoverUrl.IsAllowedOrEmpty(url).Should().BeTrue();
        }
    }
}
