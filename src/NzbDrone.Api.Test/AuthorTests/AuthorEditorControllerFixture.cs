using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using FluentValidation;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Author;

namespace NzbDrone.Api.Test.AuthorTests
{
    [TestFixture]
    public class AuthorEditorControllerFixture : TestBase<AuthorEditorController>
    {
        private List<Author> _authors;

        [SetUp]
        public void Setup()
        {
            _authors = new List<Author>
            {
                new Author { Id = 1, QualityProfileId = 1, AudiobookQualityProfileId = 2 },
                new Author { Id = 2, QualityProfileId = 1, AudiobookQualityProfileId = 0 }
            };

            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.GetAuthors(It.IsAny<IEnumerable<int>>()))
                  .Returns(_authors);

            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.UpdateAuthors(It.IsAny<List<Author>>(), It.IsAny<bool>()))
                  .Returns<List<Author>, bool>((authors, _) => authors);

            Mocker.GetMock<IQualityProfileService>()
                  .Setup(s => s.Exists(It.IsAny<int>()))
                  .Returns<int>(id => id == 1 || id == 2);
        }

        private AuthorEditorResource GivenResource(int? audiobookQualityProfileId)
        {
            return new AuthorEditorResource
            {
                AuthorIds = new List<int> { 1, 2 },
                AudiobookQualityProfileId = audiobookQualityProfileId
            };
        }

        [Test]
        public void should_reject_an_audiobook_profile_that_does_not_exist_and_change_nothing()
        {
            Action act = () => Subject.SaveAll(GivenResource(9999));

            act.Should().Throw<ValidationException>()
               .Which.Errors.Should().ContainSingle(e => e.PropertyName == nameof(AuthorEditorResource.AudiobookQualityProfileId));

            _authors.Select(a => a.AudiobookQualityProfileId).Should().Equal(2, 0);

            Mocker.GetMock<IAuthorService>()
                  .Verify(s => s.UpdateAuthors(It.IsAny<List<Author>>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void should_assign_an_audiobook_profile_that_exists()
        {
            Subject.SaveAll(GivenResource(1));

            _authors.Select(a => a.AudiobookQualityProfileId).Should().Equal(1, 1);
        }

        [Test]
        public void should_clear_the_audiobook_profile_on_an_explicit_zero()
        {
            Subject.SaveAll(GivenResource(0));

            _authors.Select(a => a.AudiobookQualityProfileId).Should().Equal(0, 0);
        }

        [Test]
        public void should_leave_the_audiobook_profile_alone_when_it_is_omitted()
        {
            Subject.SaveAll(GivenResource(null));

            _authors.Select(a => a.AudiobookQualityProfileId).Should().Equal(2, 0);

            Mocker.GetMock<IQualityProfileService>().Verify(s => s.Exists(It.IsAny<int>()), Times.Never());
        }
    }
}
