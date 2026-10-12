using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Update
{
    public interface IUpdatePackageProvider
    {
        UpdatePackage GetLatestUpdate(string branch, Version currentVersion);
        List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null);
    }

    // Update availability and the changelog come from the project's own GitHub
    // releases.
    //
    // Upstream asked readarr.servarr.com, a server this fork does not control.
    // It answers "no update" to every Librarr version and lists Readarr 0.4.18 as
    // the newest release, so Librarr could never be offered a Librarr update,
    // while whoever runs that host could have offered any package they liked:
    // the download URL and the hash it was verified against arrived in the same
    // response. The check also reported version, OS, architecture, runtime,
    // database type and an "active install" flag on every call.
    //
    // Nothing about the install is sent now. The request is the same for every
    // install; the asset for the running platform is picked locally.
    public class UpdatePackageProvider : IUpdatePackageProvider
    {
        public const string ReleasesUrl = "https://api.github.com/repos/Rorqualx/Librarr/releases";

        // Only assets served by the project's own release downloads are
        // considered, whatever else a response might name.
        public const string DownloadUrlPrefix = "https://github.com/Rorqualx/Librarr/releases/download/";

        public const string ChecksumsFileName = "SHA256SUMS";

        // Deliberately carries no version: GitHub requires a User-Agent, and the
        // default one would also name the OS and its version.
        private const string UserAgent = "Librarr (+https://github.com/Rorqualx/Librarr)";

        private const string ReleasesCacheKey = "releases";

        // Unauthenticated GitHub API calls are limited to 60 an hour per IP, an
        // allowance shared with everything else behind the same address. The
        // scheduled check runs every 6 hours, but System: Updates and the health
        // check ask too, so the answer is cached and one install makes at most
        // one API call an hour. A failure is remembered for a shorter time, so a
        // rate-limited or offline install neither hammers GitHub nor stays blind
        // for long.
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan FailureCacheLifetime = TimeSpan.FromMinutes(15);

        private readonly IHttpClient _httpClient;
        private readonly IUpdatePlatform _updatePlatform;
        private readonly ICached<List<GitHubReleaseResource>> _releaseCache;
        private readonly ICached<string> _checksumCache;
        private readonly Logger _logger;

        private List<GitHubReleaseResource> _lastKnownReleases;

        public UpdatePackageProvider(IHttpClient httpClient, IUpdatePlatform updatePlatform, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _updatePlatform = updatePlatform;
            _releaseCache = cacheManager.GetCache<List<GitHubReleaseResource>>(GetType(), "releases");
            _checksumCache = cacheManager.GetCache<string>(GetType(), "checksums");
            _logger = logger;
        }

        public UpdatePackage GetLatestUpdate(string branch, Version currentVersion)
        {
            // Releases that publish nothing for this platform cannot be installed
            // here, so they are not an available update either.
            var latest = GetPackages(branch).FirstOrDefault(p => p.Url.IsNotNullOrWhiteSpace());

            if (latest == null || latest.Version <= UpdateReleaseParser.Normalize(currentVersion))
            {
                return null;
            }

            // The hash is read out of the release's SHA256SUMS asset. A package
            // that cannot be verified is not offered at all: the updater must
            // never be handed something to install without a checksum.
            latest.Hash = GetChecksum(latest);

            if (latest.Hash.IsNullOrWhiteSpace())
            {
                _logger.Warn("Librarr {0} has been released, but no SHA-256 checksum for {1} could be found in its {2}. Not offering it as an update.", latest.Version, latest.FileName, ChecksumsFileName);

                return null;
            }

            return latest;
        }

        public List<UpdatePackage> GetRecentUpdates(string branch, Version currentVersion, Version previousVersion = null)
        {
            // This is the changelog, so every release is listed, including any
            // without a package for this platform.
            return GetPackages(branch);
        }

        // Newest first, one entry per version.
        private List<UpdatePackage> GetPackages(string branch)
        {
            var packages = new List<UpdatePackage>();

            foreach (var release in GetReleases())
            {
                var package = MapRelease(release, branch);

                if (package != null)
                {
                    packages.Add(package);
                }
            }

            return packages.OrderByDescending(p => p.Version)
                           .DistinctBy(p => p.Version)
                           .ToList();
        }

        private UpdatePackage MapRelease(GitHubReleaseResource release, string branch)
        {
            if (release == null || release.Draft || !UpdateReleaseParser.TryParseVersion(release.TagName, out var version))
            {
                return null;
            }

            var package = new UpdatePackage
            {
                Version = version,
                ReleaseDate = release.PublishedAt ?? DateTime.MinValue,
                Changes = UpdateReleaseParser.ParseChanges(release.Body),

                // There is a single release line, so the branch setting picks
                // nothing. It is echoed back because the installer rewrites the
                // configured branch to whatever the package says its branch is.
                Branch = branch
            };

            var fileName = UpdatePlatform.GetPackageFileName(UpdateReleaseParser.GetReleaseVersion(release.TagName), _updatePlatform.RuntimeIdentifier);
            var asset = FindAsset(release, fileName);

            if (asset != null)
            {
                package.FileName = asset.Name;
                package.Url = asset.BrowserDownloadUrl;
            }

            return package;
        }

        private static GitHubReleaseAssetResource FindAsset(GitHubReleaseResource release, string fileName)
        {
            if (fileName.IsNullOrWhiteSpace() || release.Assets == null)
            {
                return null;
            }

            return release.Assets.FirstOrDefault(a => a != null &&
                                                      fileName.Equals(a.Name, StringComparison.Ordinal) &&
                                                      a.BrowserDownloadUrl != null &&
                                                      a.BrowserDownloadUrl.StartsWith(DownloadUrlPrefix, StringComparison.Ordinal));
        }

        private string GetChecksum(UpdatePackage package)
        {
            // SHA256SUMS sits beside the package in the same release.
            var url = package.Url.Substring(0, package.Url.LastIndexOf('/') + 1) + ChecksumsFileName;
            var checksums = _checksumCache.Find(url);

            if (checksums == null)
            {
                checksums = FetchChecksums(url);
                _checksumCache.Set(url, checksums, checksums.IsNullOrWhiteSpace() ? FailureCacheLifetime : CacheLifetime);
            }

            return UpdateReleaseParser.FindChecksum(checksums, package.FileName);
        }

        private string FetchChecksums(string url)
        {
            try
            {
                var request = new HttpRequestBuilder(url).SetHeader("User-Agent", UserAgent).Build();
                request.SuppressHttpError = true;

                var response = _httpClient.Execute(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Info("Could not download the update checksums at {0}: HTTP {1}", url, (int)response.StatusCode);

                    return string.Empty;
                }

                return response.Content ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Info(ex, "Could not download the update checksums at {0}", url);

                return string.Empty;
            }
        }

        private List<GitHubReleaseResource> GetReleases()
        {
            var releases = _releaseCache.Find(ReleasesCacheKey);

            if (releases != null)
            {
                return releases;
            }

            releases = FetchReleases();

            if (releases != null)
            {
                _lastKnownReleases = releases;
                _releaseCache.Set(ReleasesCacheKey, releases, CacheLifetime);

                return releases;
            }

            // Having no update information is not an error. Keep answering with
            // what was last seen, if anything, and try again later.
            releases = _lastKnownReleases ?? new List<GitHubReleaseResource>();
            _releaseCache.Set(ReleasesCacheKey, releases, FailureCacheLifetime);

            return releases;
        }

        private List<GitHubReleaseResource> FetchReleases()
        {
            try
            {
                var request = new HttpRequestBuilder(ReleasesUrl)
                    .AddQueryParam("per_page", 20)
                    .Accept(HttpAccept.Json)
                    .SetHeader("User-Agent", UserAgent)
                    .Build();

                request.SuppressHttpError = true;

                var response = _httpClient.Execute(request);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    // 403 or 429 with X-RateLimit-Remaining: 0 is GitHub's rate
                    // limit; anything else is treated the same way.
                    var rateLimited = response.Headers.GetSingleValue("X-RateLimit-Remaining") == "0";

                    _logger.Info("Could not check {0} for updates: HTTP {1}{2}. Will try again later.", BuildInfo.AppName, (int)response.StatusCode, rateLimited ? " (GitHub rate limit reached)" : string.Empty);

                    return null;
                }

                return Json.Deserialize<List<GitHubReleaseResource>>(response.Content) ?? new List<GitHubReleaseResource>();
            }
            catch (Exception ex)
            {
                _logger.Info(ex, "Could not check {0} for updates. Will try again later.", BuildInfo.AppName);

                return null;
            }
        }
    }
}
