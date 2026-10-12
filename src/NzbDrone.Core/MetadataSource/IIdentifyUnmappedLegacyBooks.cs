namespace NzbDrone.Core.MetadataSource
{
    public interface IIdentifyUnmappedLegacyBooks
    {
        // True for a book that still carries its pre-OpenLibrary id and has
        // no BookIdMapping row to translate it. The metadata source cannot
        // be asked about such a book, so "the source did not return it" says
        // nothing about whether the book still exists.
        //
        // A row the user has not confirmed and reidentify was not sure of
        // (BookIdMapping.IsTrusted) does not count as a mapping: it is a
        // guess waiting on the review page, and nothing is translated
        // through it.
        bool IsUnmappedLegacyBook(string foreignBookId);

        // True for any book that still carries its pre-OpenLibrary id,
        // mapped or not. Such a book is in the library because the user had
        // it in Readarr. Its mapping came from reidentify, and a confident
        // match can still be the wrong author or work, so "the matched
        // author's list does not have it" is a statement about the match,
        // not about the book.
        bool IsLegacyBook(string foreignBookId);

        // The same question about an author. Asking the source for such an
        // author sends it an id it has never heard of.
        bool IsUnmappedLegacyAuthor(string foreignAuthorId);

        // OpenLibrary has merged works that Goodreads kept apart, so two
        // books in a library imported from Readarr can map to one work,
        // and a book added since can be the very work a legacy book maps
        // to. The source lists that work once and its entry can carry only
        // one local id, so only one of those books is refreshed from it.
        //
        // Returns the foreign id of the book that is, when it is not the
        // one asked about; null when the book asked about is the one
        // refreshed, or nothing else shares its work. A book this returns
        // an id for is missing from the author's list for that reason
        // alone, which is no evidence the source has dropped it.
        string FindBookRefreshedInstead(string foreignBookId);
    }
}
