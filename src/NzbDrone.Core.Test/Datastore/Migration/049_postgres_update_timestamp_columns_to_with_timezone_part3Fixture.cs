using System;
using System.Data;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class postgres_update_timestamp_columns_to_with_timezone_part3Fixture : MigrationTest<postgres_update_timestamp_columns_to_with_timezone_part3>
    {
        private static readonly DateTime Created = new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc);

        // Writes the row the way the app wrote it before the migration: a UTC
        // DateTime handed to the driver as a parameter. A literal in the SQL
        // text would not do, since it is the driver sending an instant into
        // a zone-less column that loses the zone.
        private static void InsertMapping(IDbConnection conn, IDbTransaction tran)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tran;
            cmd.CommandText = "INSERT INTO \"BookIdMapping\" (\"GoodreadsId\", \"Confidence\", \"Source\", \"CreatedUtc\") VALUES ('5907', 1, 'ISBN', @created)";

            var created = cmd.CreateParameter();
            created.ParameterName = "created";
            created.Value = Created;
            cmd.Parameters.Add(created);

            cmd.ExecuteNonQuery();
        }

        [Test]
        public void should_keep_the_instant_of_rows_written_before_the_migration()
        {
            WithMigrationTestDb(c => c.Execute.WithConnection(InsertMapping));

            using var conn = Mocker.Resolve<IDatabase>().OpenConnection();
            var stored = conn.Query<DateTime>("SELECT \"CreatedUtc\" FROM \"BookIdMapping\"").Single();

            stored.ToUniversalTime().Should().Be(Created);
        }

        [Test]
        public void should_leave_no_zoneless_timestamp_on_postgres()
        {
            WithMigrationTestDb();

            var db = Mocker.Resolve<IDatabase>();
            if (db.DatabaseType != DatabaseType.PostgreSQL)
            {
                return;
            }

            using var conn = db.OpenConnection();
            conn.Query<string>("SELECT data_type FROM INFORMATION_SCHEMA.COLUMNS WHERE table_name = 'BookIdMapping' AND column_name = 'CreatedUtc'")
                .Single()
                .Should().Be("timestamp with time zone");
        }
    }
}
