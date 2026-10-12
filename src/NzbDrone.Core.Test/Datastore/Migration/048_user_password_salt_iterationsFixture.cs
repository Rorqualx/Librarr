using System;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Datastore.Migration;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Datastore.Migration
{
    [TestFixture]
    public class user_password_salt_iterationsFixture : MigrationTest<user_password_salt_iterations>
    {
        [Test]
        public void should_add_columns_and_leave_existing_hash_untouched()
        {
            var identifier = Guid.NewGuid().ToString();
            var legacyHash = "a password".SHA256Hash();

            var db = WithMigrationTestDb(c =>
            {
                c.Insert.IntoTable("Users").Row(new
                {
                    Identifier = identifier,
                    Username = "librarian",
                    Password = legacyHash
                });
            });

            var users = db.Query("SELECT * FROM \"Users\"");

            users.Should().HaveCount(1);
            users.First()["Identifier"].Should().Be(identifier);
            users.First()["Username"].Should().Be("librarian");
            users.First()["Password"].Should().Be(legacyHash);
            users.First().Should().ContainKey("Salt");
            (users.First()["Salt"] as string).Should().BeNull();
            Convert.ToInt32(users.First()["Iterations"]).Should().Be(0);
        }

        [Test]
        public void should_leave_existing_user_able_to_log_in()
        {
            var db = WithMigrationTestDb(c =>
            {
                c.Insert.IntoTable("Users").Row(new
                {
                    Identifier = Guid.NewGuid().ToString(),
                    Username = "librarian",
                    Password = "a password".SHA256Hash()
                });
            });

            var row = db.Query("SELECT * FROM \"Users\"").Single();

            var migrated = new User
            {
                Id = Convert.ToInt32(row["Id"]),
                Identifier = Guid.Parse((string)row["Identifier"]),
                Username = (string)row["Username"],
                Password = (string)row["Password"],
                Salt = row["Salt"] as string,
                Iterations = Convert.ToInt32(row["Iterations"])
            };

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.FindUser("librarian"))
                  .Returns(migrated);

            var userService = Mocker.Resolve<UserService>();

            userService.FindUser("librarian", "wrong").Should().BeNull();
            userService.FindUser("librarian", "a password").Should().NotBeNull();

            Mocker.GetMock<IUserRepository>()
                  .Verify(r => r.Update(It.Is<User>(u => u.Salt != null && u.Iterations == UserService.ITERATIONS)), Times.Once());
        }
    }
}
