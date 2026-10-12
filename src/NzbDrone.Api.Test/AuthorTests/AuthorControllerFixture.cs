using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Author;

namespace NzbDrone.Api.Test.AuthorTests
{
    [TestFixture]
    public class AuthorControllerFixture : TestBase<AuthorController>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IQualityProfileService>()
                  .Setup(s => s.Exists(It.IsAny<int>()))
                  .Returns<int>(id => id == 1 || id == 2);
        }

        // Runs the same validation the request pipeline does. The resource
        // is deliberately not valid in other respects (no path, no metadata
        // profile); only failures on the audiobook profile are looked at.
        private List<ValidationFailure> AudiobookProfileFailures(string method, AuthorResource resource)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = method;

            var descriptor = new ControllerActionDescriptor
            {
                MethodInfo = typeof(AuthorController).GetMethod(method == "PUT" ? nameof(AuthorController.UpdateAuthor) : nameof(AuthorController.AddAuthor))
            };

            var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);

            Subject.ControllerContext = new ControllerContext(actionContext);

            var executing = new ActionExecutingContext(actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object> { { "authorResource", resource } },
                Subject);

            try
            {
                Subject.OnActionExecuting(executing);
            }
            catch (ValidationException ex)
            {
                return ex.Errors.Where(e => e.PropertyName == nameof(AuthorResource.AudiobookQualityProfileId)).ToList();
            }

            return new List<ValidationFailure>();
        }

        [TestCase("PUT")]
        [TestCase("POST")]
        public void should_reject_an_audiobook_profile_that_does_not_exist(string method)
        {
            var failures = AudiobookProfileFailures(method, new AuthorResource { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 9999 });

            failures.Should().ContainSingle()
                    .Which.ErrorMessage.Should().Be("Quality Profile does not exist");
        }

        [TestCase("PUT")]
        [TestCase("POST")]
        public void should_accept_an_audiobook_profile_that_exists(string method)
        {
            AudiobookProfileFailures(method, new AuthorResource { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 2 })
                .Should().BeEmpty();
        }

        [TestCase(0)]
        [TestCase(null)]
        public void should_accept_no_audiobook_profile(int? audiobookQualityProfileId)
        {
            AudiobookProfileFailures("PUT", new AuthorResource { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = audiobookQualityProfileId })
                .Should().BeEmpty();

            Mocker.GetMock<IQualityProfileService>().Verify(s => s.Exists(0), Times.Never());
        }

        private static Author GivenStoredAuthor()
        {
            return new Author { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 2 };
        }

        // A client that predates the field round-trips the author without
        // it. That must not be read as "make this author single-format".
        [Test]
        public void should_keep_the_stored_audiobook_profile_when_the_update_omits_it()
        {
            var model = new AuthorResource { Id = 1, QualityProfileId = 1 }.ToModel(GivenStoredAuthor());

            model.AudiobookQualityProfileId.Should().Be(2);
        }

        [Test]
        public void should_clear_the_audiobook_profile_when_the_update_sends_zero()
        {
            var model = new AuthorResource { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 0 }.ToModel(GivenStoredAuthor());

            model.AudiobookQualityProfileId.Should().Be(0);
        }

        [Test]
        public void should_change_the_audiobook_profile_when_the_update_names_one()
        {
            var model = new AuthorResource { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 1 }.ToModel(GivenStoredAuthor());

            model.AudiobookQualityProfileId.Should().Be(1);
        }

        [Test]
        public void should_always_report_a_number_for_the_audiobook_profile()
        {
            new Author { Id = 1 }.ToResource().AudiobookQualityProfileId.Should().Be(0);
        }
    }
}
