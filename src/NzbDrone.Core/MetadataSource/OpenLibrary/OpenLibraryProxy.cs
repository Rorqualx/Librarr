using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using LazyCache;
using LazyCache.Providers;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource.OpenLibrary.Mappers;
using NzbDrone.Core.MetadataSource.OpenLibrary.Resources;

namespace NzbDrone.Core.MetadataSource.OpenLibrary
{
    // Phase 3 MVP + Phase 4 hardening. Implements the same method shapes as
    // the IProvide* / ISearchForNew* interfaces but DOES NOT declare them
    // (RegisterMany would bind a second impl per interface alongside
    // BookInfoProxy — see Phase 5 MetadataSourceFactory).
    //
    // Phase 4 added per-resource LazyCache wrapping with the TTLs from
    // MASTER-PLAN.md §4 (authors 24h, works 7d, editions 30d, search 1h)
    // and a Send<T> helper that retries 429 / 5xx with exponential
    // back-off + jitter (no Polly: Polly 8's generic pipeline doesn't
    // compose cleanly with HttpResponse<T> covariance — see commit msg).
    public class OpenLibraryProxy
    {
        private const int MaxRetries = 3;
        private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);

        private readonly IHttpClient _httpClient;
        private readonly IOpenLibraryRequestBuilder _requestBuilder;
        private readonly IMetadataSourceStatusService _statusService;
        private readonly Logger _logger;
        private readonly CachingService _cache;

        public OpenLibraryProxy(IHttpClient httpClient,
                                IOpenLibraryRequestBuilder requestBuilder,
                                IMetadataSourceStatusService statusService,
                                Logger logger)
        {
            _statusService = statusService;
            _httpClient = httpClient;
            _requestBuilder = requestBuilder;
            _logger = logger;

            _cache = new CachingService(new MemoryCacheProvider(new MemoryCache(new MemoryCacheOptions())));
            _cache.DefaultCachePolicy = new CacheDefaults { DefaultCacheDurationSeconds = 3600 };
        }

        public Author GetAuthorInfo(string foreignAuthorId, bool useCache = true)
        {
            // Cache the raw HTTP resources only; run the mapper on every
            // call so each caller gets a fresh Author + slim Book list.
            // LazyCache hands back the same object reference on hit, and
            // downstream code (BookService.AddBook, BasicRepository.Insert)
            // mutates Book/Edition fields in place — so a cached *mapped*
            // payload leaks Id/BookId/Monitored mutations across calls.
            var resources = Cached(useCache, $"oa_{foreignAuthorId}", TimeSpan.FromHours(24), () =>
            {
                var authorReq = _requestBuilder.For($"authors/{foreignAuthorId}.json").Build();

                // OL accepts limit=1000 in a single response (verified:
                // Le Guin's 252 works returned 251 entries at limit=1000
                // vs 200 at limit=200, clipping The Dispossessed off
                // the end and out of the author's discography in the
                // UI entirely). All prolific authors known today stay
                // well under 1000 (Asimov ~500, King ~600, Le Guin 252).
                // When a future author exceeds this, the symptom is a
                // clipped list (size > entries.Count) and the fix is
                // to escalate to a paginated loop here — easy migration.
                var worksReq = _requestBuilder.For($"authors/{foreignAuthorId}/works.json?limit=1000").Build();

                var authorResp = Send<OpenLibraryAuthorResource>(authorReq);
                var worksResp = Send<OpenLibraryAuthorWorksResource>(worksReq);

                if (authorResp?.Resource == null)
                {
                    throw new OpenLibraryException("OL author not found: {0}", foreignAuthorId);
                }

                return (Author: authorResp.Resource, Works: worksResp?.Resource);
            });

            return OpenLibraryAuthorMapper.ToAuthor(resources.Author, resources.Works);
        }

        public HashSet<string> GetChangedAuthors(DateTime startTime)
        {
            // OL has no changed-since API. The per-author refresh schedule
            // covers freshness. Suppress the delta-refresh path.
            _logger.Debug("OL GetChangedAuthors called (startTime={0}); OL has no delta API, returning empty.", startTime);
            return new HashSet<string>();
        }

        public Tuple<string, Book, List<AuthorMetadata>> GetBookInfo(string id)
        {
            var resources = FetchWorkBundle(id);

            var (book, authors) = OpenLibraryWorkMapper.ToBook(resources.Work, resources.Editions);

            // AddBookService.AddSkyhookData:130 expects Item1 to be
            // the **author** foreign id so it can locate the matching
            // AuthorMetadata in Item3 via
            //   tuple.Item3.FirstOrDefault(x => x.ForeignAuthorId == tuple.Item1)
            // The BookInfoProxy returned `authorId` here; the mapper
            // was incorrectly returning the work id, which never
            // matched anything in `authors` (those carry author OLIDs
            // ending in A, work id ends in W). Result: AuthorMetadata
            // null, then NRE on `.Value.ForeignAuthorId` access in
            // AddBookService:58.
            var primaryAuthorId = authors.FirstOrDefault()?.ForeignAuthorId ?? id;
            return Tuple.Create(primaryAuthorId, book, authors);
        }

        // Cover-picker modal endpoint. Returns deduped candidates from
        // work.covers (OL's editorial picks, ordered first) plus every
        // edition's cover_i (with publisher/year metadata for the
        // thumbnail label). Reuses the same 7-day cache as GetBookInfo,
        // so the first modal open after a refresh is sub-second.
        public List<CoverCandidate> GetCoverCandidates(string foreignBookId)
        {
            var resources = FetchWorkBundle(foreignBookId);
            var seen = new HashSet<int>();
            var candidates = new List<CoverCandidate>();

            foreach (var coverId in resources.Work?.Covers ?? new List<int>())
            {
                if (coverId > 0 && seen.Add(coverId))
                {
                    candidates.Add(new CoverCandidate
                    {
                        CoverId = coverId,
                        Url = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg",
                        Source = "work"
                    });
                }
            }

            foreach (var edition in resources.Editions?.Entries ?? new List<Resources.OpenLibraryEditionResource>())
            {
                foreach (var coverId in edition.Covers ?? new List<int>())
                {
                    if (coverId > 0 && seen.Add(coverId))
                    {
                        candidates.Add(new CoverCandidate
                        {
                            CoverId = coverId,
                            Url = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg",
                            Source = "edition",
                            EditionTitle = edition.Title,
                            PublishDate = edition.PublishDate,
                            Publisher = edition.Publishers?.FirstOrDefault()
                        });
                    }
                }
            }

            return candidates;
        }

        // Cache the raw HTTP resources only (work + editions). The mapper
        // runs on every caller so each gets a fresh Book + Editions list:
        // LazyCache hands back the same object reference on hit, and
        // BookService.AddBook + BasicRepository.Insert mutate the
        // edition objects in place (Id via reflection, BookId via
        // ForEach, Monitored toggle) — so a cached *mapped* payload
        // would leak those mutations across calls. After a first add,
        // editions in cache would carry Ids that no longer correspond
        // to any DB row, and the retry path's SetMonitored assertion
        // would fire with Count(Monitored)==0.
        private (Resources.OpenLibraryWorkResource Work, Resources.OpenLibraryEditionListResource Editions) FetchWorkBundle(string id)
        {
            return Cached(true, $"ow_{id}", TimeSpan.FromDays(7), () =>
            {
                var workReq = _requestBuilder.For($"works/{id}.json").Build();
                var work = Send<Resources.OpenLibraryWorkResource>(workReq)?.Resource;
                if (work == null)
                {
                    throw new OpenLibraryException("OL work not found: {0}", id);
                }

                // 50 → 200 so works with long edition lists carry more
                // cover-bearing candidates into SelectPrimaryEdition's
                // tiered preference (English+ISBN13+cover →
                // English+cover → any cover → ...). Latent before now —
                // Rowling's books all had covers in the first 50 — but
                // surfaced by the Le Guin investigation (LHoD has 91
                // editions; cover candidates can plausibly cluster in
                // the tail of works with audiobook/foreign reprints).
                var editionsReq = _requestBuilder.For($"works/{id}/editions.json?limit=200").Build();
                var editionsRes = Send<Resources.OpenLibraryEditionListResource>(editionsReq)?.Resource;

                return (Work: work, Editions: editionsRes);
            });
        }

        public List<Author> SearchForNewAuthor(string title)
        {
            // An OL author id is not findable through OL's own author
            // search: q=OL79043A and q=author:OL79043A both come back with
            // numFound 0. That matters because /api/v1/author/lookup routes
            // here, and the Library Import wizard is its only caller -- so
            // when the wizard picked the wrong author there was no way to
            // correct it beyond retyping the name and hoping for a better
            // ranking. Pasting the id off the author's Open Library page
            // is the obvious move and it silently returned nothing.
            //
            // SearchForNewEntity has honoured `author:` since the OL
            // cutover, but that is the Add Author page's route, not this
            // one. Resolve the id forms here directly.
            var (term, authorId) = ParseAuthorSearchTerm(title);

            if (authorId != null)
            {
                try
                {
                    var author = GetAuthorInfo(authorId);
                    return author != null ? new List<Author> { author } : new List<Author>();
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    // A well-formed id that OL does not have. An empty
                    // result is the honest answer; falling back to a name
                    // search on the id string would only ever return noise.
                    return new List<Author>();
                }
            }

            // OL's `/search/*.json` returns 422 UnprocessableEntity for
            // single-char queries (e.g. when the user is mid-typing and
            // the frontend's onChange fires a search on each keystroke).
            // Short-circuit before the HTTP call to avoid both the
            // wasted round-trip and the [Fatal] error pipeline log.
            if (term.Length < 2)
            {
                return new List<Author>();
            }

            return Cached(true, $"osa_{term}", TimeSpan.FromHours(1), () =>
            {
                var req = _requestBuilder.For($"search/authors.json?q={Uri.EscapeDataString(term)}").Build();
                req.SuppressHttpError = false;
                req.SuppressHttpErrorStatusCodes = new[] { HttpStatusCode.UnprocessableEntity };
                req.LogHttpError = false;

                HttpResponse<OpenLibraryAuthorSearchResource> resp;
                try
                {
                    resp = Send<OpenLibraryAuthorSearchResource>(req);
                }
                catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    return new List<Author>();
                }

                return OpenLibrarySearchMapper.ReRankAndMapAuthors(resp?.Resource, term);
            });
        }

        // "OL does not have this record", as opposed to "the lookup failed".
        //
        // OL answers an unknown but well-formed id with a 404, and Send()
        // surfaces that as an HttpException, not an OpenLibraryException --
        // so a catch on OpenLibraryException alone lets it escape and the
        // API returns a 500 with a stack trace. Pasting an id with a typo
        // in it is an ordinary thing for a user to do and deserves "no
        // results", not an error page.
        //
        // Deliberately narrow: a 429 or a 5xx or a transport failure is a
        // real fault and has to keep propagating, or a rate-limited lookup
        // becomes indistinguishable from an author who does not exist.
        private static bool IsNotFound(Exception ex)
            => ex is OpenLibraryException ||
               (ex is HttpException http && http.Response?.StatusCode == HttpStatusCode.NotFound);

        // Splits a raw author-search term into (term to search by name,
        // OL author id to fetch directly). Exactly one of the two is
        // meaningful: a non-null id means skip the name search entirely.
        //
        // Accepts a bare id ("OL79043A") as well as the prefixed form
        // ("author:OL79043A") that SearchForNewEntity already understands.
        // An `author:` prefix in front of something that is not an id --
        // "author:le guin" -- keeps the prefix stripped and searches for
        // the rest, because a user who types it plainly means "search
        // authors for this", and passing the literal string through to OL
        // matches nothing.
        internal static (string Term, string AuthorId) ParseAuthorSearchTerm(string title)
        {
            var term = title?.Trim() ?? string.Empty;

            var colon = term.IndexOf(':');
            if (colon >= 0 &&
                term.Substring(0, colon).Trim().Equals("author", StringComparison.OrdinalIgnoreCase))
            {
                term = term.Substring(colon + 1).Trim();
            }

            // Upper-cased because OL's author endpoint is case-sensitive:
            // /authors/OL79043A.json is a 200 and /authors/ol79043a.json is
            // a 404. An id is OL + digits + A, so upper-casing it is lossless.
            return OpenLibraryIdHelper.IsAuthorId(term)
                ? (term, term.ToUpperInvariant())
                : (term, null);
        }

        public List<Book> SearchForNewBook(string title, string author, bool getAllEditions = true)
        {
            // OL's /search.json 422s on single-char queries — short-circuit
            // before the HTTP call to avoid both the wasted round-trip and
            // the [Fatal] error pipeline log when the frontend fires search
            // on every keystroke.
            if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 2)
            {
                return new List<Book>();
            }

            var cacheKey = $"os_{title}|{author}|{getAllEditions}";

            return Cached(true, cacheKey, TimeSpan.FromHours(1), () =>
            {
                string qs;
                if (string.IsNullOrWhiteSpace(author))
                {
                    // Global search-bar path: the user typed something that
                    // might be a title, an author, or a series. OL's `q=`
                    // does proper relevance scoring across all indexed
                    // fields and ranks canonical works first. `?title=`
                    // over-matches on compilation/omnibus works whose
                    // titles contain the author name (e.g. "Brandon
                    // Sanderson Sampler"), which mostly lack covers.
                    qs = $"?q={Uri.EscapeDataString(title)}";
                }
                else
                {
                    // Targeted add-book / reidentify path: caller has
                    // disambiguated by author already, exact title+author
                    // is the right shape.
                    qs = $"?title={Uri.EscapeDataString(title)}&author={Uri.EscapeDataString(author)}";
                }

                qs += "&limit=20&fields=key,title,author_name,author_key,first_publish_year,isbn,cover_i,edition_count";

                var req = _requestBuilder.For($"search.json{qs}").Build();
                req.SuppressHttpErrorStatusCodes = new[] { HttpStatusCode.UnprocessableEntity };
                req.LogHttpError = false;

                HttpResponse<OpenLibrarySearchResource> resp;
                try
                {
                    resp = Send<OpenLibrarySearchResource>(req);
                }
                catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    return new List<Book>();
                }

                return OpenLibrarySearchMapper.ReRankAndMap(resp?.Resource, title, author);
            });
        }

        public List<Book> SearchByIsbn(string isbn)
        {
            var books = Cached(true, $"oisbn_{isbn}", TimeSpan.FromDays(30), () =>
            {
                var request = _requestBuilder.For($"isbn/{isbn}.json").Build();
                request.AllowAutoRedirect = true;

                var resp = Send<OpenLibraryEditionResource>(request);
                if (resp?.Resource == null)
                {
                    return new List<Book>();
                }

                // The queried ISBN travels with the mapping: when the edition
                // record lists no isbn_13 (or several isbn_10 printings), the
                // mapper needs it to derive/select the right one — issue #10.
                return new List<Book> { OpenLibraryEditionMapper.ToBook(resp.Resource, isbn) };
            });

            return WithAuthorNames(books);
        }

        // The /isbn/ and /books/ endpoints return author keys but no names, and
        // an authorless candidate is scored as maximum author-distance rather
        // than "unknown" — a fixed 0.1875 penalty against a 0.20 accept gate,
        // which leaves no room for any other imperfection (issue #7).
        //
        // Deliberately called *outside* the callers' Cached(...) factories.
        // Resolving inside one would store whatever came back, so a single OL
        // 5xx or exhausted 429 would persist a nameless book for the full 30
        // days — turning a comfortable match into a borderline one long after
        // OL recovered. Out here, a failure simply means the next call retries.
        //
        // The write lands on the cached Book instance, so a later cache hit
        // gets the name for free. That instance is already shared (see the note
        // on GetAuthorInfo below); this particular mutation is idempotent and
        // only ever goes empty -> resolved.
        private List<Book> WithAuthorNames(List<Book> books)
        {
            foreach (var book in books ?? new List<Book>())
            {
                var metadata = book?.AuthorMetadata?.Value;

                // No author key on the edition at all. OpenLibrary keeps authors
                // on the *work*, and an edition record repeating them is common
                // but not guaranteed — 1984 and Sapiens in the captured corpus
                // carry none, so ToBook attaches no AuthorMetadata and the
                // candidate would otherwise score as authorless (the full
                // 0.1875 #7 was meant to remove). Reach through the work for the
                // author key, then resolve the name the same way. Issue #9.
                //
                // Only the authorless case pays for this — the 3-of-5 that
                // already carry an edition author key skip it — so the case #7
                // fixed gains no round trip.
                if (metadata == null || metadata.ForeignAuthorId.IsNullOrWhiteSpace())
                {
                    var workAuthorId = GetAuthorIdFromWork(book?.ForeignBookId);
                    var workAuthorName = GetAuthorName(workAuthorId);

                    if (workAuthorName.IsNotNullOrWhiteSpace())
                    {
                        AttachAuthor(book, workAuthorId, workAuthorName);
                    }

                    continue;
                }

                if (metadata.Name.IsNotNullOrWhiteSpace())
                {
                    continue;
                }

                var name = GetAuthorName(metadata.ForeignAuthorId);

                if (name.IsNullOrWhiteSpace())
                {
                    continue;
                }

                metadata.Name = name;

                if (book.Author?.Value != null)
                {
                    book.Author.Value.CleanName = Parser.Parser.CleanAuthorName(name);
                }
            }

            return books;
        }

        // Attach a freshly-resolved author to a book that reached ToBook with no
        // author key — mirrors the AuthorMetadata/Author pair ToBook builds when
        // the edition *does* carry one, so downstream (the Book.AuthorMetadataId
        // join, DistanceCalculator's clean-name compare) sees the same shape.
        private static void AttachAuthor(Book book, string foreignAuthorId, string name)
        {
            var metadata = new AuthorMetadata
            {
                ForeignAuthorId = foreignAuthorId,
                TitleSlug = foreignAuthorId,
                Name = name
            };

            book.AuthorMetadata = metadata;
            book.Author = new Author
            {
                Metadata = metadata,
                CleanName = Parser.Parser.CleanAuthorName(name)
            };
        }

        // Issue #9. The primary author key hangs on the work, not the edition, so
        // fetch just the work document (not its edition list — GetBookInfo's
        // heavier bundle is for callers that need the editions too) and read
        // authors[0]. Caches the resolved key only, for the same reason
        // GetAuthorName does: LazyCache would pin a faulted factory for the whole
        // TTL, re-poisoning exactly the transient-failure case #7 took pains to
        // survive. A key we never got is indistinguishable from one we never
        // asked for, and both retry next time.
        public string GetAuthorIdFromWork(string foreignBookId)
        {
            if (foreignBookId.IsNullOrWhiteSpace() ||
                !foreignBookId.EndsWith("W", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var cacheKey = $"owa_{foreignBookId}";
            var cached = _cache.Get<string>(cacheKey);

            if (cached.IsNotNullOrWhiteSpace())
            {
                return cached;
            }

            try
            {
                var req = _requestBuilder.For($"works/{foreignBookId}.json").Build();
                var work = Send<Resources.OpenLibraryWorkResource>(req)?.Resource;
                var authorKey = work?.Authors?.FirstOrDefault()?.Author?.Key;
                var authorId = ExtractOlid(authorKey);

                if (authorId.IsNotNullOrWhiteSpace())
                {
                    _cache.Add(cacheKey, authorId, DateTimeOffset.UtcNow.AddDays(7));
                }

                return authorId;
            }
            catch (Exception ex) when (ex is NzbDroneException or HttpException)
            {
                _logger.Debug(ex, "Could not resolve an author key from work {0}; its edition will score as authorless", foreignBookId);
                return null;
            }
        }

        // OL keys are "/authors/OL...A", "/works/OL...W"; downstream wants the
        // bare OLID. Last path segment, empty guarded.
        private static string ExtractOlid(string key)
        {
            if (key.IsNullOrWhiteSpace())
            {
                return null;
            }

            var slash = key.LastIndexOf('/');
            return slash >= 0 ? key.Substring(slash + 1) : key;
        }

        // Just the display name. GetAuthorInfo would also produce it, but it
        // additionally fetches authors/{id}/works.json?limit=1000 and re-maps
        // the entire discography on every call — a works-list round trip per
        // distinct author during a large import, to populate one string.
        //
        // Best-effort by design: a candidate that cannot be named is still a
        // usable candidate, it just scores as authorless.
        public string GetAuthorName(string foreignAuthorId)
        {
            if (foreignAuthorId.IsNullOrWhiteSpace())
            {
                return null;
            }

            // Not Cached(): LazyCache stores the faulted factory, so a lookup that
            // threw would keep throwing for the full TTL — the same
            // cache-the-failure bug this method exists to avoid, just with a
            // shorter fuse. Cache hits only. A name we never got is
            // indistinguishable from one we never asked for, and both retry.
            var cacheKey = $"oan_{foreignAuthorId}";
            var cached = _cache.Get<string>(cacheKey);

            if (cached.IsNotNullOrWhiteSpace())
            {
                return cached;
            }

            try
            {
                var req = _requestBuilder.For($"authors/{foreignAuthorId}.json").Build();
                var name = Send<OpenLibraryAuthorResource>(req)?.Resource?.Name;

                if (name.IsNotNullOrWhiteSpace())
                {
                    _cache.Add(cacheKey, name, DateTimeOffset.UtcNow.AddHours(24));
                }

                return name;
            }
            catch (Exception ex) when (ex is NzbDroneException or HttpException)
            {
                _logger.Debug(ex, "Could not resolve a display name for author {0}; its books will score as authorless", foreignAuthorId);
                return null;
            }
        }

        public List<Book> SearchByAsin(string asin)
        {
            return Cached(true, $"oasin_{asin}", TimeSpan.FromDays(30), () =>
            {
                var req = _requestBuilder.For($"search.json?q=identifier%3A{Uri.EscapeDataString(asin)}&limit=5").Build();
                var resp = Send<OpenLibrarySearchResource>(req);
                return OpenLibrarySearchMapper.ReRankAndMap(resp?.Resource, asin, null);
            });
        }

        public List<Book> SearchByForeignBookId(string foreignBookId, bool getAllEditions)
        {
            if (string.IsNullOrWhiteSpace(foreignBookId))
            {
                return new List<Book>();
            }

            if (foreignBookId.EndsWith("W", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (_, book, _) = GetBookInfo(foreignBookId);
                    return new List<Book> { book };
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    return new List<Book>();
                }
            }

            if (foreignBookId.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                // Same 404-is-not-an-error handling as the work branch above
                // and as SearchForNewAuthor: `edition:OL9999999999M` in the
                // search box is a typo, not a server fault.
                try
                {
                    var books = Cached(true, $"oe_{foreignBookId}", TimeSpan.FromDays(30), () =>
                    {
                        var resp = Send<OpenLibraryEditionResource>(_requestBuilder.For($"books/{foreignBookId}.json").Build());
                        if (resp?.Resource == null)
                        {
                            return new List<Book>();
                        }

                        return new List<Book> { OpenLibraryEditionMapper.ToBook(resp.Resource) };
                    });

                    return WithAuthorNames(books);
                }
                catch (Exception ex) when (IsNotFound(ex))
                {
                    return new List<Book>();
                }
            }

            return new List<Book>();
        }

        public List<object> SearchForNewEntity(string title)
        {
            // Typed-prefix search shortcuts mirroring the BookInfoProxy
            // syntax (`isbn:` / `asin:` / `author:` / `work:` /
            // `edition:`), updated for OL's identifier shape:
            //   author:OL1394865A  → /authors/{key}.json single result
            //   work:OL26421189W   → /works/{key}.json   single result
            //   edition:OL49282196M → /books/{key}.json  single result
            //   isbn:067003469X    → /isbn/{value}.json  ISBN lookup
            //   asin:B00JCDK5ME    → search.json?q=identifier:{asin}
            //
            // Unknown prefixes (and prefix-less queries) fall through to
            // the existing author + book merged search.
            var prefixed = TryPrefixedSearch(title);
            if (prefixed != null)
            {
                return prefixed;
            }

            // Two layers of dedup for authors:
            //   seenAuthorIds   — drops duplicate OLIDs (cheap)
            //   authorByCleanName — drops duplicate *people*. OL frequently
            //     has multiple author OLIDs for the same person spelled
            //     three different ways ("J. K. Rowling", "J.K. Rowling",
            //     "J.k. Rowling" → OL23919A, OL16230142A, OL16034707A).
            //     The book-search-synthesized candidate is preferred when
            //     a duplicate is detected, because OL's book index links
            //     to the *canonical* author OLID (the one that actually has
            //     works attached) rather than the stub records that pollute
            //     /search/authors.json. CleanName normalization (no spaces,
            //     no dots, lowercase) is what Parser.CleanAuthorName already
            //     produces, so reuse it.
            var result = new List<object>();
            var seenAuthorIds = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var authorByCleanName = new Dictionary<string, Author>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var author in SearchForNewAuthor(title))
            {
                if (author.Metadata?.Value?.ForeignAuthorId != null)
                {
                    seenAuthorIds.Add(author.Metadata.Value.ForeignAuthorId);
                }

                var cleanName = author.CleanName;
                if (!string.IsNullOrWhiteSpace(cleanName))
                {
                    // First write wins for /search/authors.json hits — they
                    // arrive already re-ranked by SearchForNewAuthor, so the
                    // first one under a given CleanName is the best-scoring
                    // record rather than whichever OL happened to list first.
                    // We may still overwrite with a book-synthesized hit later.
                    if (!authorByCleanName.ContainsKey(cleanName))
                    {
                        authorByCleanName[cleanName] = author;
                    }
                }
                else
                {
                    // No clean name (shouldn't happen, but be safe) —
                    // surface as-is.
                    result.Add(author);
                }
            }

            var books = SearchForNewBook(title, null);

            // Synthesize Author tiles from the books' author metadata.
            // OL's book index links to the canonical author OLID for each
            // work, so a synthesized author is *always* the preferred
            // entry when its CleanName collides with a stub from
            // /search/authors.json — overwrite. Don't gate on
            // seenAuthorIds here: a canonical OLID can legitimately
            // appear in BOTH /search/authors.json and the book index
            // (it just happens to have a J.K. Rowling stub spelled
            // three different ways in the author search), and we want
            // the book-derived entry to win the name-collision.
            foreach (var book in books)
            {
                var meta = book?.AuthorMetadata?.Value;
                if (meta == null || string.IsNullOrWhiteSpace(meta.ForeignAuthorId))
                {
                    continue;
                }

                var cleanName = Parser.Parser.CleanAuthorName(meta.Name);

                // Already synthesized for this exact OLID under this
                // CleanName — skip the duplicate (every other book by
                // the same author would otherwise re-run the work).
                if (!string.IsNullOrWhiteSpace(cleanName)
                    && authorByCleanName.TryGetValue(cleanName, out var existing)
                    && existing.Metadata?.Value?.ForeignAuthorId == meta.ForeignAuthorId)
                {
                    continue;
                }

                var synthesizedAuthor = new Author
                {
                    Metadata = new AuthorMetadata
                    {
                        ForeignAuthorId = meta.ForeignAuthorId,
                        TitleSlug = meta.TitleSlug ?? meta.ForeignAuthorId,
                        Name = meta.Name,
                        Images = OpenLibraryCoverUrls.ForAuthorByOlid(meta.ForeignAuthorId)
                    },
                    CleanName = cleanName
                };

                if (!string.IsNullOrWhiteSpace(cleanName))
                {
                    authorByCleanName[cleanName] = synthesizedAuthor;
                }
                else
                {
                    result.Add(synthesizedAuthor);
                }

                seenAuthorIds.Add(meta.ForeignAuthorId);
            }

            foreach (var author in authorByCleanName.Values)
            {
                result.Add(author);
                if (result.Count >= 20)
                {
                    break;
                }
            }

            foreach (var book in books)
            {
                result.Add(book);
                if (result.Count >= 40)
                {
                    break;
                }
            }

            return result;
        }

        private List<object> TryPrefixedSearch(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || !title.Contains(':'))
            {
                return null;
            }

            // Lower-case the prefix only — OL identifiers (OL...W, OL...M,
            // OL...A) preserve their original casing on the right-hand
            // side. ISBNs / ASINs are case-insensitive in practice, but
            // we pass them through unchanged so the proxy methods see
            // exactly what the user typed.
            var split = title.Split(new[] { ':' }, 2);
            if (split.Length != 2)
            {
                return null;
            }

            var prefix = split[0].Trim().ToLowerInvariant();
            var slug = split[1].Trim();

            if (string.IsNullOrWhiteSpace(slug) || slug.Any(char.IsWhiteSpace))
            {
                return null;
            }

            switch (prefix)
            {
                case "isbn":
                    return SearchByIsbn(slug).Cast<object>().ToList();
                case "asin":
                    return SearchByAsin(slug).Cast<object>().ToList();
                case "work":
                case "edition":
                    // SearchByForeignBookId routes by the suffix letter
                    // (W → work, M → edition), so both prefixes share it.
                    return SearchByForeignBookId(slug, true).Cast<object>().ToList();
                case "author":
                    // Delegates so both search routes agree on what
                    // `author:` means. It used to call GetAuthorInfo on the
                    // slug whatever the slug was, so `author:Tolkien` was a
                    // 404 and an empty result; SearchForNewAuthor resolves
                    // an id directly and falls back to a name search for
                    // anything else. (A slug with a space never reaches
                    // this switch -- the whitespace guard above returns
                    // null and the merged search handles it.)
                    return SearchForNewAuthor(title).Cast<object>().ToList();

                default:
                    return null;
            }
        }

        private T Cached<T>(bool useCache, string cacheKey, TimeSpan ttl, Func<T> factory)
        {
            if (!useCache)
            {
                return factory();
            }

            return _cache.GetOrAdd(cacheKey, () => factory(), DateTimeOffset.UtcNow.Add(ttl));
        }

        // Inline retry loop for OL transient failures. 429 + 5xx → wait
        // (2s, 4s, 8s) + jitter, max 3 retries. Honors the Retry-After
        // header when OL provides one (common on 429).
        //
        // Not using Polly: Polly 8's ResiliencePipeline<HttpResponse> doesn't
        // compose cleanly with the generic HttpResponse<T> here (covariance
        // round-trip via cast works at runtime but is ugly and brittle when
        // the upstream IHttpClient signature evolves). Worth a re-look once
        // more OL endpoints land — until then, the inline loop is plenty.
        // Every OL request funnels through here, which makes it the only place
        // that sees all the ways a request can fail — status-coded refusals and
        // the raw HttpRequestException/IOException a torn connection throws past
        // the retry loop. Recording availability anywhere higher up would miss
        // the network-level cases entirely: CandidateService's handlers filter
        // on `NzbDroneException or HttpException`, and those are neither.
        private HttpResponse<T> Send<T>(HttpRequest request)
            where T : new()
        {
            _statusService.EnsureAvailable();

            try
            {
                var response = SendCore<T>(request);

                // Any answer at all means the source is reachable.
                _statusService.RecordReachable();

                return response;
            }
            catch (Exception ex)
            {
                // The status service classifies: a 404 counts as contact, a
                // retry-exhausted 429/5xx or a torn connection counts as a
                // refusal.
                _statusService.RecordFailure(ex);

                throw;
            }
        }

        private HttpResponse<T> SendCore<T>(HttpRequest request)
            where T : new()
        {
            var delay = InitialRetryDelay;
            HttpResponse<T> response = null;

            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    response = _httpClient.Get<T>(request);
                }
                catch (HttpException ex) when (ex.Response != null && IsRetryable(ex.Response) && attempt < MaxRetries)
                {
                    _logger.Warn(ex, "OL request {0} threw on attempt {1}; retrying after {2}s", request.Url, attempt + 1, delay.TotalSeconds);
                    Wait(ex.Response, delay);
                    delay = NextDelay(delay);
                    continue;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex) && attempt < MaxRetries)
                {
                    // HTTP/2 stream resets, mid-response IOException, request
                    // timeouts. The original HttpException catch above only
                    // covers status-coded failures (429/5xx) — torn streams
                    // never reach the response phase, so they escape as raw
                    // HttpRequestException/IOException and previously
                    // aborted entire author refreshes mid-flight. Retry with
                    // the same backoff schedule as the status-coded path.
                    _logger.Warn(ex, "OL request {0} hit transient network error on attempt {1}; retrying after {2}s", request.Url, attempt + 1, delay.TotalSeconds);
                    Thread.Sleep(delay);
                    delay = NextDelay(delay);
                    continue;
                }

                if (response != null && IsRetryable(response) && attempt < MaxRetries)
                {
                    _logger.Warn("OL request {0} returned {1} on attempt {2}; retrying after {3}s", request.Url, response.StatusCode, attempt + 1, delay.TotalSeconds);
                    Wait(response, delay);
                    delay = NextDelay(delay);
                    continue;
                }

                return response;
            }

            return response;
        }

        private static bool IsRetryable(HttpResponse response)
        {
            if (response == null)
            {
                return false;
            }

            return response.StatusCode == HttpStatusCode.TooManyRequests || response.HasHttpServerError;
        }

        private static bool IsTransientNetworkError(Exception ex)
        {
            return ex is System.Net.Http.HttpRequestException
                || ex is System.IO.IOException
                || ex is System.Threading.Tasks.TaskCanceledException;
        }

        private static void Wait(HttpResponse response, TimeSpan fallback)
        {
            var retryAfter = response?.Headers?.GetSingleValue("Retry-After");
            if (!string.IsNullOrEmpty(retryAfter) && int.TryParse(retryAfter, out var seconds))
            {
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                return;
            }

            Thread.Sleep(fallback);
        }

        private static TimeSpan NextDelay(TimeSpan current)
        {
            // Exponential * (0.8–1.2) jitter. Random instance avoided to keep
            // the helper deterministic-ish in tests.
            var ms = (long)(current.TotalMilliseconds * 2);
            var jittered = ms + ((ms / 5) * ((DateTime.UtcNow.Ticks % 3) - 1));
            return TimeSpan.FromMilliseconds(Math.Max(jittered, 1));
        }
    }
}
