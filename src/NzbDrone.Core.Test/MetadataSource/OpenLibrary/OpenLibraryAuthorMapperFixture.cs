using System.Collections.Generic;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    [TestFixture]
    public class OpenLibraryAuthorMapperFixture
    {
        private static OpenLibraryAuthorResource Author()
        {
            return new OpenLibraryAuthorResource
            {
                Key = "/authors/OL26320A",
                Name = "J. R. R. Tolkien"
            };
        }

        private static OpenLibraryWorkResource Work(string key, string title)
        {
            return new OpenLibraryWorkResource { Key = key, Title = title };
        }

        // WorkCount used to be set only by the search mapper, so an author
        // fetched by id came back with 0 -- which the Library Import wizard
        // renders as "No works", labelling the right author as an empty stub.
        [Test]
        public void should_take_the_work_count_from_the_works_payload()
        {
            var works = new OpenLibraryAuthorWorksResource
            {
                Size = 342,
                Entries = new List<OpenLibraryWorkResource>
                {
                    Work("/works/OL27448W", "The Lord of the Rings")
                }
            };

            var author = OpenLibraryAuthorMapper.ToAuthor(Author(), works);

            author.Metadata.Value.WorkCount.Should().Be(342);
        }

        // `size` is the author's total, not the length of this page. OL caps
        // the entries at the requested limit but keeps reporting the full
        // count, and it is the full count a user needs to tell a real author
        // from a stub.
        [Test]
        public void should_prefer_the_reported_size_over_the_entry_count()
        {
            var works = new OpenLibraryAuthorWorksResource
            {
                Size = 342,
                Entries = new List<OpenLibraryWorkResource>
                {
                    Work("/works/OL27448W", "The Lord of the Rings"),
                    Work("/works/OL27479W", "The Hobbit")
                }
            };

            var author = OpenLibraryAuthorMapper.ToAuthor(Author(), works);

            author.Metadata.Value.WorkCount.Should().Be(342);
            author.Books.Value.Should().HaveCount(2);
        }

        [Test]
        public void should_survive_a_missing_works_payload()
        {
            var author = OpenLibraryAuthorMapper.ToAuthor(Author(), null);

            author.Metadata.Value.WorkCount.Should().Be(0);
            author.Books.Value.Should().BeEmpty();
        }

        // A merged or deleted author is served as a document with a key and a
        // type and nothing else. The proxy resolves those before they get here,
        // but a mapper that throws ArgumentNullException on a missing name
        // takes a whole author refresh down with it if one ever slips through.
        [Test]
        public void should_not_throw_on_a_document_with_no_name()
        {
            var nameless = new OpenLibraryAuthorResource { Key = "/authors/OL26320A" };

            var author = OpenLibraryAuthorMapper.ToAuthor(nameless, null);

            author.Metadata.Value.ForeignAuthorId.Should().Be("OL26320A");
            author.Metadata.Value.Name.Should().BeEmpty();
            author.CleanName.Should().BeEmpty();
        }

        // TopWork is OL's search-index notion and the works list is not
        // ordered by it, so guessing from the first entry would dress a
        // guess as a fact. describeMatch() in the wizard already renders
        // the count alone when it is absent.
        [Test]
        public void should_not_invent_a_top_work()
        {
            var works = new OpenLibraryAuthorWorksResource
            {
                Size = 2,
                Entries = new List<OpenLibraryWorkResource>
                {
                    Work("/works/OL27448W", "The Lord of the Rings")
                }
            };

            var author = OpenLibraryAuthorMapper.ToAuthor(Author(), works);

            author.Metadata.Value.TopWork.Should().BeNull();
        }
    }
}
