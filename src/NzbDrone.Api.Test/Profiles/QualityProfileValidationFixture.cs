using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation.Results;
using NUnit.Framework;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Profiles.Quality;

namespace NzbDrone.Api.Test.Profiles
{
    // Issue #97: with no custom formats, a positive minimum score threw
    // "Sequence contains no elements" (HTTP 500) instead of failing validation.
    [TestFixture]
    public class QualityProfileValidationFixture : TestBase
    {
        private const string NeverSatisfied = "Minimum Custom Format Score can never be satisfied";

        private TestableQualityProfileController _controller;

        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<ICustomFormatService>()
                  .Setup(s => s.All())
                  .Returns(new List<CustomFormat>());

            _controller = Mocker.Resolve<TestableQualityProfileController>();
        }

        private static QualityProfileResource GivenProfile(int minFormatScore, params int[] scores)
        {
            return new QualityProfileResource
            {
                Name = "Test",
                Items = new List<QualityProfileQualityItemResource>(),
                MinFormatScore = minFormatScore,
                FormatItems = scores.Select((score, i) => new ProfileFormatItemResource { Format = i + 1, Score = score }).ToList()
            };
        }

        private List<string> Errors(QualityProfileResource resource)
        {
            return _controller.ValidateShared(resource).Errors.Select(e => e.ErrorMessage).ToList();
        }

        [Test]
        public void should_fail_validation_not_throw_when_there_are_no_custom_formats()
        {
            Errors(GivenProfile(10)).Should().Contain(NeverSatisfied);
        }

        [Test]
        public void should_accept_zero_minimum_score_when_there_are_no_custom_formats()
        {
            Errors(GivenProfile(0)).Should().NotContain(NeverSatisfied);
        }

        [Test]
        public void should_fail_validation_when_scores_cannot_reach_minimum()
        {
            Errors(GivenProfile(10, 3, 4, -2)).Should().Contain(NeverSatisfied);
        }

        [Test]
        public void should_accept_minimum_reachable_by_positive_scores()
        {
            Errors(GivenProfile(10, 6, 5, -2)).Should().NotContain(NeverSatisfied);
        }

        public class TestableQualityProfileController : QualityProfileController
        {
            public TestableQualityProfileController(IQualityProfileService qualityProfileService, ICustomFormatService formatService)
                : base(qualityProfileService, formatService)
            {
            }

            public ValidationResult ValidateShared(QualityProfileResource resource)
            {
                return SharedValidator.Validate(resource);
            }
        }
    }
}
