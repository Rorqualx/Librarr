using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(049)]
    public class postgres_update_timestamp_columns_to_with_timezone_part3 : NzbDroneMigrationBase
    {
        // Same repair as migrations 023 and 032, for two columns that were
        // created with AsDateTime() and so became `timestamp without time
        // zone` on PostgreSQL:
        //
        //   * BookIdMapping.CreatedUtc (main db, migration 041, the fork's)
        //   * UpdateHistory.Date       (log db, migration 024, inherited)
        //
        // The app sends UTC instants. Into a zone-less column PostgreSQL
        // stores them as wall-clock time in the server's zone and then
        // forgets the zone, so on any server not running in UTC the value
        // read back is off by the UTC offset (measured in CI: a value
        // written as 03:30Z came back as 21:30 from a server in
        // America/Chicago).
        //
        // Changing the type to timestamptz makes PostgreSQL reinterpret the
        // existing values in the session's zone, which is the zone they were
        // written in, so existing rows come out as the instants originally
        // intended.
        //
        // PostgreSQL only. SQLite stores both declarations identically, and
        // an AlterColumn there rebuilds the whole table to change nothing.
        protected override void MainDbUpgrade()
        {
            IfDatabase("postgres").Alter.Table("BookIdMapping").AlterColumn("CreatedUtc").AsDateTimeOffset().NotNullable();
        }

        protected override void LogDbUpgrade()
        {
            IfDatabase("postgres").Alter.Table("UpdateHistory").AlterColumn("Date").AsDateTimeOffset().NotNullable();
        }
    }
}
