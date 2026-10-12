using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    public class OpenLibraryEditionResource
    {
        // "/books/OL12345M"
        public string Key { get; set; }

        public string Title { get; set; }
        public string Subtitle { get; set; }

        public List<string> Publishers { get; set; }

        [JsonProperty("publish_date")]
        public string PublishDate { get; set; }

        [JsonProperty("isbn_10")]
        public List<string> Isbn10 { get; set; }

        [JsonProperty("isbn_13")]
        public List<string> Isbn13 { get; set; }

        [JsonProperty("number_of_pages")]
        public int? NumberOfPages { get; set; }

        [JsonProperty("physical_format")]
        public string PhysicalFormat { get; set; }

        [JsonConverter(typeof(OpenLibraryIdListConverter))]
        public List<int> Covers { get; set; }

        public List<OpenLibraryKey> Languages { get; set; }

        public List<OpenLibraryKey> Works { get; set; }

        public List<OpenLibraryKey> Authors { get; set; }

        public OpenLibraryIdentifiers Identifiers { get; set; }

        [JsonConverter(typeof(OpenLibraryDescriptionConverter))]
        public string Description { get; set; }

        public string Pagination { get; set; }
    }

    public class OpenLibraryIdentifiers
    {
        // OL stores ASIN under "amazon" — a list of ASINs (rarely more than one).
        public List<string> Amazon { get; set; }
        public List<string> Goodreads { get; set; }
        public List<string> Librarything { get; set; }
    }
}
