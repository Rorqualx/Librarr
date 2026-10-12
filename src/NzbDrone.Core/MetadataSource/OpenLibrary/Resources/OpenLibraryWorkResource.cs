using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    public class OpenLibraryWorkResource : IOpenLibraryDocument
    {
        // OL returns keys as "/works/OL12345W". Strip the prefix at the boundary
        // before storing as ForeignBookId.
        public string Key { get; set; }

        // "/type/work", or "/type/redirect" / "/type/delete" for a work that
        // was merged away or removed. See OpenLibraryDocumentType.
        [JsonConverter(typeof(OpenLibraryKeyOrStringConverter))]
        public string Type { get; set; }

        // On a redirect document, the key of the surviving work.
        [JsonConverter(typeof(OpenLibraryKeyOrStringConverter))]
        public string Location { get; set; }

        public string Title { get; set; }

        public string Subtitle { get; set; }

        // OL's `description` is sometimes a plain string, sometimes the typed
        // form {"type":"/type/text","value":"..."}. The converter handles both.
        [JsonConverter(typeof(OpenLibraryDescriptionConverter))]
        public string Description { get; set; }

        [JsonProperty("first_publish_date")]
        public string FirstPublishDate { get; set; }

        public List<string> Subjects { get; set; }

        public List<OpenLibraryAuthorLink> Authors { get; set; }

        // Cover image IDs — render as https://covers.openlibrary.org/b/id/{id}-L.jpg
        // The converter drops anything in the array that is not an id; see it
        // for why a List<int> cannot be trusted to bind on its own.
        [JsonConverter(typeof(OpenLibraryIdListConverter))]
        public List<int> Covers { get; set; }
    }

    public class OpenLibraryAuthorLink
    {
        public OpenLibraryKey Author { get; set; }
    }

    public class OpenLibraryKey
    {
        // e.g., "/authors/OL12345A" or "/works/OL12345W"
        public string Key { get; set; }
    }

    public class OpenLibraryEditionListResource
    {
        // How many editions OL has for the work, which is routinely more than
        // the page in Entries. See OpenLibraryProxy.IsTruncated.
        public int Size { get; set; }
        public OpenLibraryListLinks Links { get; set; }
        public List<OpenLibraryEditionResource> Entries { get; set; }
    }

    // The `links` object on a list response (an author's works, a work's
    // editions):
    //   { "self": "/authors/OL34221A/works.json?limit=50",
    //     "author": "/authors/OL34221A",
    //     "next": "/authors/OL34221A/works.json?limit=50&offset=50" }
    //
    // Only `next` is modelled, and only its presence is used: it is how OL
    // says the list goes on past this page. Not to be confused with the
    // `links` *array* on a work or author document, which is a list of
    // external web links and is not modelled at all.
    public class OpenLibraryListLinks
    {
        public string Next { get; set; }
    }
}
