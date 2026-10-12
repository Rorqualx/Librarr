using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    public class OpenLibraryAuthorResource : IOpenLibraryDocument
    {
        // "/authors/OL5749351A"
        public string Key { get; set; }

        // "/type/author" for a live record. A record that was merged into
        // another is served -- with a 200 -- as "/type/redirect", and a removed
        // one as "/type/delete"; neither carries a name. See
        // OpenLibraryDocumentType.
        [JsonConverter(typeof(OpenLibraryKeyOrStringConverter))]
        public string Type { get; set; }

        // On a redirect document, the key of the surviving record
        // ("/authors/OL2A"). Careful: on a live author this same field is
        // free text for where they lived, so it only means "go here" when
        // Type says redirect.
        [JsonConverter(typeof(OpenLibraryKeyOrStringConverter))]
        public string Location { get; set; }

        public string Name { get; set; }

        [JsonProperty("personal_name")]
        public string PersonalName { get; set; }

        [JsonConverter(typeof(OpenLibraryDescriptionConverter))]
        public string Bio { get; set; }

        [JsonProperty("birth_date")]
        public string BirthDate { get; set; }

        [JsonProperty("death_date")]
        public string DeathDate { get; set; }

        [JsonProperty("alternate_names")]
        public List<string> AlternateNames { get; set; }

        [JsonConverter(typeof(OpenLibraryIdListConverter))]
        public List<int> Photos { get; set; }
    }

    public class OpenLibraryAuthorWorksResource
    {
        // The author's total work count as OL reports it. Slightly generous:
        // it counts records the list itself leaves out (Le Guin: size 252, 251
        // entries), so it is not a reliable "is there more" test. Links.Next
        // is. After OpenLibraryProxy.FetchAllWorks, Entries holds every page.
        public int Size { get; set; }
        public OpenLibraryListLinks Links { get; set; }
        public List<OpenLibraryWorkResource> Entries { get; set; }
    }
}
