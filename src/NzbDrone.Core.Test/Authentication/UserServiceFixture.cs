using System;
using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.Authentication
{
    [TestFixture]
    public class UserServiceFixture : CoreTest<UserService>
    {
        private const string Username = "librarian";
        private const string Password = "correct horse battery staple";

        private User _stored;

        [SetUp]
        public void Setup()
        {
            _stored = null;

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.Insert(It.IsAny<User>()))
                  .Returns<User>(u => _stored = u);

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.Update(It.IsAny<User>()))
                  .Returns<User>(u => _stored = u);

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.FindUser(Username))
                  .Returns(() => _stored);

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.SingleOrDefault())
                  .Returns(() => _stored);
        }

        private void GivenLegacyUser()
        {
            _stored = new User
            {
                Id = 1,
                Identifier = Guid.NewGuid(),
                Username = Username,
                Password = Password.SHA256Hash()
            };
        }

        [Test]
        public void should_store_new_password_salted_and_iterated()
        {
            Subject.Add(Username, Password);

            _stored.Salt.Should().NotBeNullOrWhiteSpace();
            Convert.FromBase64String(_stored.Salt).Should().HaveCount(16);
            _stored.Iterations.Should().Be(UserService.ITERATIONS);
            _stored.Password.Should().NotBe(Password);
            _stored.Password.Should().NotBe(Password.SHA256Hash());
        }

        [Test]
        public void should_use_a_different_salt_for_every_hash()
        {
            Subject.Add(Username, Password);
            var first = _stored;

            Subject.Add(Username, Password);

            _stored.Salt.Should().NotBe(first.Salt);
            _stored.Password.Should().NotBe(first.Password);
        }

        [Test]
        public void should_find_user_with_correct_password()
        {
            Subject.Add(Username, Password);

            Subject.FindUser(Username, Password).Should().BeSameAs(_stored);
        }

        [Test]
        public void should_find_user_regardless_of_username_case()
        {
            Subject.Add(Username, Password);

            Subject.FindUser("Librarian", Password).Should().NotBeNull();
        }

        [Test]
        public void should_not_find_user_with_wrong_password()
        {
            Subject.Add(Username, Password);

            Subject.FindUser(Username, "wrong").Should().BeNull();
        }

        [Test]
        public void should_not_accept_the_stored_hash_as_the_password()
        {
            Subject.Add(Username, Password);

            Subject.FindUser(Username, _stored.Password).Should().BeNull();
        }

        [Test]
        public void should_not_find_unknown_user()
        {
            Subject.FindUser("nobody", Password).Should().BeNull();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void should_not_find_user_with_empty_password(string password)
        {
            Subject.Add(Username, Password);

            Subject.FindUser(Username, password).Should().BeNull();
        }

        [Test]
        public void should_not_rewrite_an_up_to_date_hash_on_login()
        {
            Subject.Add(Username, Password);

            Subject.FindUser(Username, Password);

            Mocker.GetMock<IUserRepository>().Verify(r => r.Update(It.IsAny<User>()), Times.Never());
        }

        [Test]
        public void should_log_in_legacy_sha256_user_and_upgrade_the_hash()
        {
            GivenLegacyUser();
            var identifier = _stored.Identifier;

            var user = Subject.FindUser(Username, Password);

            user.Should().NotBeNull();
            user.Identifier.Should().Be(identifier);

            _stored.Salt.Should().NotBeNullOrWhiteSpace();
            _stored.Iterations.Should().Be(UserService.ITERATIONS);
            _stored.Password.Should().NotBe(Password.SHA256Hash());

            Mocker.GetMock<IUserRepository>().Verify(r => r.Update(It.IsAny<User>()), Times.Once());
        }

        [Test]
        public void should_still_log_in_after_legacy_hash_was_upgraded()
        {
            GivenLegacyUser();

            Subject.FindUser(Username, Password);

            Subject.FindUser(Username, Password).Should().NotBeNull();
            Subject.FindUser(Username, "wrong").Should().BeNull();

            Mocker.GetMock<IUserRepository>().Verify(r => r.Update(It.IsAny<User>()), Times.Once());
        }

        [Test]
        public void should_not_upgrade_legacy_user_with_wrong_password()
        {
            GivenLegacyUser();

            Subject.FindUser(Username, "wrong").Should().BeNull();

            _stored.Salt.Should().BeNull();
            _stored.Iterations.Should().Be(0);
            _stored.Password.Should().Be(Password.SHA256Hash());

            Mocker.GetMock<IUserRepository>().Verify(r => r.Update(It.IsAny<User>()), Times.Never());
        }

        [Test]
        public void should_not_accept_legacy_hash_as_the_password()
        {
            GivenLegacyUser();

            Subject.FindUser(Username, Password.SHA256Hash()).Should().BeNull();
        }

        [Test]
        public void should_still_log_in_legacy_user_when_the_upgrade_cannot_be_saved()
        {
            GivenLegacyUser();

            Mocker.GetMock<IUserRepository>()
                  .Setup(r => r.Update(It.IsAny<User>()))
                  .Throws(new InvalidOperationException("database is read-only"));

            var user = Subject.FindUser(Username, Password);

            user.Should().NotBeNull();
            user.Salt.Should().BeNull();
            user.Password.Should().Be(Password.SHA256Hash());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void should_rehash_on_login_when_stored_iteration_count_is_lower()
        {
            Subject.Add(Username, Password);

            // Re-derive the same password at a weaker cost, as a hash written
            // by a build with a lower ITERATIONS would look.
            var salt = Convert.FromBase64String(_stored.Salt);
            _stored.Iterations = 1000;
            _stored.Password = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(Password, salt, 1000, HashAlgorithmName.SHA512, 32));

            Subject.FindUser(Username, Password).Should().NotBeNull();

            _stored.Iterations.Should().Be(UserService.ITERATIONS);
            Subject.FindUser(Username, Password).Should().NotBeNull();
        }

        [Test]
        public void upsert_should_add_user_when_none_exists()
        {
            Subject.Upsert("Librarian", Password);

            _stored.Username.Should().Be(Username);
            Subject.FindUser(Username, Password).Should().NotBeNull();
        }

        [Test]
        public void upsert_should_change_password_when_one_is_given()
        {
            Subject.Add(Username, Password);
            var oldHash = _stored.Password;

            Subject.Upsert(Username, "a new password");

            _stored.Password.Should().NotBe(oldHash);
            Subject.FindUser(Username, "a new password").Should().NotBeNull();
            Subject.FindUser(Username, Password).Should().BeNull();
        }

        [TestCase(null)]
        [TestCase("")]
        public void upsert_should_keep_password_and_update_username_when_no_password_is_given(string password)
        {
            Subject.Add(Username, Password);
            var oldHash = _stored.Password;
            var oldSalt = _stored.Salt;

            Subject.Upsert("Renamed", password);

            _stored.Username.Should().Be("renamed");
            _stored.Password.Should().Be(oldHash);
            _stored.Salt.Should().Be(oldSalt);
        }

        [Test]
        public void upsert_should_keep_legacy_hash_when_no_password_is_given()
        {
            GivenLegacyUser();

            Subject.Upsert(Username, null);

            _stored.Salt.Should().BeNull();
            _stored.Password.Should().Be(Password.SHA256Hash());
        }

        [Test]
        public void upsert_should_not_create_a_user_without_a_password()
        {
            Subject.Upsert(Username, null).Should().BeNull();

            Mocker.GetMock<IUserRepository>().Verify(r => r.Insert(It.IsAny<User>()), Times.Never());
        }
    }
}
