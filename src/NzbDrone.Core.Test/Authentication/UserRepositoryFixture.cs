using System;
using FluentAssertions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.Authentication
{
    // UserService against a real Users table, so the Salt/Iterations mapping
    // is exercised on whichever engine the suite is running against.
    [TestFixture]
    public class UserRepositoryFixture : DbTest<UserRepository, User>
    {
        private UserService _userService;

        [SetUp]
        public void Setup()
        {
            _userService = new UserService(Subject, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_round_trip_salt_and_iterations()
        {
            _userService.Add("librarian", "a password");

            var stored = Subject.FindUser("librarian");

            stored.Salt.Should().NotBeNullOrWhiteSpace();
            stored.Iterations.Should().Be(UserService.ITERATIONS);

            _userService.FindUser("librarian", "a password").Should().NotBeNull();
            _userService.FindUser("librarian", "wrong").Should().BeNull();
        }

        [Test]
        public void should_persist_the_upgrade_of_a_legacy_user()
        {
            Subject.Insert(new User
            {
                Identifier = Guid.NewGuid(),
                Username = "librarian",
                Password = "a password".SHA256Hash()
            });

            Subject.FindUser("librarian").Salt.Should().BeNull();

            _userService.FindUser("librarian", "a password").Should().NotBeNull();

            var stored = Subject.FindUser("librarian");

            stored.Salt.Should().NotBeNullOrWhiteSpace();
            stored.Iterations.Should().Be(UserService.ITERATIONS);
            stored.Password.Should().NotBe("a password".SHA256Hash());

            _userService.FindUser("librarian", "a password").Should().NotBeNull();
        }
    }
}
