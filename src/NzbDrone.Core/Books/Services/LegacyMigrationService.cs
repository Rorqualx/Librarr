using System;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource.OpenLibrary;

namespace NzbDrone.Core.Books
{
    // First-boot migration of a Goodreads/BookInfo-shaped imported DB onto
    // OpenLibrary. Fires once on ApplicationStartedEvent, populates the
    // BookIdMapping table by enqueuing ReidentifyLibraryCommand, and
    // protects the user from the "refresh-after-cutover" surprise where
    // OL discovers hundreds of new works for each legacy author (Joseph
    // Conrad ballooned 1 → 1001 books on us pre-mitigation).
    //
    // The pipeline:
    //   1. Read LegacyMigrationCompleted marker — skip if set.
    //   2. If no authors exist → fresh install, set marker, done.
    //   3. If every author's ForeignAuthorId is OL-shaped → already migrated,
    //      set marker, done.
    //   4. If BookIdMapping already has rows and this service never started
    //      a run (user ran reidentify by hand pre-this-feature) → preserve
    //      their state, set marker, done.
    //   5. Otherwise: record that the migration has started, flip every
    //      author whose MonitorNewItems == All to None (preserves explicit
    //      New/None choices), then enqueue ReidentifyLibraryCommand. Set
    //      marker when the command completes.
    //
    // Step 5 leaves the marker unset if the command fails, which it does
    // when any lookup could not reach the metadata source, and if the app
    // stops before the command finishes. Either way the next startup runs
    // step 5 again; reidentify skips what is already mapped, so that
    // resumes rather than restarts. The body runs on a background Task so
    // we don't block the rest of ApplicationStartedEvent fan-out.
    public class LegacyMigrationService : IHandleAsync<ApplicationStartedEvent>, IHandle<CommandExecutedEvent>
    {
        public const int CurrentVersion = 1;

        private readonly IConfigService _configService;
        private readonly IAuthorService _authorService;
        private readonly IBookIdMappingRepository _mappingRepo;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;

        private int _pendingCommandId;

        public LegacyMigrationService(IConfigService configService,
                                      IAuthorService authorService,
                                      IBookIdMappingRepository mappingRepo,
                                      IManageCommandQueue commandQueue,
                                      Logger logger)
        {
            _configService = configService;
            _authorService = authorService;
            _mappingRepo = mappingRepo;
            _commandQueue = commandQueue;
            _logger = logger;
        }

        public void HandleAsync(ApplicationStartedEvent message)
        {
            // Off-thread so we don't gate other startup handlers behind
            // our DB reads + command enqueue. Wrap in try/catch so a
            // migration crash never prevents the app from starting.
            Task.Run(() =>
            {
                try
                {
                    RunMigration();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "LegacyMigrationService failed; marker left unset, will retry on next startup");
                }
            });
        }

        public void Handle(CommandExecutedEvent message)
        {
            if (_pendingCommandId == 0 || message?.Command == null || message.Command.Id != _pendingCommandId)
            {
                return;
            }

            if (message.Command.Status == CommandStatus.Completed)
            {
                _configService.LegacyMigrationCompleted = true;
                _configService.LegacyMigrationVersion = CurrentVersion;
                _logger.Info("LegacyMigrationService: marker set (version {0})", CurrentVersion);
            }
            else
            {
                _logger.Warn("LegacyMigrationService: ReidentifyLibraryCommand finished with Status={0}; marker left unset, will retry next startup", message.Command.Status);
            }

            _pendingCommandId = 0;
        }

        internal void RunMigration()
        {
            if (_configService.LegacyMigrationCompleted && _configService.LegacyMigrationVersion >= CurrentVersion)
            {
                _logger.Debug("LegacyMigrationService: marker already set at version {0}, skipping", _configService.LegacyMigrationVersion);
                return;
            }

            var authors = _authorService.GetAllAuthors();

            if (authors.Count == 0)
            {
                _logger.Info("LegacyMigrationService: no authors present, marking migration complete (fresh install)");
                _configService.LegacyMigrationCompleted = true;
                _configService.LegacyMigrationVersion = CurrentVersion;
                return;
            }

            var legacyAuthors = authors
                .Where(a => !OpenLibraryIdHelper.IsAuthorId(a.Metadata?.Value?.ForeignAuthorId))
                .ToList();

            if (legacyAuthors.Count == 0)
            {
                _logger.Info("LegacyMigrationService: all {0} authors already on OL shape, marking complete", authors.Count);
                _configService.LegacyMigrationCompleted = true;
                _configService.LegacyMigrationVersion = CurrentVersion;
                return;
            }

            // User ran reidentify by hand pre-this-feature. Don't touch their
            // MonitorNewItems state — they may have already fine-tuned it.
            // Just set the marker so we stop checking. Rows left behind by
            // our own interrupted or failed run are not that case: there
            // the migration is unfinished and must carry on below.
            if (!_configService.LegacyMigrationStarted && _mappingRepo.All().Any())
            {
                _logger.Info("LegacyMigrationService: BookIdMapping has rows ({0} legacy authors), assuming prior manual reidentify; marking complete without further action", legacyAuthors.Count);
                _configService.LegacyMigrationCompleted = true;
                _configService.LegacyMigrationVersion = CurrentVersion;
                return;
            }

            if (_configService.LegacyMigrationStarted)
            {
                // The MonitorNewItems flip already ran in full before the
                // flag was set. Repeating it here would undo anything the
                // user has switched back to All since.
                _logger.Info("LegacyMigrationService: {0} legacy authors detected, resuming unfinished migration", legacyAuthors.Count);
            }
            else
            {
                _logger.Info("LegacyMigrationService: {0} legacy authors detected, beginning migration", legacyAuthors.Count);

                var toFlip = legacyAuthors
                    .Where(a => a.MonitorNewItems == NewItemMonitorTypes.All)
                    .ToList();

                if (toFlip.Count > 0)
                {
                    foreach (var a in toFlip)
                    {
                        // Use UpdateAuthor (not UpdateAuthors) so we don't trip
                        // the AuthorPathBuilder side-effect in the bulk path —
                        // a flip-monitor operation must not rewrite folder
                        // paths even when the naming format has drifted.
                        a.MonitorNewItems = NewItemMonitorTypes.None;
                        _authorService.UpdateAuthor(a);
                    }

                    _logger.Info("LegacyMigrationService: flipped MonitorNewItems=All → None on {0} authors (preserves explicit New/None elsewhere)", toFlip.Count);
                }
                else
                {
                    _logger.Info("LegacyMigrationService: no authors with MonitorNewItems=All to flip");
                }

                _configService.LegacyMigrationStarted = true;
            }

            var queued = _commandQueue.Push(new ReidentifyLibraryCommand(), CommandPriority.High, CommandTrigger.Unspecified);
            _pendingCommandId = queued.Id;
            _logger.Info("LegacyMigrationService: enqueued ReidentifyLibraryCommand id={0}; marker will be set on completion", queued.Id);
        }
    }
}
