using NLog;
using NzbDrone.Common;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;
using NzbDrone.Host.AccessControl;
using IServiceProvider = NzbDrone.Common.IServiceProvider;

namespace NzbDrone.Host
{
    public interface IUtilityModeRouter
    {
        void Route(ApplicationModes applicationModes);
    }

    public class UtilityModeRouter : IUtilityModeRouter
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConsoleService _consoleService;
        private readonly IProcessProvider _processProvider;
        private readonly IRemoteAccessAdapter _remoteAccessAdapter;
        private readonly IAppFolderFactory _appFolderFactory;
        private readonly Logger _logger;

        public UtilityModeRouter(IServiceProvider serviceProvider,
                      IConsoleService consoleService,
                      IProcessProvider processProvider,
                      IRemoteAccessAdapter remoteAccessAdapter,
                      IAppFolderFactory appFolderFactory,
                      Logger logger)
        {
            _serviceProvider = serviceProvider;
            _consoleService = consoleService;
            _processProvider = processProvider;
            _remoteAccessAdapter = remoteAccessAdapter;
            _appFolderFactory = appFolderFactory;
            _logger = logger;
        }

        public void Route(ApplicationModes applicationModes)
        {
            _logger.Info("Application mode: {0}", applicationModes);

            switch (applicationModes)
            {
                case ApplicationModes.InstallService:
                    {
                        _logger.Debug("Install Service selected");
                        if (_serviceProvider.ServiceExist(ServiceProvider.SERVICE_NAME))
                        {
                            _consoleService.PrintServiceAlreadyExist();
                        }
                        else
                        {
                            _remoteAccessAdapter.MakeAccessible(true);

                            // By now this process, which is elevated, has created the
                            // data folder: the log file is in it, and so is config.xml,
                            // written when the firewall adapter above asked for the port.
                            // Both are owned by an administrator, and the service runs as
                            // LocalService, which %ProgramData%'s default ACL lets create
                            // files but not change somebody else's. A service installed
                            // this way on a machine with no data folder yet reported
                            // RUNNING and never listened -- found by the Windows smoke
                            // job. Opening the folder up here is what /registerurl already
                            // does, and what the service itself would do on a folder it
                            // had created; a folder with a protected ACL (the installer's)
                            // is left as it is.
                            _appFolderFactory.SetPermissions();

                            _serviceProvider.Install(ServiceProvider.SERVICE_NAME);
                            _serviceProvider.SetPermissions(ServiceProvider.SERVICE_NAME);

                            // Start the service and exit.
                            // Ensures that there isn't an instance of Readarr already running that the service account cannot stop.
                            _processProvider.SpawnNewProcess("sc.exe", $"start {ServiceProvider.SERVICE_NAME}", null, true);
                        }

                        break;
                    }

                case ApplicationModes.UninstallService:
                    {
                        _logger.Debug("Uninstall Service selected");
                        if (!_serviceProvider.ServiceExist(ServiceProvider.SERVICE_NAME))
                        {
                            _consoleService.PrintServiceDoesNotExist();
                        }
                        else
                        {
                            _serviceProvider.Uninstall(ServiceProvider.SERVICE_NAME);
                        }

                        break;
                    }

                case ApplicationModes.RegisterUrl:
                    {
                        _logger.Debug("Regiser URL selected");
                        _remoteAccessAdapter.MakeAccessible(false);
                        _appFolderFactory.SetPermissions();

                        break;
                    }

                default:
                    {
                        _consoleService.PrintHelp();
                        break;
                    }
            }
        }
    }
}
