using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Test.Common
{
    public abstract class LoggingTest
    {
        protected static readonly Logger TestLogger = NzbDroneLogger.GetLogger("TestLogger");

        protected static void InitLogging()
        {
            // Force NzbDroneLogger's static constructor to run BEFORE anything
            // below installs a target. Its body is
            //
            //     LogManager.Configuration = new LoggingConfiguration();
            //
            // which discards every target, and it fires lazily on the first
            // touch of the static TestLogger field -- which is the
            // TestLogger.Info(...) at the end of LoggingTestSetup, i.e. after
            // this method has finished registering ExceptionVerification.
            //
            // So the first test executed in any test process ran with no log
            // capture at all. That surfaced as ExpectedWarns(1) seeing zero
            // warns, but only when a filter put a log-asserting test first;
            // a full-assembly run happens to start elsewhere, which is why CI
            // never saw it. The quiet half is worse: AssertNoUnexpectedLogs()
            // passes vacuously in that slot, because nothing was captured.
            RuntimeHelpers.RunClassConstructor(typeof(NzbDroneLogger).TypeHandle);

            new StartupContext();

            if (LogManager.Configuration == null || LogManager.Configuration.AllTargets.None(c => c is ExceptionVerification))
            {
                LogManager.Configuration = new LoggingConfiguration();

                Enum.TryParse<TestLogOutput>(Environment.GetEnvironmentVariable("READARR_TESTS_LOG_OUTPUT"), out var logOutput);

                RegisterSentryLogger();

                switch (logOutput)
                {
                    case TestLogOutput.Console:
                        RegisterConsoleLogger();
                        break;
                    case TestLogOutput.File:
                        RegisterFileLogger();
                        break;
                }

                RegisterExceptionVerification();

                LogManager.ReconfigExistingLoggers();
            }
        }

        private static void RegisterConsoleLogger()
        {
            var consoleTarget = new ConsoleTarget { Layout = "${date:format=HH\\:mm\\:ss.f} ${level}: ${message} ${exception}" };
            LogManager.Configuration.AddTarget(consoleTarget.GetType().Name, consoleTarget);
            LogManager.Configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Trace, consoleTarget));
        }

        private static void RegisterFileLogger()
        {
            const string layout = @"${level}|${message}${onexception:inner=${newline}${newline}${exception:format=ToString}${newline}}";

            var fileTarget = new FileTarget();

            fileTarget.Name = "Test File Logger";
            fileTarget.FileName = Path.Combine(TestContext.CurrentContext.WorkDirectory, "TestLog.txt");
            fileTarget.AutoFlush = false;
            fileTarget.KeepFileOpen = true;
            fileTarget.Layout = layout;

            LogManager.Configuration.AddTarget(fileTarget.GetType().Name, fileTarget);
            LogManager.Configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Trace, fileTarget));
        }

        private static void RegisterSentryLogger()
        {
            // Register a null target for sentry logs, so they aren't caught by other loggers.
            var loggingRuleSentry = new LoggingRule("Sentry", LogLevel.Debug, new NullTarget()) { Final = true };
            LogManager.Configuration.LoggingRules.Insert(0, loggingRuleSentry);
        }

        private static void RegisterExceptionVerification()
        {
            var exceptionVerification = new ExceptionVerification();
            LogManager.Configuration.AddTarget("ExceptionVerification", exceptionVerification);
            LogManager.Configuration.LoggingRules.Add(new LoggingRule("*", LogLevel.Warn, exceptionVerification));
        }

        [SetUp]
        public void LoggingTestSetup()
        {
            InitLogging();
            ExceptionVerification.Reset();
            TestLogger.Info("--- Start: {0} ---", TestContext.CurrentContext.Test.FullName);

            // Checked after the TestLogger touch, because that touch is what
            // used to wipe the configuration. A missing capture target does not
            // announce itself -- every log assertion in the test simply sees an
            // empty list, so ExpectedWarns fails for the wrong reason and
            // AssertNoUnexpectedLogs succeeds for no reason. Fail loudly here
            // instead, wherever the next thing to reset the configuration
            // appears.
            if (LogManager.Configuration.AllTargets.None(c => c is ExceptionVerification))
            {
                Assert.Fail(
                    "The ExceptionVerification log target is not attached, so this test cannot " +
                    "observe any log output. Something reset LogManager.Configuration after " +
                    "InitLogging registered it — see the note there about NzbDroneLogger.");
            }
        }

        [TearDown]
        public void LoggingDownBase()
        {
            if (TestContext.CurrentContext.Result.Outcome == ResultState.Success)
            {
                // The mono 2.6.2 nunit teardown bug this guard was added for
                // (https://bugs.launchpad.net/nunitv2/+bug/1076932, 2012)
                // predates .NET Core; the Debug gate survives only to keep a
                // bare `dotnet test` fast. Its cost is that a *Release*-
                // configured run verifies none of its logs — and because
                // OutputPath ignores $(Configuration) (Directory.Build.props),
                // building Release once (e.g. to check StyleCop) leaves every
                // later `dotnet test` silently Release-flavoured with this
                // assertion off. CI ran only published Release artifacts, so it
                // never fired there at all — eleven undeclared Error logs rode
                // in that way (issue #12). CI now runs a Debug unit-test leg
                // (build.yml) so it fires; here, announce the skip once per run
                // so a local Release run is never silently, half-verified green.
                if (BuildInfo.IsDebug)
                {
                    ExceptionVerification.AssertNoUnexpectedLogs();
                }
                else
                {
                    AnnounceLogAssertionsDisabledOnce();
                }
            }

            TestLogger.Info("--- End: {0} ---", TestContext.CurrentContext.Test.FullName);
        }

        private static int _logAssertionSkipAnnounced;

        private static void AnnounceLogAssertionsDisabledOnce()
        {
            if (Interlocked.Exchange(ref _logAssertionSkipAnnounced, 1) == 0)
            {
                TestContext.Progress.WriteLine(
                    "WARNING: AssertNoUnexpectedLogs is OFF — this run is not a Debug build, so " +
                    "undeclared Warn/Error/Fatal logs will NOT fail any test. Rebuild in Debug to " +
                    "re-enable it (issue #12); OutputPath ignores $(Configuration), so a single " +
                    "earlier Release build makes every later `dotnet test` here Release-flavoured.");
            }
        }
    }
}
