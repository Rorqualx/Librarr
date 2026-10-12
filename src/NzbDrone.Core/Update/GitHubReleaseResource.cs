using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.Update
{
    // The subset of GitHub's "list releases" response the update check reads.
    // https://docs.github.com/en/rest/releases/releases#list-releases
    public class GitHubReleaseResource
    {
        public GitHubReleaseResource()
        {
            Assets = new List<GitHubReleaseAssetResource>();
        }

        [JsonProperty("tag_name")]
        public string TagName { get; set; }

        public string Name { get; set; }

        public bool Draft { get; set; }

        public bool Prerelease { get; set; }

        [JsonProperty("published_at")]
        public DateTime? PublishedAt { get; set; }

        public string Body { get; set; }

        public List<GitHubReleaseAssetResource> Assets { get; set; }
    }

    public class GitHubReleaseAssetResource
    {
        public string Name { get; set; }

        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }
}
