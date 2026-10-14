using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Processes;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using RestSharp;

namespace NzbDrone.Test.Common
{
    public class NzbDroneRunner
    {
        private readonly IProcessProvider _processProvider;
        private readonly IRestClient _restClient;
        private Process _nzbDroneProcess;
        private List<string> _startupLog;

        public string AppData { get; private set; }
        public string ApiKey { get; private set; }
        public PostgresOptions PostgresOptions { get; private set; }
        public int Port { get; private set; }
        public string UrlBase { get; private set; }

        public NzbDroneRunner(Logger logger, PostgresOptions postgresOptions, int port = 8787, string urlBase = "")
        {
            _processProvider = new ProcessProvider(logger);
            _restClient = new RestClient($"http://localhost:{port}{urlBase}/api/v1");

            PostgresOptions = postgresOptions;
            Port = port;
            UrlBase = urlBase;
        }

        public void Start(bool enableAuth = false)
        {
            AppData = Path.Combine(TestContext.CurrentContext.TestDirectory, "_intg_" + TestBase.GetUID());
            Directory.CreateDirectory(AppData);

            GenerateConfigFile(enableAuth);

            string readarrConsoleExe;
            if (OsInfo.IsWindows)
            {
                readarrConsoleExe = "Readarr.Console.exe";
            }
            else
            {
                readarrConsoleExe = "Readarr";
            }

            _startupLog = new List<string>();

            // net10.0, not net6.0. The .NET 10 migration missed this path, and
            // because the old _output/net6.0 tree survives on disk from any
            // earlier build, every integration test went on booting a
            // pre-migration binary without complaint -- passing or failing on
            // behaviour that had not been in the codebase for months. Nothing
            // pointed at it: the suite is gated behind READARR_RUN_INTEGRATION,
            // so nobody was watching.
            var binaryPath = BuildInfo.IsDebug
                ? Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "_output", "net10.0", readarrConsoleExe)
                : Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "bin", readarrConsoleExe);

            // Say which binary, and when it was built. The failure mode above
            // was silent for months precisely because nothing ever named the
            // file it launched: the Release path (_tests/bin) is populated by
            // a full ./build.sh, so a plain `dotnet test` refreshes the test
            // assemblies and leaves the app behind, and the only symptom is
            // assertions failing against code you are looking at.
            var resolved = Path.GetFullPath(binaryPath);

            if (!File.Exists(resolved))
            {
                Assert.Fail(
                    $"Readarr binary not found at {resolved}. " +
                    "Run ./build.sh --backend first, or run the suite in Debug so it picks up _output/net10.0.");
            }

            TestContext.Progress.WriteLine(
                $"Starting Readarr from {resolved} (built {File.GetLastWriteTime(resolved):s})");

            AssertAppIsNotStale(Path.GetDirectoryName(resolved));

            Start(resolved);

            while (true)
            {
                _nzbDroneProcess.Refresh();

                if (_nzbDroneProcess.HasExited)
                {
                    TestContext.Progress.WriteLine("Readarr has exited unexpectedly");
                    Thread.Sleep(2000);
                    var output = _startupLog.Join(Environment.NewLine);
                    Assert.Fail("Process has exited: ExitCode={0} Output={1}", _nzbDroneProcess.ExitCode, output);
                }

                var request = new RestRequest("system/status");
                request.AddHeader("Authorization", ApiKey);
                request.AddHeader("X-Api-Key", ApiKey);

                var statusCall = _restClient.Get(request);

                if (statusCall.ResponseStatus == ResponseStatus.Completed)
                {
                    _startupLog = null;
                    TestContext.Progress.WriteLine($"Readarr {Port} is started. Running Tests");
                    return;
                }

                TestContext.Progress.WriteLine("Waiting for Readarr to start. Response Status : {0}  [{1}] {2}", statusCall.ResponseStatus, statusCall.StatusDescription, statusCall.ErrorException.Message);

                Thread.Sleep(500);
            }
        }

        // Printing the binary's build time (above) was not enough. The number
        // is only meaningful next to something to compare it against, and on
        // its own it reads as provenance rather than as a warning -- a whole
        // afternoon went into three "failing" Playwright tests that were
        // really one app tree built before the features under test existed.
        //
        // The app serves the UI folder sitting beside its own binary, and no
        // single command keeps that folder current:
        //
        //   yarn build             writes _output/UI, and nothing else
        //   ./build.sh --backend   rebuilds the app -- and starts with
        //                          `rm -rf _output`, so it DELETES _output/UI
        //   ./build.sh (no args)   builds both, then PackageFiles() copies
        //                          _output/UI next to the binary
        //
        // Only the third produces a coherent tree. Iterating with the first
        // two, in either order, leaves the running app serving whichever
        // frontend happened to be copied there last. Every symptom of that is
        // a lie: assertions fail against code you are looking at, and -- worse
        // -- assertions *pass* against code you deleted.
        private static void AssertAppIsNotStale(string appFolder)
        {
            var servedUi = Path.Combine(appFolder, "UI", "index.html");

            if (!File.Exists(servedUi))
            {
                Assert.Fail(
                    $"No UI at {Path.Combine(appFolder, "UI")}. Run a full `./build.sh`, or " +
                    "`yarn build` followed by copying _output/UI next to the binary.");
            }

            // _output/UI is the frontend's own output, two or three levels up
            // depending on whether this build is RID-specific. Absent in a CI
            // Release layout, in which case there is nothing to compare and
            // the check is skipped.
            var builtUi = FindFrontendOutput(appFolder);

            if (builtUi == null)
            {
                return;
            }

            var servedAt = File.GetLastWriteTimeUtc(servedUi);
            var builtAt = File.GetLastWriteTimeUtc(builtUi);

            if (builtAt > servedAt)
            {
                Assert.Fail(
                    $"The UI next to the binary is stale: {servedUi} was written {servedAt:s}, " +
                    $"but {builtUi} is newer ({builtAt:s}). `yarn build` writes only the latter. " +
                    $"Copy it across (`cp -r {Path.GetDirectoryName(builtUi)} {appFolder}/`) or run a " +
                    "full `./build.sh` -- otherwise the suite tests a frontend you no longer have.");
            }
        }

        private static string FindFrontendOutput(string appFolder)
        {
            // Resolve first. The Release path is _tests/bin, which is a symlink
            // into _output/<tfm>/<rid>, and Path.GetFullPath is purely lexical
            // -- so walking up the unresolved path climbs out through _tests and
            // never sees _output at all. That would leave this check quietly
            // skipping itself, which is the failure mode it exists to prevent.
            var start = Directory.ResolveLinkTarget(appFolder, true)?.FullName ?? appFolder;

            for (var folder = new DirectoryInfo(start); folder != null; folder = folder.Parent)
            {
                var candidate = folder.Name == "_output"
                    ? Path.Combine(folder.FullName, "UI", "index.html")
                    : Path.Combine(folder.FullName, "_output", "UI", "index.html");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public void Kill()
        {
            try
            {
                if (_nzbDroneProcess != null)
                {
                    _nzbDroneProcess.Refresh();
                    if (_nzbDroneProcess.HasExited)
                    {
                        var log = File.ReadAllLines(Path.Combine(AppData, "logs", "readarr.trace.txt"));
                        var output = log.Join(Environment.NewLine);
                        TestContext.Progress.WriteLine("Process has exited prematurely: ExitCode={0} Output:\n{1}", _nzbDroneProcess.ExitCode, output);
                    }

                    _processProvider.Kill(_nzbDroneProcess.Id);
                }
            }
            catch (InvalidOperationException)
            {
                // May happen if the process closes while being closed
            }

            TestBase.DeleteTempFolder(AppData);
        }

        public void KillAll()
        {
            try
            {
                if (_nzbDroneProcess != null)
                {
                    _processProvider.Kill(_nzbDroneProcess.Id);
                }

                _processProvider.KillAll(ProcessProvider.READARR_CONSOLE_PROCESS_NAME);
                _processProvider.KillAll(ProcessProvider.READARR_PROCESS_NAME);
            }
            catch (InvalidOperationException)
            {
                // May happen if the process closes while being closed
            }

            TestBase.DeleteTempFolder(AppData);
        }

        private void Start(string outputNzbdroneConsoleExe)
        {
            StringDictionary envVars = new ();
            if (PostgresOptions?.Host != null)
            {
                envVars.Add("Readarr__Postgres__Host", PostgresOptions.Host);
                envVars.Add("Readarr__Postgres__Port", PostgresOptions.Port.ToString());
                envVars.Add("Readarr__Postgres__User", PostgresOptions.User);
                envVars.Add("Readarr__Postgres__Password", PostgresOptions.Password);
                envVars.Add("Readarr__Postgres__MainDb", PostgresOptions.MainDb);
                envVars.Add("Readarr__Postgres__LogDb", PostgresOptions.LogDb);
                envVars.Add("Readarr__Postgres__CacheDb", PostgresOptions.CacheDb);

                TestContext.Progress.WriteLine("Using env vars:\n{0}", envVars.ToJson());
            }

            TestContext.Progress.WriteLine("Starting instance from {0} on port {1}", outputNzbdroneConsoleExe, Port);

            var args = "-nobrowser -nosingleinstancecheck -data=\"" + AppData + "\"";
            _nzbDroneProcess = _processProvider.Start(outputNzbdroneConsoleExe, args, envVars, OnOutputDataReceived, OnOutputDataReceived);
        }

        private void OnOutputDataReceived(string data)
        {
            TestContext.Progress.WriteLine($" [{Port}] > " + data);

            if (_startupLog != null)
            {
                _startupLog.Add(data);
            }

            if (data.Contains("Press enter to exit"))
            {
                _nzbDroneProcess.StandardInput.WriteLine(" ");
            }
        }

        private void GenerateConfigFile(bool enableAuth)
        {
            var configFile = Path.Combine(AppData, "config.xml");

            // Generate and set the api key so we don't have to poll the config file
            var apiKey = Guid.NewGuid().ToString().Replace("-", "");

            var xDoc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(ConfigFileProvider.CONFIG_ELEMENT_NAME,
                             new XElement(nameof(ConfigFileProvider.ApiKey), apiKey),
                             new XElement(nameof(ConfigFileProvider.LogLevel), "trace"),
                             new XElement(nameof(ConfigFileProvider.AnalyticsEnabled), false),
                             new XElement(nameof(ConfigFileProvider.AuthenticationMethod), enableAuth ? "Forms" : "None"),
                             new XElement(nameof(ConfigFileProvider.AuthenticationRequired), "DisabledForLocalAddresses"),
                             new XElement(nameof(ConfigFileProvider.Port), Port),
                             new XElement(nameof(ConfigFileProvider.UrlBase), UrlBase)));

            var data = xDoc.ToString();

            File.WriteAllText(configFile, data);

            ApiKey = apiKey;
        }
    }
}
