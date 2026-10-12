using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedNarrators : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedNarrators(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""Narrators""
                             WHERE ""Id"" IN (
                             SELECT ""Narrators"".""Id"" FROM ""Narrators""
                             LEFT OUTER JOIN ""EditionNarrators""
                             ON ""Narrators"".""Id"" = ""EditionNarrators"".""NarratorId""
                             WHERE ""EditionNarrators"".""Id"" IS NULL)");
        }
    }
}
