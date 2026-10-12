using System.Collections.Generic;

namespace NzbDrone.Core.HealthCheck
{
    public interface IServerSideNotificationService
    {
        public List<HealthCheck> GetServerChecks();
    }

    // Upstream fetched /notification from readarr.servarr.com every two hours and
    // rendered whatever message and link came back as a banner on the health page
    // of the admin UI, reporting version, OS, architecture and branch as it asked.
    //
    // That server belongs to the retired upstream project, not to this fork, and
    // text chosen by someone else has no business appearing in Librarr's own UI
    // as if Librarr had said it. There is no Librarr equivalent to ask, so this
    // returns nothing. The type stays so the health-check plumbing is untouched.
    public class ServerSideNotificationService : IServerSideNotificationService
    {
        public List<HealthCheck> GetServerChecks()
        {
            return new List<HealthCheck>();
        }
    }
}
