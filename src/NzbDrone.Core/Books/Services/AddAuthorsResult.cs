using System.Collections.Generic;

namespace NzbDrone.Core.Books
{
    public enum AddAuthorSkipReason
    {
        // Another entry earlier in the same batch resolved to the same author.
        Duplicate,

        // The author is already in the library.
        Exists,

        // The lookup or validation failed; Message says why.
        Failed
    }

    public class SkippedAuthor
    {
        // The author as it was requested, not as the metadata source returned
        // it, so the caller can tie the outcome back to what it sent.
        public Author Author { get; set; }
        public AddAuthorSkipReason Reason { get; set; }
        public string Message { get; set; }
    }

    public class AddAuthorsResult
    {
        public List<Author> Added { get; set; } = new List<Author>();
        public List<SkippedAuthor> Skipped { get; set; } = new List<SkippedAuthor>();
    }
}
