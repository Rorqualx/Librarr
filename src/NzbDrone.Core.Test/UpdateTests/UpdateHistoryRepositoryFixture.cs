using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Update.History;

namespace NzbDrone.Core.Test.UpdateTests
{
    // Runs against the log database. On PostgreSQL (the "Unit tests
    // (PostgreSQL)" job, whose server deliberately does not run in UTC) these
    // are the tests that fail if UpdateHistory.Date goes back to being a
    // zone-less timestamp; see migration 049.
    [TestFixture]
    public class UpdateHistoryRepositoryFixture : DbTest<UpdateHistoryRepository, UpdateHistory>
    {
        private static readonly DateTime Installed = new DateTime(2026, 1, 15, 3, 30, 0, DateTimeKind.Utc);

        protected override MigrationType MigrationType => MigrationType.Log;

        private void GivenInstalled(DateTime date, string version)
        {
            Subject.Insert(new UpdateHistory
            {
                Date = date,
                Version = new Version(version),
                EventType = UpdateHistoryEventType.Installed
            });
        }

        [Test]
        public void should_round_trip_date_as_the_same_instant()
        {
            GivenInstalled(Installed, "1.2.3.4");

            Subject.LastInstalled().Date.ToUniversalTime().Should().Be(Installed);
        }

        [Test]
        public void should_order_installs_by_date()
        {
            GivenInstalled(Installed.AddDays(1), "1.2.4.0");
            GivenInstalled(Installed, "1.2.3.0");
            GivenInstalled(Installed.AddDays(2), "1.2.5.0");

            Subject.LastInstalled().Version.Should().Be(new Version("1.2.5.0"));
            Subject.PreviouslyInstalled().Version.Should().Be(new Version("1.2.4.0"));
        }

        [Test]
        public void should_compare_installed_since_against_the_utc_instant()
        {
            // An hour either side of the cut-off, so an offset error of the
            // size a time zone introduces would move one across it.
            GivenInstalled(Installed.AddHours(-1), "1.2.3.0");
            GivenInstalled(Installed.AddHours(1), "1.2.4.0");

            Subject.InstalledSince(Installed).Select(x => x.Version)
                .Should().Equal(new Version("1.2.4.0"));
        }

        [Test]
        public void postgres_log_db_should_not_contain_timestamp_without_timezone_columns()
        {
            // The log-database counterpart of the check DatabaseFixture makes
            // on the main database.
            if (Db.DatabaseType != DatabaseType.PostgreSQL)
            {
                return;
            }

            Mocker.Resolve<IDatabase>()
                .OpenConnection().Query("SELECT table_name, column_name, data_type FROM INFORMATION_SCHEMA.COLUMNS WHERE table_schema = 'public' AND data_type = 'timestamp without time zone'")
                .Should()
                .BeNullOrEmpty();
        }
    }
}
