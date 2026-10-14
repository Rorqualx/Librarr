using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.ValidationTests
{
    // Our property validators keep the FluentValidation 9 shape and are
    // adapted to FluentValidation 11 where they are attached to a rule
    // (NzbDrone.Core.Validation.PropertyValidator). These pin down what the
    // API puts in a 400 for each pattern they use, so the adapter has to
    // reproduce 9.x exactly: property name, message, placeholder arguments,
    // error code, the owning object and warning state.
    [TestFixture]
    public class PropertyValidatorFixture : CoreTest
    {
        public class Model
        {
            public int Id { get; set; }
            public string Path { get; set; }
            public int? DownloadClientId { get; set; }
            public int QualityProfileId { get; set; }
            public int AudiobookQualityProfileId { get; set; }
        }

        private static string SystemFolder => OsInfo.IsOsx ? "/System" : "/usr/bin";

        [Test]
        public void should_fill_message_placeholder_from_appended_argument()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists("/nope")).Returns(false);

            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.Path).SetValidator(Mocker.Resolve<PathExistsValidator>())
            };

            var failure = validator.Validate(new Model { Path = "/nope" }).Errors.Single();

            failure.PropertyName.Should().Be("Path");
            failure.ErrorMessage.Should().Be("Path '/nope' does not exist");
            failure.ErrorCode.Should().Be("PathExistsValidator");
            failure.AttemptedValue.Should().Be("/nope");
            failure.FormattedMessagePlaceholderValues["path"].Should().Be("/nope");
        }

        [Test]
        public void should_use_last_value_of_an_argument_appended_more_than_once()
        {
            PosixOnly();

            // Appends "systemFolder" once per system folder until one matches;
            // on Linux the match is the last of six.
            var path = SystemFolder + "/child";

            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.Path).SetValidator(new SystemFolderValidator())
            };

            var failure = validator.Validate(new Model { Path = path }).Errors.Single();

            failure.ErrorMessage.Should().Be($"Path '{path}' is child of system folder {SystemFolder}");
            failure.ErrorCode.Should().Be("SystemFolderValidator");
        }

        [Test]
        public void should_fill_custom_message_from_appended_argument()
        {
            PosixOnly();

            // As CustomScriptSettings does.
            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.Path).SetValidator(new SystemFolderValidator()).WithMessage("Must not be a descendant of '{systemFolder}'")
            };

            var failure = validator.Validate(new Model { Path = SystemFolder }).Errors.Single();

            failure.ErrorMessage.Should().Be($"Must not be a descendant of '{SystemFolder}'");
        }

        [Test]
        public void should_see_the_object_that_owns_the_property()
        {
            Mocker.GetMock<IAuthorService>().Setup(s => s.AllAuthorPaths()).Returns(new Dictionary<int, string> { { 1, "/books/a" } });

            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.Path).SetValidator(Mocker.Resolve<AuthorPathValidator>())
            };

            validator.Validate(new Model { Id = 1, Path = "/books/a" }).IsValid.Should().BeTrue();

            var failure = validator.Validate(new Model { Id = 2, Path = "/books/a" }).Errors.Single();

            failure.ErrorMessage.Should().Be("Path '/books/a' is already configured for another author");
        }

        [Test]
        public void should_keep_warning_state()
        {
            Mocker.GetMock<IDiskProvider>().Setup(s => s.FolderExists("/nope")).Returns(false);

            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.Path).SetValidator(Mocker.Resolve<PathExistsValidator>()).AsWarning()
            };

            var result = new NzbDroneValidationResult(validator.Validate(new Model { Path = "/nope" }));

            result.Errors.Should().BeEmpty();
            result.Warnings.Single().IsWarning.Should().BeTrue();
            result.Warnings.Single().ErrorMessage.Should().Be("Path '/nope' does not exist");
        }

        [Test]
        public void should_pass_null_through_to_the_validator()
        {
            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.DownloadClientId).SetValidator(Mocker.Resolve<DownloadClientExistsValidator>())
            };

            validator.Validate(new Model { DownloadClientId = null }).IsValid.Should().BeTrue();

            Mocker.GetMock<IDownloadClientFactory>().Setup(s => s.Exists(5)).Returns(false);

            validator.Validate(new Model { DownloadClientId = 5 }).Errors.Single().ErrorMessage.Should().Be("Download Client does not exist");
        }

        // The one difference from 9.x. There, When() stored its condition on
        // the validator instance, so a validator shared by two rules took the
        // condition to both. AuthorController and RootFolderController share
        // one QualityProfileExistsValidator between the quality profile and the
        // audiobook quality profile, and only the latter is conditional (> 0):
        // the quality profile was only checked when an audiobook profile was
        // set, and a root folder could be saved with a profile that does not
        // exist. FluentValidation 11 keeps the condition on the rule.
        [Test]
        public void should_not_apply_a_condition_to_the_same_validator_on_another_rule()
        {
            Mocker.GetMock<IQualityProfileService>().Setup(s => s.Exists(It.IsAny<int>())).Returns(false);

            var exists = Mocker.Resolve<QualityProfileExistsValidator>();

            var validator = new TestValidator<Model>
            {
                v => v.RuleFor(s => s.QualityProfileId).SetValidator(exists),
                v => v.RuleFor(s => s.AudiobookQualityProfileId).SetValidator(exists).When(s => s.AudiobookQualityProfileId > 0)
            };

            var failure = validator.Validate(new Model { QualityProfileId = 999, AudiobookQualityProfileId = 0 }).Errors.Single();

            failure.PropertyName.Should().Be("QualityProfileId");
            failure.ErrorMessage.Should().Be("Quality Profile does not exist");
        }
    }
}
