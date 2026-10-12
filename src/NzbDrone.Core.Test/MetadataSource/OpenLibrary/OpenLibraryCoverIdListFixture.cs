using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // `covers` and `photos` are lists of image ids, and a list of ids is not
    // guaranteed to hold only ids: a null slot is the case a reviewer raised.
    // Json.NET will not put a null into a List<int> -- it throws, and the
    // exception takes the whole document with it, so one bad cover slot would
    // fail an author refresh.
    //
    // None of the captured payloads contains one, so these are hand-written.
    // Deserialised through Json.Deserialize because that is what
    // HttpResponse<T> uses; the cassette loader's bare JsonConvert has
    // different settings.
    [TestFixture]
    public class OpenLibraryCoverIdListFixture
    {
        [Test]
        public void work_with_a_null_cover_should_deserialize()
        {
            var work = Json.Deserialize<OpenLibraryWorkResource>(@"{ ""key"": ""/works/OL1W"", ""title"": ""T"", ""covers"": [null, 123, -1] }");

            work.Title.Should().Be("T");
            work.Covers.Should().Equal(123, -1);
        }

        [Test]
        public void edition_with_a_null_cover_should_deserialize()
        {
            var edition = Json.Deserialize<OpenLibraryEditionResource>(@"{ ""key"": ""/books/OL1M"", ""title"": ""T"", ""covers"": [456, null] }");

            edition.Covers.Should().Equal(456);
            OpenLibraryEditionMapper.ToEdition(edition).Images.Should().NotBeEmpty();
        }

        [Test]
        public void author_with_a_null_photo_should_deserialize()
        {
            var author = Json.Deserialize<OpenLibraryAuthorResource>(@"{ ""key"": ""/authors/OL1A"", ""name"": ""N"", ""photos"": [null] }");

            author.Name.Should().Be("N");
            author.Photos.Should().BeEmpty();
        }

        [Test]
        public void works_list_should_survive_one_entry_with_a_null_cover()
        {
            var works = Json.Deserialize<OpenLibraryAuthorWorksResource>(@"{ ""size"": 2, ""entries"": [
                { ""key"": ""/works/OL1W"", ""title"": ""One"", ""covers"": [null] },
                { ""key"": ""/works/OL2W"", ""title"": ""Two"", ""covers"": [7] } ] }");

            works.Entries.Should().HaveCount(2);
        }

        // Values that are not ids at all are dropped the same way rather than
        // failing the document: the id list is decoration, the record is not.
        [Test]
        public void junk_in_the_list_should_be_skipped()
        {
            var work = Json.Deserialize<OpenLibraryWorkResource>(@"{ ""key"": ""/works/OL1W"", ""covers"": [""abc"", 12, {}, ""45""] }");

            work.Covers.Should().Equal(12, 45);
        }

        [Test]
        public void absent_and_null_lists_should_stay_null()
        {
            Json.Deserialize<OpenLibraryWorkResource>(@"{ ""key"": ""/works/OL1W"" }").Covers.Should().BeNull();
            Json.Deserialize<OpenLibraryWorkResource>(@"{ ""key"": ""/works/OL1W"", ""covers"": null }").Covers.Should().BeNull();
        }
    }
}
