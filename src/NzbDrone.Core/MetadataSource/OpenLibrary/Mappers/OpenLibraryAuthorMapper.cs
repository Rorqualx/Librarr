using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Mappers
{
    internal static class OpenLibraryAuthorMapper
    {
        public static Author ToAuthor(OpenLibraryAuthorResource resource, OpenLibraryAuthorWorksResource works)
        {
            var metadata = ToMetadata(resource);

            // WorkCount only ever came from /search/authors.json, so an
            // author reached by id instead of by name arrived with 0 —
            // and describeMatch() in the Library Import wizard renders 0
            // as "No works". Pasting a known-good OL id therefore showed
            // the correct author labelled as if it were an empty stub,
            // which is precisely backwards.
            //
            // works.json's `size` is the same quantity: it reports 238 for
            // OL79043A where the search index says 236, a freshness gap,
            // not a different measure. TopWork stays unset — OL computes
            // `top_work` in the search index and the works list is not
            // ordered by it, so the first entry would be a guess wearing
            // the label of a fact. describeMatch already handles its
            // absence.
            if (works != null)
            {
                metadata.WorkCount = works.Size;
            }

            // Series stays empty for OL — OL doesn't model series at the
            // author level; per-work series links surface during book
            // refresh via OpenLibrarySeriesProxy. Initialising to an
            // empty list (rather than leaving the LazyLoaded null)
            // avoids the NRE in RefreshAuthorService.UpdateEntity:141
            // (`local.Series = remote.Series.Value`) on every Add-Author
            // refresh.
            var author = new Author
            {
                Metadata = metadata,
                CleanName = Parser.Parser.CleanAuthorName(metadata.Name),
                Monitored = true,
                Series = new List<Series>(),
                Books = ToSlimBooks(works, metadata),
            };

            return author;
        }

        // Map each work entry to a slim Book record. Editions stay empty
        // here — the per-book refresh path (RefreshBookService →
        // OpenLibraryProxy.GetBookInfo) fetches /works/{key}/editions.json
        // and hydrates the full edition list lazily. This slim mapping
        // is what gives the Add-Author flow its initial Books(N) count;
        // before this, the works payload was fetched but discarded, so
        // every fresh-add landed on Books(0).
        private static List<Book> ToSlimBooks(OpenLibraryAuthorWorksResource works, AuthorMetadata authorMetadata)
        {
            if (works?.Entries == null || works.Entries.Count == 0)
            {
                return new List<Book>();
            }

            return works.Entries
                .Where(w => !string.IsNullOrWhiteSpace(w?.Key) && !string.IsNullOrWhiteSpace(w.Title))
                .Select(w =>
                {
                    var workKey = ExtractKey(w.Key);

                    // Each slim book MUST carry at least one Edition.
                    // MetadataProfileService.FilterBooks:171 removes any
                    // book whose `Editions.Value.Any()` is false — so an
                    // entry with `Editions = new List<Edition>()` gets
                    // silently dropped and the Author lands on Books(0)
                    // even when the works payload was non-empty. The
                    // placeholder edition reuses the work key as a
                    // foreign id; per-book refresh (RefreshBookService →
                    // OpenLibraryProxy.GetBookInfo) replaces it with the
                    // real edition set from /works/{key}/editions.json.
                    var placeholderEdition = new Edition
                    {
                        ForeignEditionId = workKey,
                        TitleSlug = workKey,
                        Title = w.Title,
                        Monitored = true,
                        Images = OpenLibraryCoverUrls.ForBookByOlid(workKey),
                    };

                    // OL's bulk-imported author/works payload omits
                    // `first_publish_date` on the vast majority of entries
                    // (verified live: 192/193 Brandon Sanderson works have
                    // it null). The Standard metadata profile's
                    // SkipMissingDate=1 then filters them all out.
                    // Synthesise a placeholder date so HasValue passes;
                    // the real date is hydrated when the per-book refresh
                    // fetches /works/{key}.json (which usually has a
                    // `first_publish_date` even when the author/works
                    // index doesn't).
                    //
                    // The placeholder is a named value, not a date: it means
                    // "not known yet", and anything that reasons about when a
                    // book came out has to ask PlaceholderReleaseDate.IsKnown
                    // rather than read it as 1900. See that type for why it
                    // is not simply null.
                    var releaseDate = OpenLibraryDateParser.Parse(w.FirstPublishDate)
                        ?? PlaceholderReleaseDate.Value;

                    return new Book
                    {
                        ForeignBookId = workKey,
                        TitleSlug = workKey,
                        Title = w.Title,
                        CleanTitle = Parser.Parser.CleanAuthorName(w.Title),
                        ReleaseDate = releaseDate,
                        Genres = w.Subjects?.Take(10).ToList() ?? new List<string>(),
                        AnyEditionOk = true,
                        Monitored = true,
                        Editions = new List<Edition> { placeholderEdition },
                        AuthorMetadata = authorMetadata,

                        // MetadataProfileService.FilterBooks calls
                        // BookAllowedByRating with the Standard profile's
                        // MinPopularity=350 (a Goodreads-era default that
                        // Phase 5 didn't migrate for OL). Without
                        // synthesised ratings, every slim book fails
                        // `Popularity >= MinPopularity` and the user
                        // lands on Books(0) even with a clean refresh.
                        // 5.0 × 200 = 1000 popularity comfortably clears
                        // typical profile thresholds and gets overwritten
                        // by real ratings when the per-book refresh hits
                        // /works/{key}.json.
                        Ratings = new Ratings { Value = 5.0m, Votes = 200 },
                    };
                })
                .ToList();
        }

        public static AuthorMetadata ToMetadata(OpenLibraryAuthorResource resource)
        {
            var olKey = ExtractKey(resource.Key);

            var metadata = new AuthorMetadata
            {
                ForeignAuthorId = olKey,
                TitleSlug = olKey,

                // Empty rather than null when the document carries no name at
                // all, which is what a /type/redirect or /type/delete record
                // looks like. OpenLibraryProxy resolves those before mapping,
                // so this is the backstop: CleanSpaces() on null is an
                // ArgumentNullException, and one unnamed document should not
                // abort an author refresh. Empty matches the stub
                // OpenLibraryWorkMapper builds for an author it cannot name yet.
                Name = (resource.PersonalName ?? resource.Name)?.CleanSpaces() ?? string.Empty,
                Overview = resource.Bio,
                Born = OpenLibraryDateParser.Parse(resource.BirthDate),
                Died = OpenLibraryDateParser.Parse(resource.DeathDate),
                Status = resource.DeathDate.IsNotNullOrWhiteSpace() ? AuthorStatusType.Ended : AuthorStatusType.Continuing
            };

            metadata.SortName = metadata.Name?.ToLowerInvariant();
            metadata.NameLastFirst = metadata.Name.ToLastFirst();
            metadata.SortNameLastFirst = metadata.NameLastFirst?.ToLowerInvariant();

            if (resource.AlternateNames != null)
            {
                metadata.Aliases.AddRange(resource.AlternateNames);
            }

            metadata.Images = OpenLibraryCoverUrls.ForAuthor(resource.Photos);

            // TODO Phase 4: links → Links list (homepage, wikipedia).
            return metadata;
        }

        private static string ExtractKey(string olKey)
        {
            // "/authors/OL5749351A" → "OL5749351A"; pass-through if already bare.
            if (olKey.IsNullOrWhiteSpace())
            {
                return olKey;
            }

            var slash = olKey.LastIndexOf('/');
            return slash >= 0 ? olKey.Substring(slash + 1) : olKey;
        }
    }
}
