using System;
using System.Diagnostics;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Sentry;

namespace NzbDrone.Common.Instrumentation
{
    public static class NzbDroneLogger
    {
        private const string FILE_LOG_LAYOUT = @"${date:format=yyyy-MM-dd HH\:mm\:ss.f}|${level}|${logger}|${message}${onexception:inner=${newline}${newline}[v${assembly-version}] ${exception:format=ToString}${newline}${exception:format=Data}${newline}}";

        private static bool _isConfigured;

        static NzbDroneLogger()
        {
            LogManager.Configuration = new LoggingConfiguration();
        }

        public static void Register(IStartupContext startupContext, bool updateApp, bool inConsole)
        {
            if (_isConfigured)
            {
                throw new InvalidOperationException("Loggers have already been registered.");
            }

            _isConfigured = true;

            GlobalExceptionHandlers.Register();

            var appFolderInfo = new AppFolderInfo(startupContext);

            RegisterGlobalFilters();

            if (Debugger.IsAttached)
            {
                RegisterDebugger();
            }

            RegisterSentry(updateApp, appFolderInfo);

            if (updateApp)
            {
                RegisterUpdateFile(appFolderInfo);
            }
            else
            {
                if (inConsole && (OsInfo.IsNotWindows || RuntimeInfo.IsUserInteractive))
                {
                    RegisterConsole();
                }

                RegisterAppFile(appFolderInfo);
            }

            RegisterAuthLogger();

            LogManager.ReconfigExistingLoggers();
        }

        private static void RegisterSentry(bool updateClient, IAppFolderInfo appFolderInfo)
        {
            // Upstream hard-coded Readarr's own sentry.servarr.com DSNs here, and
            // AnalyticsEnabled defaults to true, so every production install reported
            // its crashes to Servarr's instance. That was correct for Readarr and is
            // not correct for a fork of it: Librarr has no crash-reporting service of
            // its own, so those events went to a retired project that never agreed to
            // receive them and to maintainers who cannot act on them. Shipping other
            // people's crash data to a third party by default is not ours to do.
            //
            // Telemetry is therefore off unless an operator points it at a DSN they
            // control. This is read from the environment rather than config.xml
            // because logging is registered before the config layer exists.
            var dsn = Environment.GetEnvironmentVariable("LIBRARR_SENTRY_DSN");

            if (dsn.IsNullOrWhiteSpace())
            {
                return;
            }

            var target = new SentryTarget(dsn, appFolderInfo)
            {
                Name = "sentryTarget",
                Layout = "${message}"
            };

            var loggingRule = new LoggingRule("*", updateClient ? LogLevel.Trace : LogLevel.Debug, target);
            LogManager.Configuration.AddTarget("sentryTarget", target);
            LogManager.Configuration.LoggingRules.Add(loggingRule);

            // Events logged to Sentry go only to Sentry.
            var loggingRuleSentry = new LoggingRule("Sentry", LogLevel.Debug, target) { Final = true };
            LogManager.Configuration.LoggingRules.Insert(0, loggingRuleSentry);
        }

        private static void RegisterDebugger()
        {
            var target = new DebuggerTarget();
            target.Name = "debuggerLogger";
            target.Layout = "[${level}] [${threadid}] ${logger}: ${message} ${onexception:inner=${newline}${newline}[v${assembly-version}] ${exception:format=ToString}${newline}${exception:format=Data}${newline}}";

            var loggingRule = new LoggingRule("*", LogLevel.Trace, target);
            LogManager.Configuration.AddTarget("debugger", target);
            LogManager.Configuration.LoggingRules.Add(loggingRule);
        }

        private static void RegisterGlobalFilters()
        {
            LogManager.Setup().LoadConfiguration(c =>
            {
                c.ForLogger("System.*").WriteToNil(LogLevel.Warn);
                c.ForLogger("Microsoft.*").WriteToNil(LogLevel.Warn);
                c.ForLogger("Microsoft.Hosting.Lifetime*").WriteToNil(LogLevel.Info);
                c.ForLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").WriteToNil(LogLevel.Fatal);
            });
        }

        private static void RegisterConsole()
        {
            var level = LogLevel.Trace;

            var coloredConsoleTarget = new ColoredConsoleTarget();

            coloredConsoleTarget.Name = "consoleLogger";
            coloredConsoleTarget.Layout = "[${level}] ${logger}: ${message} ${onexception:inner=${newline}${newline}[v${assembly-version}] ${exception:format=ToString}${newline}${exception:format=Data}${newline}}";

            var loggingRule = new LoggingRule("*", level, coloredConsoleTarget);

            LogManager.Configuration.AddTarget("console", coloredConsoleTarget);
            LogManager.Configuration.LoggingRules.Add(loggingRule);
        }

        private static void RegisterAppFile(IAppFolderInfo appFolderInfo)
        {
            RegisterAppFile(appFolderInfo, "appFileInfo", "readarr.txt", 5, LogLevel.Info);
            RegisterAppFile(appFolderInfo, "appFileDebug", "readarr.debug.txt", 50, LogLevel.Off);
            RegisterAppFile(appFolderInfo, "appFileTrace", "readarr.trace.txt", 50, LogLevel.Off);
        }

        private static void RegisterAppFile(IAppFolderInfo appFolderInfo, string name, string fileName, int maxArchiveFiles, LogLevel minLogLevel)
        {
            var fileTarget = new NzbDroneFileTarget();

            fileTarget.Name = name;
            fileTarget.FileName = Path.Combine(appFolderInfo.GetLogFolder(), fileName);
            fileTarget.AutoFlush = true;
            fileTarget.KeepFileOpen = false;
            fileTarget.ArchiveAboveSize = 1024000;
            fileTarget.MaxArchiveFiles = maxArchiveFiles;
            fileTarget.EnableFileDelete = true;
            // NLog 6 dropped rolling numbering; its default suffix is "_00", but the
            // Logs page only serves names matching [-.a-zA-Z0-9]+.txt. Keep readarr.N.txt.
            fileTarget.ArchiveSuffixFormat = ".{0}";
            fileTarget.Layout = FILE_LOG_LAYOUT;

            var loggingRule = new LoggingRule("*", minLogLevel, fileTarget);

            LogManager.Configuration.AddTarget(name, fileTarget);
            LogManager.Configuration.LoggingRules.Add(loggingRule);
        }

        private static void RegisterUpdateFile(IAppFolderInfo appFolderInfo)
        {
            var fileTarget = new FileTarget();

            fileTarget.Name = "updateFileLogger";
            fileTarget.FileName = Path.Combine(appFolderInfo.GetUpdateLogFolder(), DateTime.Now.ToString("yyyy.MM.dd-HH.mm") + ".txt");
            fileTarget.AutoFlush = true;
            fileTarget.KeepFileOpen = false;
            fileTarget.Layout = FILE_LOG_LAYOUT;

            var loggingRule = new LoggingRule("*", LogLevel.Trace, fileTarget);

            LogManager.Configuration.AddTarget("updateFile", fileTarget);
            LogManager.Configuration.LoggingRules.Add(loggingRule);
        }

        private static void RegisterAuthLogger()
        {
            var consoleTarget = LogManager.Configuration.FindTargetByName("console");
            var fileTarget = LogManager.Configuration.FindTargetByName("appFileInfo");

            var target = consoleTarget ?? fileTarget ?? new NullTarget();

            // Send Auth to Console and info app file, but not the log database
            var rule = new LoggingRule("Auth", LogLevel.Info, target) { Final = true };
            if (consoleTarget != null && fileTarget != null)
            {
                rule.Targets.Add(fileTarget);
            }

            LogManager.Configuration.LoggingRules.Insert(0, rule);
        }

        public static Logger GetLogger(Type obj)
        {
            return LogManager.GetLogger(obj.Name.Replace("NzbDrone.", ""));
        }

        public static Logger GetLogger(object obj)
        {
            return GetLogger(obj.GetType());
        }
    }
}
