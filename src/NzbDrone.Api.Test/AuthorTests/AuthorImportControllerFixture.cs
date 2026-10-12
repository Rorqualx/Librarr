using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Test.Common;
using Readarr.Api.V1.Author;
using Readarr.Http.REST;

namespace NzbDrone.Api.Test.AuthorTests
{
    [TestFixture]
    public class AuthorImportControllerFixture : TestBase<AuthorImportController>
    {
        private static NzbDrone.Core.Books.Author GivenAuthor(string foreignAuthorId, string path)
        {
            return new NzbDrone.Core.Books.Author
            {
                Path = path,
                Metadata = new AuthorMetadata
                {
                    ForeignAuthorId = foreignAuthorId,
                    Name = "Name " + foreignAuthorId
                }
            };
        }

        // An empty body binds to null; that used to be a NullReferenceException.
        [Test]
        public void Import_should_reject_a_missing_body()
        {
            Assert.Throws<BadRequestException>(() => Subject.Import(null));

            Mocker.GetMock<IAddAuthorService>()
                  .Verify(v => v.ImportAuthors(It.IsAny<List<NzbDrone.Core.Books.Author>>(), It.IsAny<bool>()), Times.Never());
        }

        [Test]
        public void Import_should_report_imported_and_skipped_rows_by_path()
        {
            Mocker.GetMock<IAddAuthorService>()
                  .Setup(v => v.ImportAuthors(It.IsAny<List<NzbDrone.Core.Books.Author>>(), It.IsAny<bool>()))
                  .Returns(new AddAuthorsResult
                  {
                      Added = new List<NzbDrone.Core.Books.Author> { GivenAuthor("OL1A", "/books/Harper Lee") },
                      Skipped = new List<SkippedAuthor>
                      {
                          new SkippedAuthor { Author = GivenAuthor("OL1A", "/books/Lee, Harper"), Reason = AddAuthorSkipReason.Duplicate, Message = "dup" },
                          new SkippedAuthor { Author = GivenAuthor("OL2A", "/books/Existing"), Reason = AddAuthorSkipReason.Exists, Message = "exists" },
                          new SkippedAuthor { Author = GivenAuthor("OL3A", "/books/Broken"), Reason = AddAuthorSkipReason.Failed, Message = "boom" }
                      }
                  });

            var result = Subject.Import(new List<AuthorResource> { new AuthorResource(), null });

            result.Imported.Select(x => x.Path).Should().Equal("/books/Harper Lee");
            result.Skipped.Select(x => x.Path).Should().Equal("/books/Lee, Harper", "/books/Existing", "/books/Broken");
            result.Skipped.Select(x => x.Reason).Should().Equal("duplicate", "exists", "failed");
            result.Skipped.Select(x => x.Message).Should().Equal("dup", "exists", "boom");
            result.Skipped.First().ForeignAuthorId.Should().Be("OL1A");

            // The null entry is dropped rather than passed on to blow up later.
            Mocker.GetMock<IAddAuthorService>()
                  .Verify(v => v.ImportAuthors(It.Is<List<NzbDrone.Core.Books.Author>>(l => l.Count == 1), It.IsAny<bool>()), Times.Once());
        }
    }
}
