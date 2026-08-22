using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Mappers
{
    internal static class OpenLibraryEditionMapper
    {
        // Overload kept for method-group use (OpenLibraryWorkMapper maps a
        // work's edition list with no queried ISBN in play).
        public static Edition ToEdition(OpenLibraryEditionResource resource)
        {
            return ToEdition(resource, null);
        }

        public static Edition ToEdition(OpenLibraryEditionResource resource, string queriedIsbn)
        {
            var olKey = ExtractKey(resource.Key);
            var isbn13 = SelectIsbn13(resource, queriedIsbn);
            var asin = resource.Identifiers?.Amazon?.FirstOrDefault();
            var format = NormalizeFormat(resource.PhysicalFormat);

            // Cover-resolution chain (best → fallback):
            //   1. resource.Covers — work-specific cover_i list returned by OL
            //      for editions that have an indexed primary cover.
            //   2. ISBN-keyed — many editions ship without a `covers` field
            //      yet have an /b/isbn/<n>-L.jpg image. Verified live for the
            //      majority of Sparknotes / Pottermore editions whose JSON
            //      omits `covers`. `?default=false` (set in
            //      OpenLibraryCoverUrls) makes missing covers 404 so the
            //      MediaCoverProxy 404-quiet path takes over. A *derived*
            //      ISBN-13 (isbn_10-only edition, see SelectIsbn13) works
            //      here too: OL's cover index resolves checksum-equivalents,
            //      verified live for both isbn_10-only captures (2026-08-05).
            //   3. olid-keyed — final fallback for editions that have no
            //      ISBN either; cheap to keep, harmless when it 404s.
            // When none of the three actually produces an image, the
            // frontend renders its built-in book placeholder.
            var images = OpenLibraryCoverUrls.ForBook(resource.Covers);
            if (images.Count == 0 && isbn13.IsNotNullOrWhiteSpace())
            {
                images = OpenLibraryCoverUrls.ForBookByIsbn(isbn13);
            }

            if (images.Count == 0 && olKey.IsNotNullOrWhiteSpace())
            {
                images = OpenLibraryCoverUrls.ForBookByOlid(olKey);
            }

            return new Edition
            {
                ForeignEditionId = olKey,
                TitleSlug = olKey,
                Isbn13 = isbn13,
                Asin = asin,
                Title = resource.Title,
                Language = ExtractLanguageCode(resource.Languages),
                Overview = resource.Description,
                Format = format,
                IsEbook = IsEbookFormat(format),
                Disambiguation = resource.Subtitle,
                Publisher = resource.Publishers?.FirstOrDefault(),
                PageCount = resource.NumberOfPages ?? 0,
                ReleaseDate = OpenLibraryDateParser.Parse(resource.PublishDate),
                Images = images,
                Monitored = true
            };
        }

        // Issue #10: two of the five captured /isbn/ payloads carry no
        // `isbn_13` at all — OL answered with `isbn_10` only, in both cases
        // the checksum-equivalent of the ISBN-13 that was queried. Reading
        // `isbn_13` alone sent those candidates to the matcher with no ISBN,
        // which drops the 10.0-weight isbn bucket from DistanceCalculator's
        // denominator and lets the 3.0-weight author penalty dominate the
        // normalised score (0.2857 against the 0.20 gate, measured in PR #4).
        // Deriving the ISBN-13 from `isbn_10` is a checksum, not a round trip.
        //
        // Which entry matters. Dune's edition record lists two ISBN-10s —
        // different printings on one record — and only the second is the ISBN
        // that was queried. Blindly taking the first would attach a
        // *different* ISBN-13 than the one the caller looked up, which is
        // worse than none: a present-and-mismatched ISBN scores the full
        // 10.0-weight bucket at distance 1. So the queried ISBN, when the
        // caller knows it, wins over list order.
        //
        // Which entry can be chosen at all. The queried ISBN, when the caller
        // knows it, disambiguates. Without it — the work-list mapping path,
        // where ToEdition is a method group with no ISBN in play — a record
        // carrying several printings has nothing to pick by, and committing to
        // an arbitrary one fabricates an ISBN-13 the file may then mismatch at
        // the full 10.0 weight (worse than the 0.1 isbn_missing that null
        // yields). So a lone derivable ISBN-10 is taken, but several with no
        // query stay null. (#13 review, Finding 2.)
        private static string SelectIsbn13(OpenLibraryEditionResource resource, string queriedIsbn)
        {
            // The queried ISBN can arrive in either format — ReidentifyService
            // passes whatever the file's tags carry, and older or Calibre-
            // tagged files commonly hold an ISBN-10. Compare in ISBN-13 space.
            var queried = IsbnUtils.ToIsbn13(queriedIsbn);

            if (resource.Isbn13 != null && resource.Isbn13.Count > 0)
            {
                var preferred = queried != null
                    ? resource.Isbn13.FirstOrDefault(i => IsbnUtils.NormalizeIsbn(i) == queried)
                    : null;

                // Several listed isbn_13 with no query is the same ambiguity as
                // the derived branch below, but each is a real ISBN-13 that OL
                // attached to the record rather than one this code invented, and
                // this first-wins was the behaviour before #13 — left as is.
                return preferred ?? resource.Isbn13.First();
            }

            var derived = resource.Isbn10?
                .Select(IsbnUtils.Isbn10ToIsbn13)
                .Where(i => i != null)
                .ToList();

            if (derived == null || derived.Count == 0)
            {
                return null;
            }

            if (queried != null)
            {
                return derived.FirstOrDefault(i => i == queried) ?? derived.First();
            }

            return derived.Count == 1 ? derived[0] : null;
        }

        // OL's Languages list carries entries like {"key": "/languages/eng"}.
        // Extract the 3-letter ISO 639-2 code from the last segment of the
        // first entry. Cycle 2 of the Le Guin completeness loop: Edition
        // already had a Language column + the EditionResource already
        // surfaced it both ways, but the mapper here was the gap — so
        // 0/249 books had a populated language despite OL having data on
        // 244/249. SelectPrimaryEdition in OpenLibraryWorkMapper already
        // reads the same Languages list for its English-preference tiers,
        // proving the data shape is reliable.
        private static string ExtractLanguageCode(System.Collections.Generic.List<Resources.OpenLibraryKey> languages)
        {
            var key = languages?.FirstOrDefault()?.Key;
            if (key.IsNullOrWhiteSpace())
            {
                return null;
            }

            var slash = key.LastIndexOf('/');
            return slash >= 0 ? key.Substring(slash + 1) : key;
        }

        public static Book ToBook(OpenLibraryEditionResource resource, string queriedIsbn = null)
        {
            // When an external ID lookup (ISBN, ASIN, edition OLID) hits OL,
            // we only know the edition. Reconstruct a slim book wrapper so
            // downstream code can still navigate Book → Editions.
            var workKey = resource.Works?.FirstOrDefault()?.Key;
            var foreignBookId = workKey.IsNotNullOrWhiteSpace() ? ExtractKey(workKey) : ExtractKey(resource.Key);

            var edition = ToEdition(resource, queriedIsbn);

            var book = new Book
            {
                ForeignBookId = foreignBookId,
                ForeignEditionId = edition.ForeignEditionId,
                TitleSlug = foreignBookId,
                Title = resource.Title,
                CleanTitle = Parser.Parser.CleanAuthorName(resource.Title),
                ReleaseDate = edition.ReleaseDate,
                AnyEditionOk = true,
                Editions = new List<Edition> { edition }
            };

            // The edition JSON carries author *keys* and never author *names*.
            // Attach the primary key so the caller knows who to ask about; the
            // display name has to come from a separate /authors/{key}.json
            // lookup, which OpenLibraryProxy does outside its response cache.
            //
            // This matters more than it looks. Import identification scores
            // candidates on the author name, and an empty name is maximum
            // author-distance rather than "unknown" — a fixed 0.1875 against a
            // 0.20 accept gate, before anything else is even considered. See
            // issue #7.
            //
            // Name is empty rather than null, matching the stub
            // CandidateService.ToCandidates builds for the same situation.
            var authorKey = ExtractKey(resource.Authors?.FirstOrDefault()?.Key);
            if (authorKey.IsNotNullOrWhiteSpace())
            {
                var metadata = new AuthorMetadata
                {
                    ForeignAuthorId = authorKey,
                    TitleSlug = authorKey,
                    Name = string.Empty
                };

                book.AuthorMetadata = metadata;
                book.Author = new Author { Metadata = metadata, CleanName = string.Empty };
            }

            // TODO Phase 4: also fetch the work for richer metadata when callers
            // can absorb a second HTTP call.
            return book;
        }

        private static string NormalizeFormat(string raw)
        {
            if (raw.IsNullOrWhiteSpace())
            {
                return null;
            }

            var lower = raw.ToLowerInvariant();
            if (lower.Contains("ebook") || lower.Contains("electronic") || lower.Contains("epub") || lower.Contains("kindle"))
            {
                return "EBook";
            }

            if (lower.Contains("audio"))
            {
                return "AudioBook";
            }

            if (lower.Contains("hardcover") || lower.Contains("hardback"))
            {
                return "Hardcover";
            }

            if (lower.Contains("paperback") || lower.Contains("softcover"))
            {
                return "Paperback";
            }

            return raw;
        }

        private static bool IsEbookFormat(string normalizedFormat)
        {
            return normalizedFormat == "EBook" || normalizedFormat == "AudioBook";
        }

        private static string ExtractKey(string olKey)
        {
            if (olKey.IsNullOrWhiteSpace())
            {
                return olKey;
            }

            var slash = olKey.LastIndexOf('/');
            return slash >= 0 ? olKey.Substring(slash + 1) : olKey;
        }
    }
}
