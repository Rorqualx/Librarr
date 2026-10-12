using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Update;
using NzbDrone.Core.Update.History;
using Readarr.Http;

namespace Readarr.Api.V1.Update
{
    [V1ApiController]
    public class UpdateController : Controller
    {
        private readonly IRecentUpdateProvider _recentUpdateProvider;
        private readonly IUpdateHistoryService _updateHistoryService;
        private readonly ICheckUpdateService _checkUpdateService;

        public UpdateController(IRecentUpdateProvider recentUpdateProvider, IUpdateHistoryService updateHistoryService, ICheckUpdateService checkUpdateService)
        {
            _recentUpdateProvider = recentUpdateProvider;
            _checkUpdateService = checkUpdateService;
            _updateHistoryService = updateHistoryService;
        }

        [HttpGet]
        public List<UpdateResource> GetRecentUpdates()
        {
            var resources = _recentUpdateProvider.GetRecentUpdatePackages()
                                                 .OrderByDescending(u => u.Version)
                                                 .ToResource();

            if (resources.Any())
            {
                var first = resources.First();
                first.Latest = true;

                // The list is the changelog and names every release. Only one
                // the update check would actually hand to the installer, with a
                // package for this platform and a checksum to verify it against,
                // is installable. Both answers come out of the same cached fetch.
                if (first.Version > BuildInfo.Version)
                {
                    var available = _checkUpdateService.AvailableUpdate();

                    first.Installable = available != null && available.Version == first.Version;
                }

                var installed = resources.SingleOrDefault(r => r.Version == BuildInfo.Version);

                if (installed != null)
                {
                    installed.Installed = true;
                }

                var installDates = _updateHistoryService.InstalledSince(resources.Last().ReleaseDate)
                                                        .DistinctBy(v => v.Version)
                                                        .ToDictionary(v => v.Version);

                foreach (var resource in resources)
                {
                    if (installDates.TryGetValue(resource.Version, out var installDate))
                    {
                        resource.InstalledOn = installDate.Date;
                    }
                }
            }

            return resources;
        }
    }
}
