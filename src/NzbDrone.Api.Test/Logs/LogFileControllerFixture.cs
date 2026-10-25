using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Logs;
using Readarr.Http;

namespace NzbDrone.Api.Test.Logs
{
    // contentsUrl was built as "/api/v1//readarr.txt" (log/file) and
    // "/api/v1/update/readarr.txt" (log/file/update), both 404. It must
    // point at the controller's own GET {filename} route.
    [TestFixture]
    public class LogFileControllerFixture : TestBase
    {
        private void GivenUrlBase(string urlBase)
        {
            Mocker.GetMock<IConfigFileProvider>().SetupGet(s => s.UrlBase).Returns(urlBase);
            Mocker.GetMock<IAppFolderInfo>().Setup(s => s.AppDataFolder).Returns("/config");
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>()
                  .Setup(s => s.GetFiles(It.IsAny<string>(), false))
                  .Returns<string, bool>((folder, _) => new[] { System.IO.Path.Combine(folder, "readarr.txt") });
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FileGetLastWrite(It.IsAny<string>())).Returns(DateTime.UtcNow);
        }

        private static string RouteOf<T>()
        {
            return "/" + typeof(T).GetCustomAttribute<VersionedApiControllerAttribute>().Template;
        }

        [TestCase("")]
        [TestCase("/librarr")]
        public void log_file_contents_url_should_match_controller_route(string urlBase)
        {
            GivenUrlBase(urlBase);

            var file = Mocker.Resolve<LogFileController>().GetLogFilesResponse().Single();

            file.ContentsUrl.Should().Be($"{urlBase}/api/v1/log/file/readarr.txt");
            file.ContentsUrl.Should().Be($"{urlBase}{RouteOf<LogFileController>()}/readarr.txt");
            file.DownloadUrl.Should().Be($"{urlBase}/logfile/readarr.txt");
        }

        [TestCase("")]
        [TestCase("/librarr")]
        public void update_log_file_contents_url_should_match_controller_route(string urlBase)
        {
            GivenUrlBase(urlBase);

            var file = Mocker.Resolve<UpdateLogFileController>().GetLogFilesResponse().Single();

            file.ContentsUrl.Should().Be($"{urlBase}/api/v1/log/file/update/readarr.txt");
            file.ContentsUrl.Should().Be($"{urlBase}{RouteOf<UpdateLogFileController>()}/readarr.txt");
            file.DownloadUrl.Should().Be($"{urlBase}/updatelogfile/readarr.txt");
        }
    }
}
