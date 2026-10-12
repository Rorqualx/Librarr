using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.Test.MetadataSource.OpenLibrary
{
    // The low-confidence mapping page tells people to paste "/works/OLxxxxW".
    // Whatever they paste is stored and then matched against ^OL\d+W$ by
    // MetadataSourceFactory, so anything that is not reduced to the bare id
    // here becomes a mapping that silently translates to nothing.
    [TestFixture]
    public class OpenLibraryIdNormalizationFixture
    {
        [TestCase("OL45804W")]
        [TestCase("ol45804w")]
        [TestCase("  OL45804W\t")]
        [TestCase("/works/OL45804W")]
        [TestCase("works/OL45804W")]
        [TestCase("/works/ol45804w/")]
        [TestCase("/works/OL45804W.json")]
        [TestCase("https://openlibrary.org/works/OL45804W")]
        [TestCase("https://openlibrary.org/works/OL45804W/Fantastic_Mr_Fox?edition=key%3A/books/OL1M#reader")]
        [TestCase("http://www.openlibrary.org/works/OL45804W/")]
        [TestCase("openlibrary.org/works/OL45804W")]
        [TestCase(" HTTPS://OpenLibrary.org/Works/ol45804w ")]
        public void should_normalize_a_work_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeWorkId(input, out var id).Should().BeTrue();
            id.Should().Be("OL45804W");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("45804")]
        [TestCase("OL45804")]
        [TestCase("OLW")]
        [TestCase("OL45804A")]
        [TestCase("OL45804M")]
        [TestCase("/authors/OL45804W")]
        [TestCase("/works/OL45804A")]
        [TestCase("https://openlibrary.org/authors/OL45804A/Roald_Dahl")]
        [TestCase("https://example.com/works/OL45804W")]
        [TestCase("https://openlibrary.org.example.com/works/OL45804W")]
        [TestCase("OL45804W OL1W")]
        [TestCase("xOL45804W")]
        [TestCase("OL45804Wx")]
        [TestCase("Fantastic Mr Fox")]
        public void should_reject_anything_that_is_not_a_work_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeWorkId(input, out var id).Should().BeFalse();
            id.Should().BeNull();
        }

        [TestCase("OL34184A")]
        [TestCase(" ol34184a ")]
        [TestCase("/authors/OL34184A")]
        [TestCase("https://openlibrary.org/authors/OL34184A/Roald_Dahl")]
        public void should_normalize_an_author_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeAuthorId(input, out var id).Should().BeTrue();
            id.Should().Be("OL34184A");
        }

        [TestCase("OL45804W")]
        [TestCase("/works/OL34184A")]
        [TestCase("https://openlibrary.org/works/OL45804W")]
        [TestCase("Roald Dahl")]
        [TestCase("")]
        public void should_reject_anything_that_is_not_an_author_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeAuthorId(input, out var id).Should().BeFalse();
            id.Should().BeNull();
        }

        [TestCase("OL7353617M")]
        [TestCase("/books/ol7353617m")]
        [TestCase("https://openlibrary.org/books/OL7353617M/Fantastic_Mr._Fox")]
        public void should_normalize_an_edition_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeEditionId(input, out var id).Should().BeTrue();
            id.Should().Be("OL7353617M");
        }

        [TestCase("OL45804W")]
        [TestCase("/works/OL7353617M")]
        [TestCase("9780142410349")]
        public void should_reject_anything_that_is_not_an_edition_id(string input)
        {
            OpenLibraryIdHelper.TryNormalizeEditionId(input, out var id).Should().BeFalse();
            id.Should().BeNull();
        }
    }
}
