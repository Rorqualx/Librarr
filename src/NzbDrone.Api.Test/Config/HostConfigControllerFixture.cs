using System.Linq;
using FluentAssertions;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Config;

namespace NzbDrone.Api.Test.Config
{
    [TestFixture]
    public class HostConfigControllerFixture : TestBase<HostConfigControllerFixture.TestableHostConfigController>
    {
        private const string StoredHash = "kq3Zc0mUu1eTqkYc1m3J8h2q1bq0mX0kq3Zc0mUu1eQ=";

        private User _user;

        [SetUp]
        public void Setup()
        {
            _user = new User
            {
                Id = 1,
                Username = "librarian",
                Password = StoredHash,
                Salt = "c2FsdHNhbHRzYWx0c2FsdA==",
                Iterations = UserService.ITERATIONS
            };

            Mocker.GetMock<IUserService>()
                  .Setup(s => s.FindUser())
                  .Returns(() => _user);
        }

        private void GivenNoUser()
        {
            _user = null;
        }

        private static HostConfigResource GivenResource(string password, string passwordConfirmation)
        {
            return new HostConfigResource
            {
                Id = 1,
                Port = 8787,
                Branch = "develop",
                BackupInterval = 7,
                BackupRetention = 28,
                AuthenticationMethod = AuthenticationType.Forms,
                Username = "librarian",
                Password = password,
                PasswordConfirmation = passwordConfirmation
            };
        }

        // Only the credential rules are under test; the rest of the resource
        // (bind address, SSL, backup paths) has its own unrelated rules.
        private string[] PasswordErrors(HostConfigResource resource)
        {
            return Subject.Validate(resource)
                          .Errors
                          .Select(e => e.PropertyName)
                          .Where(p => p == "Username" || p == "Password" || p == "PasswordConfirmation")
                          .ToArray();
        }

        [Test]
        public void get_should_not_return_the_stored_hash()
        {
            var resource = Subject.GetHostConfig();

            resource.Username.Should().Be("librarian");
            resource.Password.Should().Be(HostConfigController.PASSWORD_PLACEHOLDER);
            resource.PasswordConfirmation.Should().BeEmpty();

            resource.ToJson().Should().NotContain(StoredHash);
            resource.ToJson().Should().NotContain(_user.Salt);
        }

        [Test]
        public void get_should_return_empty_password_when_no_user_exists()
        {
            GivenNoUser();

            var resource = Subject.GetHostConfig();

            resource.Username.Should().BeEmpty();
            resource.Password.Should().BeEmpty();
        }

        [Test]
        public void save_should_not_return_the_stored_hash()
        {
            var result = Subject.SaveHostConfig(GivenResource("a new password", "a new password"));

            var returned = (HostConfigResource)((ObjectResult)result.Result).Value;

            returned.Password.Should().Be(HostConfigController.PASSWORD_PLACEHOLDER);
            returned.ToJson().Should().NotContain(StoredHash);
        }

        [Test]
        public void save_with_placeholder_should_leave_the_password_untouched()
        {
            Subject.SaveHostConfig(GivenResource(HostConfigController.PASSWORD_PLACEHOLDER, string.Empty));

            Mocker.GetMock<IUserService>().Verify(s => s.Upsert("librarian", null), Times.Once());
            Mocker.GetMock<IUserService>().Verify(s => s.Upsert(It.IsAny<string>(), It.IsNotNull<string>()), Times.Never());
        }

        [Test]
        public void save_with_new_password_should_change_it()
        {
            Subject.SaveHostConfig(GivenResource("a new password", "a new password"));

            Mocker.GetMock<IUserService>().Verify(s => s.Upsert("librarian", "a new password"), Times.Once());
        }

        [Test]
        public void save_should_treat_placeholder_typed_into_both_fields_as_a_new_password()
        {
            Subject.SaveHostConfig(GivenResource(HostConfigController.PASSWORD_PLACEHOLDER, HostConfigController.PASSWORD_PLACEHOLDER));

            Mocker.GetMock<IUserService>().Verify(s => s.Upsert("librarian", HostConfigController.PASSWORD_PLACEHOLDER), Times.Once());
        }

        [Test]
        public void should_be_valid_when_placeholder_is_sent_back_unchanged()
        {
            PasswordErrors(GivenResource(HostConfigController.PASSWORD_PLACEHOLDER, string.Empty)).Should().BeEmpty();
        }

        [Test]
        public void should_be_valid_when_new_password_is_confirmed()
        {
            PasswordErrors(GivenResource("a new password", "a new password")).Should().BeEmpty();
        }

        [Test]
        public void should_be_invalid_when_new_password_is_not_confirmed()
        {
            PasswordErrors(GivenResource("a new password", string.Empty)).Should().Equal("PasswordConfirmation");
        }

        [Test]
        public void should_be_invalid_when_the_stored_hash_is_sent_back()
        {
            PasswordErrors(GivenResource(StoredHash, string.Empty)).Should().Equal("PasswordConfirmation");
        }

        [Test]
        public void should_be_invalid_when_password_is_empty_and_auth_is_enabled()
        {
            GivenNoUser();

            PasswordErrors(GivenResource(string.Empty, string.Empty)).Should().Equal("Password");
        }

        [Test]
        public void should_be_invalid_when_placeholder_is_sent_and_no_user_exists()
        {
            GivenNoUser();

            PasswordErrors(GivenResource(HostConfigController.PASSWORD_PLACEHOLDER, string.Empty)).Should().Contain("Password");
        }

        [Test]
        public void should_not_require_password_when_auth_is_external()
        {
            GivenNoUser();

            var resource = GivenResource(string.Empty, string.Empty);
            resource.AuthenticationMethod = AuthenticationType.External;
            resource.Username = string.Empty;

            PasswordErrors(resource).Should().BeEmpty();
        }

        // SharedValidator is protected and normally only runs from the MVC
        // action filter; this exposes it so the rules can be tested directly.
        public class TestableHostConfigController : HostConfigController
        {
            public TestableHostConfigController(IConfigFileProvider configFileProvider,
                                                IConfigService configService,
                                                IUserService userService,
                                                FileExistsValidator fileExistsValidator)
                : base(configFileProvider, configService, userService, fileExistsValidator)
            {
            }

            public ValidationResult Validate(HostConfigResource resource)
            {
                return SharedValidator.Validate(resource);
            }
        }
    }
}
