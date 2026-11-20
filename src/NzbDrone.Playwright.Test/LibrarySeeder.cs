using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RestSharp;

namespace NzbDrone.Playwright.Test
{
    // Puts one author into the running instance's library so a fixture can
    // assert on a page that has content, rather than on the empty state.
    //
    // Seeding goes through the app's own API. The alternative the release
    // checklist floated -- shipping a pre-built SQLite file under
    // tests/regression/ -- pins the schema at whatever migration count it was
    // captured on, so it silently rots the next time somebody adds a
    // migration, and there are 48 of those already. Seeding through the API
    // costs a network round trip and stays correct by construction.
    //
    // The network round trip is the catch: adding an author performs a real
    // OpenLibrary lookup, and OpenLibrary refuses an IP that asks too often
    // (the integration suite learned this the hard way -- see _AssemblyGate
    // there). So this seeds ONE small author per run, and when OpenLibrary
    // cannot be reached the fixture is marked Inconclusive rather than failed.
    // A smoke suite that goes red because a third party is rate-limiting you
    // teaches you to ignore it.
    public static class LibrarySeeder
    {
        // 4 works. Chosen for being small: every work is refreshed from
        // OpenLibrary on add, at roughly a second each.
        public const string AuthorId = "OL1422008A";
        public const string AuthorName = "Philip W. Errington";

        private static bool _seeded;
        private static string _rootFolder;

        public static void EnsureSeeded(int port, string apiKey)
        {
            if (_seeded)
            {
                return;
            }

            var client = new RestClient($"http://localhost:{port}/api/v1");

            if (Get(client, apiKey, "author").Any(a => a.Value<string>("foreignAuthorId") == AuthorId))
            {
                _seeded = true;
                return;
            }

            EnsureRootFolder(client, apiKey);

            var lookup = Get(client, apiKey, $"author/lookup?term={AuthorId}");

            if (lookup.Count == 0)
            {
                Assert.Inconclusive(
                    $"Could not look up {AuthorId} — OpenLibrary is unreachable or rate-limiting this IP. " +
                    "The seeded fixtures need one real metadata fetch; the unseeded smokes are unaffected.");
            }

            var author = (JObject)lookup[0];
            author["qualityProfileId"] = FirstId(client, apiKey, "qualityprofile");
            author["metadataProfileId"] = FirstId(client, apiKey, "metadataprofile");
            author["rootFolderPath"] = _rootFolder;
            author["path"] = Path.Combine(_rootFolder, author.Value<string>("authorName"));
            author["monitored"] = true;
            author["addOptions"] = new JObject();

            Directory.CreateDirectory(author.Value<string>("path"));

            var post = new RestRequest("author", Method.Post);
            post.AddHeader("X-Api-Key", apiKey);
            post.AddParameter("application/json", author.ToString(), ParameterType.RequestBody);

            var response = client.Execute(post);

            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
            {
                Assert.Inconclusive($"Seeding {AuthorName} failed: {response.StatusCode} {response.Content}");
            }

            WaitForBooks(client, apiKey);

            _seeded = true;
        }

        // The route is /book/:titleSlug, so a test that wants the book detail
        // page has to ask the API which slug the seeded author's books got --
        // the slug is derived from the title by the backend, not something the
        // test can predict.
        public static string FirstBookTitleSlug(int port, string apiKey)
        {
            var client = new RestClient($"http://localhost:{port}/api/v1");
            var books = Get(client, apiKey, "book");

            if (books.Count == 0)
            {
                Assert.Inconclusive($"{AuthorName} has no books, so there is no book detail page to open.");
            }

            return books[0].Value<string>("titleSlug");
        }

        // The author row lands immediately; its books arrive on a background
        // refresh command. Asserting on a page before that finishes is the
        // difference between "the book list renders" and a flaky empty table.
        private static void WaitForBooks(RestClient client, string apiKey)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);

            while (DateTime.UtcNow < deadline)
            {
                var books = Get(client, apiKey, "book");

                if (books.Count > 0)
                {
                    TestContext.Progress.WriteLine($"Seeded {AuthorName} with {books.Count} books");
                    return;
                }

                Thread.Sleep(1000);
            }

            Assert.Inconclusive(
                $"{AuthorName} was added but no books appeared within 90s — " +
                "OpenLibrary is likely throttling the per-work refresh.");
        }

        private static void EnsureRootFolder(RestClient client, string apiKey)
        {
            _rootFolder = Path.Combine(Path.GetTempPath(), "librarr-playwright-library");
            Directory.CreateDirectory(_rootFolder);

            var existing = Get(client, apiKey, "rootfolder");

            if (existing.Any(f => f.Value<string>("path") == _rootFolder))
            {
                return;
            }

            var body = new JObject
            {
                ["path"] = _rootFolder,
                ["name"] = "Playwright",
                ["defaultQualityProfileId"] = FirstId(client, apiKey, "qualityprofile"),
                ["defaultMetadataProfileId"] = FirstId(client, apiKey, "metadataprofile"),
                ["isCalibreLibrary"] = false
            };

            var request = new RestRequest("rootfolder", Method.Post);
            request.AddHeader("X-Api-Key", apiKey);
            request.AddParameter("application/json", body.ToString(), ParameterType.RequestBody);

            var response = client.Execute(request);

            if (response.StatusCode != HttpStatusCode.Created && response.StatusCode != HttpStatusCode.OK)
            {
                Assert.Inconclusive($"Could not create a root folder: {response.StatusCode} {response.Content}");
            }
        }

        private static int FirstId(RestClient client, string apiKey, string resource)
        {
            var all = Get(client, apiKey, resource);

            if (all.Count == 0)
            {
                Assert.Inconclusive($"No {resource} exists on a fresh instance, which should be impossible.");
            }

            return all[0].Value<int>("id");
        }

        private static JArray Get(RestClient client, string apiKey, string resource)
        {
            var request = new RestRequest(resource);
            request.AddHeader("X-Api-Key", apiKey);

            var response = client.Execute(request);

            if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrWhiteSpace(response.Content))
            {
                return new JArray();
            }

            return JArray.Parse(response.Content);
        }
    }
}
