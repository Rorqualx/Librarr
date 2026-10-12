using System.Runtime.InteropServices;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Update
{
    public interface IUpdatePlatform
    {
        // The .NET runtime identifier of the running build, in the spelling the
        // release assets use (linux-x64, linux-musl-arm64, osx-arm64, win-x86...),
        // or null when no package is published for this platform.
        string RuntimeIdentifier { get; }
    }

    public class UpdatePlatform : IUpdatePlatform
    {
        // ProcessArchitecture rather than OSArchitecture: an update replaces the
        // binaries that are running, so an x86 build on 64-bit Windows, or an x64
        // build under Rosetta, has to be offered the same flavour again.
        public string RuntimeIdentifier => GetRuntimeIdentifier(OsInfo.Os, RuntimeInformation.ProcessArchitecture);

        public static string GetRuntimeIdentifier(Os os, Architecture architecture)
        {
            string osPart;

            switch (os)
            {
                case Os.Windows:
                    osPart = "win";
                    break;
                case Os.Osx:
                    osPart = "osx";
                    break;
                case Os.Linux:
                    osPart = "linux";
                    break;
                case Os.LinuxMusl:
                    osPart = "linux-musl";
                    break;
                default:
                    // Bsd: nothing is published for it.
                    return null;
            }

            string archPart;

            switch (architecture)
            {
                case Architecture.X64:
                    archPart = "x64";
                    break;
                case Architecture.X86:
                    archPart = "x86";
                    break;
                case Architecture.Arm:
                    archPart = "arm";
                    break;
                case Architecture.Arm64:
                    archPart = "arm64";
                    break;
                default:
                    return null;
            }

            return $"{osPart}-{archPart}";
        }

        // release.yml names the archives Librarr.<version>.<rid>.tar.gz, or .zip
        // for Windows. The exact name is matched, so the macOS "-app.zip" bundles
        // and the Windows "-installer.exe" files are never picked up as update
        // packages: the built-in updater cannot apply either of them.
        public static string GetPackageFileName(string releaseVersion, string runtimeIdentifier)
        {
            if (string.IsNullOrWhiteSpace(releaseVersion) || string.IsNullOrWhiteSpace(runtimeIdentifier))
            {
                return null;
            }

            var extension = runtimeIdentifier.StartsWith("win-") ? "zip" : "tar.gz";

            return $"{BuildInfo.AppName}.{releaseVersion}.{runtimeIdentifier}.{extension}";
        }
    }
}
