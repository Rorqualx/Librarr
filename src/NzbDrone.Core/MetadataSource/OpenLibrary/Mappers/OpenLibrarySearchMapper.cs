using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Mappers
{
    internal static class OpenLibrarySearchMapper
    {
        // Map each search doc to a slim Book record. The search response
        // doesn't include enough to populate Editions properly; downstream
        // code is expected to call GetBookInfo(work_key) for fuller data.
        public static List<Book> ReRankAndMap(OpenLibrarySearchResource resource, string queryTitle, string queryAuthor)
        {
            if (resource?.Docs == null || resource.Docs.Count == 0)
            {
                return new List<Book>();
            }

            var ranked = resource.Docs
                .Select(d => new { Doc = d, Score = Score(d, queryTitle, queryAuthor) })
                .OrderByDescending(x => x.Score)
                .Take(20);

            return ranked.Select(x => ToBook(x.Doc)).ToList();
        }

        // OL's author relevance ranking is keyword-soup in the same way its
        // book ranking is, and it degrades badly on the folder-name shapes
        // the Library Import wizard feeds it. Verified against live OL:
        // "Tolkien, J.R.R." — an ordinary Calibre folder convention — ranks
        // a 1-work record titled "Wheeler, R.E.M. And Wheeler, T.V.
        // (Tolkien, J..." above the real 355-work J.R.R. Tolkien. The wizard
        // auto-selects the first result, so that ordering is what actually
        // gets imported.
        //
        // Re-rank on name match first, with work count only as a bounded
        // tiebreak — sorting on work count alone would bury a genuinely
        // obscure author under a famous unrelated one. OrderByDescending is
        // stable, so records that tie keep OL's own ordering.
        public static List<Author> ReRankAndMapAuthors(OpenLibraryAuthorSearchResource resource, string query)
        {
            if (resource?.Docs == null || resource.Docs.Count == 0)
            {
                return new List<Author>();
            }

            return resource.Docs
                .OrderByDescending(d => ScoreAuthor(d, query))
                .Select(ToAuthorSummary)
                .ToList();
        }

        public static Author ToAuthorSummary(OpenLibraryAuthorSearchDoc doc)
        {
            var key = ExtractKey(doc.Key);

            // OL's /search/authors.json response doesn't include the
            // photo id, but covers.openlibrary.org accepts olid-keyed
            // photos. Use that so the search-result tile shows the
            // author photo immediately, without waiting for the full
            // /authors/{key}.json hydration on add. `?default=false`
            // (set in OpenLibraryCoverUrls) means missing photos 404
            // so the frontend falls back to its own placeholder.
            var author = new Author
            {
                Metadata = new AuthorMetadata
                {
                    ForeignAuthorId = key,
                    TitleSlug = key,
                    Name = doc.Name,
                    Born = OpenLibraryDateParser.Parse(doc.BirthDate),
                    Images = OpenLibraryCoverUrls.ForAuthorByOlid(key),

                    // Transient — see AuthorMetadata. A search for a
                    // well-known author routinely returns several records
                    // whose every mapped field is identical, and these two
                    // are the only way to tell them apart: OL carries three
                    // "Stephen King" records at 606, 48 and 7 works, the
                    // last of which wrote Principles of Macroeconomics.
                    WorkCount = doc.WorkCount,
                    TopWork = doc.TopWork
                },
                CleanName = Parser.Parser.CleanAuthorName(doc.Name)
            };

            return author;
        }

        private static Book ToBook(OpenLibrarySearchDoc doc)
        {
            var key = ExtractKey(doc.Key);

            // A search document is a work, and its `isbn` array is the ISBNs of
            // every edition of that work: 187 entries for Dune in the captured
            // search_dune_herbert.json. This used to take the first
            // 13-character one and set it as the placeholder edition's Isbn13,
            // which is the hazard OpenLibraryEditionMapper.SelectIsbn13 was
            // written to avoid, on a much larger scale: a file tagged with any
            // of the other 186 scored the 10.0-weight isbn bucket at distance
            // 1 against the correct work (0.6250 normalised, against a 0.20
            // gate) and was rejected as not close enough.
            //
            // The placeholder asserts no ISBN. The whole list travels in
            // WorkIsbns, in ISBN-13 space, for DistanceCalculator to test
            // membership against -- see the note on Edition.WorkIsbns. The
            // array mixes ISBN-10s, ISBN-13s and hyphenated forms
            // ("978-2-266-00856-0"); ToIsbn13 brings them to one form and
            // drops whatever is neither.
            var workIsbns = (doc.Isbn ?? new List<string>())
                .Select(IsbnUtils.ToIsbn13)
                .Where(i => i != null)
                .Distinct()
                .ToList();

            // Only for the cover fallback below, where any edition's ISBN will
            // do: the covers API serves the image of whichever edition it is.
            var isbn13 = doc.Isbn?.FirstOrDefault(i => i?.Length == 13);

            // OL's search response includes author_key + author_name as
            // parallel arrays (the work can have multiple co-authors).
            // The AddBook validator's `RuleFor(b => b.Author.ForeignAuthorId).NotEmpty()`
            // 400s when Author isn't populated, which silently surfaces
            // in the UI as "'Author. Foreign Author Id' must not be empty."
            // Pick the first author key/name pair; co-authors are out of
            // scope for the search-result summary (the full Author refresh
            // re-fetches via /authors/{key}.json anyway).
            var authorKey = doc.AuthorKey?.FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            var authorName = doc.AuthorName?.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

            var book = new Book
            {
                ForeignBookId = key,
                TitleSlug = key,
                Title = doc.Title,
                CleanTitle = Parser.Parser.CleanAuthorName(doc.Title),
                ReleaseDate = doc.FirstPublishYear.HasValue
                    ? new System.DateTime(doc.FirstPublishYear.Value, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
                    : (System.DateTime?)null,
                AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        ForeignEditionId = key,
                        TitleSlug = key,
                        Title = doc.Title,
                        WorkIsbns = workIsbns,
                        Monitored = true,

                        // Cover-resolution chain (best → fallback):
                        //   1. cover_i — deterministic primary cover id from
                        //      OL's search index. Always works when set.
                        //   2. ISBN-keyed — `/b/isbn/<isbn13>` works for many
                        //      books that have an edition cover but no
                        //      work-level cover indexed. Verified against
                        //      live OL: canonical Sanderson ISBNs all 302
                        //      to a real image even when cover_i is missing
                        //      from the search doc.
                        //   3. olid-keyed — the rare edge case where work
                        //      has `covers` set but search response omits
                        //      cover_i (search reindex race). Preserved
                        //      from the earlier fix; cheap to keep.
                        // For works with no cover anywhere on OL (e.g.
                        // small-press samplers), all three 404 and the
                        // frontend falls back to its own placeholder via
                        // the 404-quiet path in MediaCoverProxy.GetImage.
                        Images =
                            doc.CoverI.HasValue
                                ? OpenLibraryCoverUrls.ForBook(new List<int> { doc.CoverI.Value })
                                : (!string.IsNullOrWhiteSpace(isbn13)
                                    ? OpenLibraryCoverUrls.ForBookByIsbn(isbn13)
                                    : OpenLibraryCoverUrls.ForBookByOlid(key))
                    }
                }
            };

            if (authorKey != null)
            {
                var authorMeta = new AuthorMetadata
                {
                    ForeignAuthorId = authorKey,
                    TitleSlug = authorKey,
                    Name = authorName ?? authorKey,
                };

                book.Author = new Author
                {
                    Metadata = authorMeta,
                    CleanName = Parser.Parser.CleanAuthorName(authorName ?? authorKey)
                };

                book.AuthorMetadata = authorMeta;
            }

            return book;
        }

        // Token-overlap re-rank. OL's relevance ranking is keyword-soup, so
        // a cheap exact-token boost meaningfully improves first-result quality.
        // TODO Phase 4: substitute Levenshtein from Parser/Parser.cs helpers.
        private static int Score(OpenLibrarySearchDoc doc, string queryTitle, string queryAuthor)
        {
            var score = 0;

            if (queryTitle.IsNotNullOrWhiteSpace() && doc.Title.IsNotNullOrWhiteSpace())
            {
                var queryTokens = Tokens(queryTitle);
                var titleTokens = Tokens(doc.Title);
                score += queryTokens.Intersect(titleTokens).Count() * 10;

                if (string.Equals(doc.Title.Trim(), queryTitle.Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
            }

            if (queryAuthor.IsNotNullOrWhiteSpace() && doc.AuthorName != null)
            {
                var queryAuthorLower = queryAuthor.ToLowerInvariant();
                if (doc.AuthorName.Any(a => a != null && a.ToLowerInvariant().Contains(queryAuthorLower)))
                {
                    score += 30;
                }
            }

            score += System.Math.Min(doc.EditionCount, 50);

            return score;
        }

        // Author-name variant of Score(). Deliberately a separate scorer:
        // the book one is tuned against titles and drops tokens of two
        // characters or fewer, which would throw away real surnames ("Ng",
        // "Wu") and the "Le" of "Le Guin". Single characters stay dropped —
        // matching on the "J" of "J.R.R." is pure noise.
        private static int ScoreAuthor(OpenLibraryAuthorSearchDoc doc, string query)
        {
            var score = 0;

            var queryTokens = AuthorTokens(query).ToList();

            if (queryTokens.Any() && doc.Name.IsNotNullOrWhiteSpace())
            {
                score += queryTokens.Intersect(AuthorTokens(doc.Name)).Count() * 10;
            }

            // Exact hit on the primary name or on any alternate. OL lists
            // "Sanderson, Brandon" as an alternate of "Brandon Sanderson",
            // which is the shape a library folder tends to have.
            if (MatchesExactly(doc.Name, query) ||
                (doc.AlternateNames != null && doc.AlternateNames.Any(n => MatchesExactly(n, query))))
            {
                score += 50;
            }

            // Bounded, so it breaks ties between name-equivalent records
            // without ever outweighing the name match itself. Same shape as
            // the EditionCount tiebreak in Score() above.
            score += System.Math.Min(doc.WorkCount, 50);

            return score;
        }

        private static bool MatchesExactly(string candidate, string query)
        {
            return candidate.IsNotNullOrWhiteSpace() &&
                   query.IsNotNullOrWhiteSpace() &&
                   string.Equals(candidate.Trim(), query.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> AuthorTokens(string s)
        {
            return (s ?? string.Empty)
                .ToLowerInvariant()
                .Split(new[] { ' ', '-', ':', ',', '.', '(', ')' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 1);
        }

        private static IEnumerable<string> Tokens(string s)
        {
            return (s ?? string.Empty)
                .ToLowerInvariant()
                .Split(new[] { ' ', '-', ':', ',', '.', '(', ')' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2);
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
