using System.Collections.Generic;
using System.Linq;
using FizzWare.NBuilder;
using FluentAssertions;
using FluentValidation.Results;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Test.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.MusicTests
{
    // The bulk path behind the Library Import wizard and import list sync. One
    // row must never be able to take the rest of the batch down with it.
    [TestFixture]
    public class AddAuthorsFixture : CoreTest<AddAuthorService>
    {
        [SetUp]
        public void Setup()
        {
            Mocker.GetMock<IProvideAuthorInfo>()
                  .Setup(s => s.GetAuthorInfo(It.IsAny<string>(), false))
                  .Returns<string, bool>((id, _) => GivenMetadataAuthor(id));

            Mocker.GetMock<IAddAuthorValidator>()
                  .Setup(s => s.Validate(It.IsAny<Author>()))
                  .Returns(new ValidationResult());

            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.AddAuthors(It.IsAny<List<Author>>(), It.IsAny<bool>()))
                  .Returns<List<Author>, bool>((authors, _) => authors);
        }

        private static Author GivenMetadataAuthor(string foreignAuthorId)
        {
            var author = Builder<Author>.CreateNew().With(s => s.Path = null).Build();
            author.Metadata = Builder<AuthorMetadata>.CreateNew()
                .With(m => m.ForeignAuthorId = foreignAuthorId)
                .With(m => m.Name = "Name " + foreignAuthorId)
                .Build();
            author.Books = new List<Book>();

            return author;
        }

        private static Author GivenRequest(string foreignAuthorId, string path)
        {
            return new Author
            {
                ForeignAuthorId = foreignAuthorId,
                Path = path
            };
        }

        private void GivenExistingAuthor(string foreignAuthorId)
        {
            Mocker.GetMock<IAuthorService>()
                  .Setup(s => s.FindById(foreignAuthorId))
                  .Returns(GivenMetadataAuthor(foreignAuthorId));
        }

        [Test]
        public void should_add_an_author_only_once_when_two_entries_resolve_to_it()
        {
            var added = Subject.AddAuthors(new List<Author>
            {
                GivenRequest("OL1A", @"C:\Test\Harper Lee".AsOsAgnostic()),
                GivenRequest("OL1A", @"C:\Test\Lee, Harper".AsOsAgnostic()),
                GivenRequest("OL2A", @"C:\Test\Other".AsOsAgnostic())
            });

            added.Select(x => x.ForeignAuthorId).Should().Equal("OL1A", "OL2A");
            added.First().Path.Should().Be(@"C:\Test\Harper Lee".AsOsAgnostic());

            Mocker.GetMock<IAuthorMetadataService>()
                  .Verify(v => v.UpsertMany(It.Is<List<AuthorMetadata>>(l => l.Count == 2)), Times.Once());
        }

        [Test]
        public void should_skip_an_author_already_in_the_library_and_add_the_rest()
        {
            GivenExistingAuthor("OL1A");

            var added = Subject.AddAuthors(new List<Author>
            {
                GivenRequest("OL1A", @"C:\Test\Existing".AsOsAgnostic()),
                GivenRequest("OL2A", @"C:\Test\New".AsOsAgnostic())
            });

            added.Select(x => x.ForeignAuthorId).Should().Equal("OL2A");
        }

        [Test]
        public void should_not_fetch_metadata_for_entries_it_skips()
        {
            GivenExistingAuthor("OL1A");

            Subject.AddAuthors(new List<Author>
            {
                GivenRequest("OL1A", @"C:\Test\Existing".AsOsAgnostic()),
                GivenRequest("OL2A", @"C:\Test\New".AsOsAgnostic()),
                GivenRequest("OL2A", @"C:\Test\New Again".AsOsAgnostic())
            });

            Mocker.GetMock<IProvideAuthorInfo>()
                  .Verify(v => v.GetAuthorInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Once());
        }

        [Test]
        public void should_report_why_each_entry_was_skipped()
        {
            GivenExistingAuthor("OL1A");

            Mocker.GetMock<IProvideAuthorInfo>()
                  .Setup(s => s.GetAuthorInfo("OL3A", false))
                  .Throws(new AuthorNotFoundException("OL3A"));

            var existing = GivenRequest("OL1A", @"C:\Test\Existing".AsOsAgnostic());
            var duplicate = GivenRequest("OL2A", @"C:\Test\New Again".AsOsAgnostic());
            var missing = GivenRequest("OL3A", @"C:\Test\Missing".AsOsAgnostic());

            var result = Subject.ImportAuthors(new List<Author>
            {
                existing,
                GivenRequest("OL2A", @"C:\Test\New".AsOsAgnostic()),
                duplicate,
                missing
            });

            result.Added.Select(x => x.ForeignAuthorId).Should().Equal("OL2A");

            result.Skipped.Should().HaveCount(3);
            result.Skipped.Single(x => x.Author == existing).Reason.Should().Be(AddAuthorSkipReason.Exists);
            result.Skipped.Single(x => x.Author == existing).Message.Should().Be("This author has already been added");
            result.Skipped.Single(x => x.Author == duplicate).Reason.Should().Be(AddAuthorSkipReason.Duplicate);
            result.Skipped.Single(x => x.Author == missing).Reason.Should().Be(AddAuthorSkipReason.Failed);
            result.Skipped.Single(x => x.Author == missing).Message.Should().Be("An author with this ID was not found");

            // AddSkyhookData logs the not-found, the batch loop logs the failure.
            ExceptionVerification.ExpectedErrors(2);
        }

        [Test]
        public void should_skip_an_entry_the_metadata_source_resolves_to_an_existing_author()
        {
            GivenExistingAuthor("OL9A");

            Mocker.GetMock<IProvideAuthorInfo>()
                  .Setup(s => s.GetAuthorInfo("OL1A", false))
                  .Returns(GivenMetadataAuthor("OL9A"));

            var result = Subject.ImportAuthors(new List<Author>
            {
                GivenRequest("OL1A", @"C:\Test\Redirected".AsOsAgnostic())
            });

            result.Added.Should().BeEmpty();
            result.Skipped.Single().Reason.Should().Be(AddAuthorSkipReason.Exists);
        }

        [Test]
        public void should_still_add_a_later_entry_when_an_earlier_one_for_the_same_author_failed()
        {
            var badPath = @"C:\Test\Bad".AsOsAgnostic();

            Mocker.GetMock<IAddAuthorValidator>()
                  .Setup(s => s.Validate(It.Is<Author>(a => a.Path == badPath)))
                  .Returns(new ValidationResult(new List<ValidationFailure>
                                                {
                                                    new ValidationFailure("Path", "Test validation failure")
                                                }));

            var result = Subject.ImportAuthors(new List<Author>
            {
                GivenRequest("OL1A", badPath),
                GivenRequest("OL1A", @"C:\Test\Good".AsOsAgnostic())
            });

            result.Added.Should().ContainSingle().Which.Path.Should().Be(@"C:\Test\Good".AsOsAgnostic());
            result.Skipped.Single().Message.Should().Be("Test validation failure");

            ExceptionVerification.ExpectedErrors(1);
        }

        [Test]
        public void should_skip_an_entry_without_a_foreign_author_id()
        {
            var result = Subject.ImportAuthors(new List<Author>
            {
                GivenRequest(null, @"C:\Test\Nameless".AsOsAgnostic())
            });

            result.Added.Should().BeEmpty();
            result.Skipped.Single().Reason.Should().Be(AddAuthorSkipReason.Failed);

            Mocker.GetMock<IProvideAuthorInfo>()
                  .Verify(v => v.GetAuthorInfo(It.IsAny<string>(), It.IsAny<bool>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }
    }
}
