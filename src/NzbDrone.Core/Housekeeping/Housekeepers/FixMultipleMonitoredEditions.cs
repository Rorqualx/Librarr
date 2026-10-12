using Dapper;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Housekeeping.Housekeepers
{
    public class FixMultipleMonitoredEditions : IHousekeepingTask
    {
        private readonly IMainDatabase _database;

        public FixMultipleMonitoredEditions(IMainDatabase database)
        {
            _database = database;
        }

        public void Clean()
        {
            using var mapper = _database.OpenConnection();

            // Raw SQL rather than SqlBuilder, like every other housekeeper:
            // this is a set-based UPDATE over a subquery, which the builder
            // cannot express. It is one statement for both engines, with the
            // boolean passed as a parameter so neither needs its own literal,
            // and FixMultipleMonitoredEditionsFixture runs it on both.
            //
            // This used to be two branches. The PostgreSQL one set
            // "Monitored" = true on rows it had just selected for being
            // monitored, so it changed nothing and the duplicates stayed. The
            // SQLite one unmonitored only the lowest id per book, so a book
            // with three monitored editions still had two after a pass.
            //
            // The survivor is the highest id, which is what the SQLite branch
            // converged on over repeated passes.
            mapper.Execute(@"UPDATE ""Editions""
                             SET ""Monitored"" = @notMonitored
                             WHERE ""Monitored"" = @monitored
                             AND ""Id"" NOT IN (
                                 SELECT MAX(""Id"")
                                 FROM ""Editions""
                                 WHERE ""Monitored"" = @monitored
                                 GROUP BY ""BookId""
                             )",
                new { monitored = true, notMonitored = false });
        }
    }
}
