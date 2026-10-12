using System;
using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.MetadataSource
{
    public interface IProvideBookInfo
    {
        // useCache: false asks the source afresh instead of serving a copy
        // it already holds. For a refresh the user explicitly asked for, of
        // one book; anything that walks the library should leave it true.
        Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string id, bool useCache = true);
    }
}
