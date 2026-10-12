using NzbDrone.Common.Http;

namespace NzbDrone.Common.Cloud
{
    public interface IReadarrCloudRequestBuilder
    {
        IHttpRequestBuilderFactory Metadata { get; }
    }

    public class ReadarrCloudRequestBuilder : IReadarrCloudRequestBuilder
    {
        public ReadarrCloudRequestBuilder()
        {
            // There used to be a second factory here, Services, pointing at
            // readarr.servarr.com: the retired upstream project's server, which
            // this fork does not control. Updates, health-page notifications and
            // the time and proxy checks all went through it. It is gone on
            // purpose. Updates come from the project's own GitHub releases
            // (UpdatePackageProvider) and the checks use the metadata source.
            // Do not add it back: Librarr must not fetch instructions, content
            // or packages from, or report installs to, a host it does not run.
            Metadata = new HttpRequestBuilder("https://api.bookinfo.club/v1/{route}")
                .CreateFactory();
        }

        public IHttpRequestBuilderFactory Metadata { get; }
    }
}
