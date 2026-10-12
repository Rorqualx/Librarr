namespace NzbDrone.Core.MetadataSource
{
    public interface IIdentifyUnmappedLegacyBooks
    {
        // True for a book that still carries its pre-OpenLibrary id and has
        // no BookIdMapping row to translate it. The metadata source cannot
        // be asked about such a book, so "the source did not return it" says
        // nothing about whether the book still exists.
        bool IsUnmappedLegacyBook(string foreignBookId);
    }
}
