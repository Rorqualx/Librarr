using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class CleanupOrphanedEditionNarrators : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public CleanupOrphanedEditionNarrators(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();
            mapper.Execute(@"DELETE FROM ""EditionNarrators""
                             WHERE ""Id"" IN (
                             SELECT ""EditionNarrators"".""Id"" FROM ""EditionNarrators""
                             LEFT OUTER JOIN ""Editions""
                             ON ""EditionNarrators"".""EditionId"" = ""Editions"".""Id""
                             WHERE ""Editions"".""Id"" IS NULL)");
        }
    }
}
