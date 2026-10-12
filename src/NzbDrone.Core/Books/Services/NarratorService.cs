using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    public interface INarratorService
    {
        // Replaces the full Narrator/EditionNarrator set for an edition.
        // Names are dedup'd by case-insensitive CleanName; existing
        // Narrator rows are reused, new ones are inserted. Idempotent —
        // calling twice with the same names is a no-op after the first
        // write.
        void SetNarratorsForEdition(int editionId, IEnumerable<string> orderedNames);

        // Reads the Narrator set for an edition in credited-billing
        // order (the EditionNarrators.Order column).
        List<Narrator> GetNarratorsForEdition(int editionId);

        // Single-row lookup, used by the V1 narrator endpoint.
        // Returns null when no narrator with that id exists.
        Narrator GetById(int narratorId);

        // Phase 12.4: distinct books this narrator has narrated, derived
        // by walking EditionNarrators → Editions → Books. Returns empty
        // when the narrator has no edition links.
        List<Book> GetBooksForNarrator(int narratorId);
    }

    public class NarratorService : INarratorService
    {
        private readonly INarratorRepository _narratorRepo;
        private readonly IEditionNarratorRepository _editionNarratorRepo;
        private readonly IEditionService _editionService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public NarratorService(
            INarratorRepository narratorRepo,
            IEditionNarratorRepository editionNarratorRepo,
            IEditionService editionService,
            IBookService bookService,
            Logger logger)
        {
            _narratorRepo = narratorRepo;
            _editionNarratorRepo = editionNarratorRepo;
            _editionService = editionService;
            _bookService = bookService;
            _logger = logger;
        }

        public void SetNarratorsForEdition(int editionId, IEnumerable<string> orderedNames)
        {
            if (editionId <= 0)
            {
                return;
            }

            // Filter + normalize the incoming list. The dedup is by CleanName
            // so "George Guidall" and "george guidall" collapse to one row.
            var normalized = (orderedNames ?? Enumerable.Empty<string>())
                .Where(n => n.IsNotNullOrWhiteSpace())
                .Select(n => new
                {
                    Name = n.Trim(),
                    CleanName = Parser.Parser.CleanAuthorName(n.Trim())
                })
                .GroupBy(n => n.CleanName)
                .Select(g => g.First())
                .ToList();

            _editionNarratorRepo.DeleteByEditionId(editionId);

            if (normalized.Count == 0)
            {
                return;
            }

            var joinRows = new List<EditionNarrator>(normalized.Count);
            var order = 0;

            foreach (var entry in normalized)
            {
                var narrator = _narratorRepo.FindByCleanName(entry.CleanName);
                if (narrator == null)
                {
                    narrator = _narratorRepo.Insert(new Narrator
                    {
                        Name = entry.Name,
                        CleanName = entry.CleanName
                    });
                }

                joinRows.Add(new EditionNarrator
                {
                    EditionId = editionId,
                    NarratorId = narrator.Id,
                    Order = order++
                });
            }

            _editionNarratorRepo.InsertMany(joinRows);

            _logger.Trace("Set {0} narrator(s) for edition {1}", normalized.Count, editionId);
        }

        public Narrator GetById(int narratorId)
        {
            if (narratorId <= 0)
            {
                return null;
            }

            return _narratorRepo.Find(narratorId);
        }

        public List<Book> GetBooksForNarrator(int narratorId)
        {
            if (narratorId <= 0)
            {
                return new List<Book>();
            }

            var joinRows = _editionNarratorRepo.FindByNarratorId(narratorId);
            if (joinRows.Count == 0)
            {
                return new List<Book>();
            }

            // Walk EditionNarrator → Edition → Book. One narrator can
            // appear on multiple editions of the same book (e.g. an
            // abridged + unabridged audiobook), so dedup by BookId.
            // FindEditions is one query for the whole set and returns no
            // row for an id whose edition is gone, so a join row that
            // outlived its edition is skipped (GetEdition would throw).
            var editionIds = joinRows.Select(r => r.EditionId).Distinct().ToList();
            var bookIds = _editionService.FindEditions(editionIds)
                .Select(e => e.BookId)
                .Distinct()
                .ToList();

            if (bookIds.Count == 0)
            {
                return new List<Book>();
            }

            return _bookService.GetBooks(bookIds);
        }

        public List<Narrator> GetNarratorsForEdition(int editionId)
        {
            var joinRows = _editionNarratorRepo.FindByEditionId(editionId)
                .OrderBy(r => r.Order)
                .ToList();

            if (joinRows.Count == 0)
            {
                return new List<Narrator>();
            }

            // Re-sort the unordered repo result back into credited-billing
            // order using the join rows as the canonical sequence.
            var byId = _narratorRepo.FindForEdition(editionId).ToDictionary(n => n.Id);
            return joinRows
                .Select(r => byId.TryGetValue(r.NarratorId, out var n) ? n : null)
                .Where(n => n != null)
                .ToList();
        }
    }
}
