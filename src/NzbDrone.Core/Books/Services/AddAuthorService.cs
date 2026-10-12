using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Books
{
    public interface IAddAuthorService
    {
        Author AddAuthor(Author newAuthor, bool doRefresh = true);
        List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh = true);
        AddAuthorsResult ImportAuthors(List<Author> newAuthors, bool doRefresh = true);
    }

    public class AddAuthorService : IAddAuthorService
    {
        private readonly IAuthorService _authorService;
        private readonly IAuthorMetadataService _authorMetadataService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly IBuildFileNames _fileNameBuilder;
        private readonly IAddAuthorValidator _addAuthorValidator;
        private readonly Logger _logger;

        public AddAuthorService(IAuthorService authorService,
                                IAuthorMetadataService authorMetadataService,
                                IProvideAuthorInfo authorInfo,
                                IBuildFileNames fileNameBuilder,
                                IAddAuthorValidator addAuthorValidator,
                                Logger logger)
        {
            _authorService = authorService;
            _authorMetadataService = authorMetadataService;
            _authorInfo = authorInfo;
            _fileNameBuilder = fileNameBuilder;
            _addAuthorValidator = addAuthorValidator;
            _logger = logger;
        }

        public Author AddAuthor(Author newAuthor, bool doRefresh = true)
        {
            Ensure.That(newAuthor, () => newAuthor).IsNotNull();

            newAuthor = AddSkyhookData(newAuthor);
            newAuthor = SetPropertiesAndValidate(newAuthor);

            _logger.Info("Adding Author {0} Path: [{1}]", newAuthor, newAuthor.Path);

            // add metadata
            _authorMetadataService.Upsert(newAuthor.Metadata.Value);
            newAuthor.AuthorMetadataId = newAuthor.Metadata.Value.Id;

            // add the author itself
            return _authorService.AddAuthor(newAuthor, doRefresh);
        }

        public List<Author> AddAuthors(List<Author> newAuthors, bool doRefresh = true)
        {
            return ImportAuthors(newAuthors, doRefresh).Added;
        }

        public AddAuthorsResult ImportAuthors(List<Author> newAuthors, bool doRefresh = true)
        {
            var added = DateTime.UtcNow;
            var result = new AddAuthorsResult();
            var authorsToAdd = new List<Author>();
            var queuedIds = new HashSet<string>();

            foreach (var s in newAuthors)
            {
                var foreignAuthorId = s.Metadata.Value.ForeignAuthorId;

                if (foreignAuthorId.IsNullOrWhiteSpace())
                {
                    _logger.Warn("Skipping author {0}, it has no foreign author id", s.Metadata.Value.Name);
                    result.Skipped.Add(new SkippedAuthor { Author = s, Reason = AddAuthorSkipReason.Failed, Message = "An author ID is required" });
                    continue;
                }

                // Checked before the lookup so a row that is going to be skipped
                // anyway doesn't cost a metadata request. Without these checks the
                // metadata upsert / author insert below hits a unique constraint
                // and takes the whole batch down with it.
                if (ShouldSkip(s, foreignAuthorId, queuedIds, result))
                {
                    continue;
                }

                try
                {
                    var author = AddSkyhookData(s);

                    // The metadata source can answer with a different id than the
                    // one asked for, so the same checks apply to what came back.
                    var resolvedId = author.Metadata.Value.ForeignAuthorId;
                    if (resolvedId.IsNotNullOrWhiteSpace() && resolvedId != foreignAuthorId && ShouldSkip(s, resolvedId, queuedIds, result))
                    {
                        continue;
                    }

                    author = SetPropertiesAndValidate(author);
                    author.Added = added;
                    authorsToAdd.Add(author);

                    queuedIds.Add(foreignAuthorId);
                    if (resolvedId.IsNotNullOrWhiteSpace())
                    {
                        queuedIds.Add(resolvedId);
                    }
                }
                catch (Exception ex)
                {
                    // Catch Import Errors for now until we get things fixed up
                    _logger.Error(ex, "Failed to import id: {0} - {1}", foreignAuthorId, s.Metadata.Value.Name);

                    var message = ex is ValidationException validationException && validationException.Errors.Any()
                        ? string.Join("; ", validationException.Errors.Select(x => x.ErrorMessage))
                        : ex.Message;

                    result.Skipped.Add(new SkippedAuthor { Author = s, Reason = AddAuthorSkipReason.Failed, Message = message });
                }
            }

            // add metadata
            _authorMetadataService.UpsertMany(authorsToAdd.Select(x => x.Metadata.Value).ToList());
            authorsToAdd.ForEach(x => x.AuthorMetadataId = x.Metadata.Value.Id);

            result.Added = _authorService.AddAuthors(authorsToAdd, doRefresh);

            return result;
        }

        private bool ShouldSkip(Author requested, string foreignAuthorId, HashSet<string> queuedIds, AddAuthorsResult result)
        {
            if (queuedIds.Contains(foreignAuthorId))
            {
                _logger.Debug("Skipping author {0}, it is already part of this import", foreignAuthorId);
                result.Skipped.Add(new SkippedAuthor
                {
                    Author = requested,
                    Reason = AddAuthorSkipReason.Duplicate,
                    Message = "This author is selected more than once in this import"
                });

                return true;
            }

            if (_authorService.FindById(foreignAuthorId) != null)
            {
                _logger.Debug("Skipping author {0}, it has already been added", foreignAuthorId);
                result.Skipped.Add(new SkippedAuthor
                {
                    Author = requested,
                    Reason = AddAuthorSkipReason.Exists,
                    Message = AuthorExistsValidator.AlreadyAddedMessage
                });

                return true;
            }

            return false;
        }

        private Author AddSkyhookData(Author newAuthor)
        {
            Author author;

            try
            {
                author = _authorInfo.GetAuthorInfo(newAuthor.Metadata.Value.ForeignAuthorId, false);
            }
            catch (AuthorNotFoundException)
            {
                _logger.Error("ReadarrId {0} was not found, it may have been removed from Goodreads.", newAuthor.Metadata.Value.ForeignAuthorId);

                throw new ValidationException(new List<ValidationFailure>
                {
                    new ("ForeignAuthorId", "An author with this ID was not found", newAuthor.Metadata.Value.ForeignAuthorId)
                });
            }

            author.ApplyChanges(newAuthor);

            return author;
        }

        private Author SetPropertiesAndValidate(Author newAuthor)
        {
            var path = newAuthor.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                var folderName = _fileNameBuilder.GetAuthorFolder(newAuthor);
                path = Path.Combine(newAuthor.RootFolderPath, folderName);
            }

            // Disambiguate author path if it exists already
            if (_authorService.AuthorPathExists(path))
            {
                if (newAuthor.Metadata.Value.Disambiguation.IsNotNullOrWhiteSpace())
                {
                    path += $" ({newAuthor.Metadata.Value.Disambiguation})";
                }

                if (_authorService.AuthorPathExists(path))
                {
                    var basepath = path;
                    var i = 0;
                    do
                    {
                        i++;
                        path = basepath + $" ({i})";
                    }
                    while (_authorService.AuthorPathExists(path));
                }
            }

            newAuthor.Path = path;
            newAuthor.CleanName = newAuthor.Metadata.Value.Name.CleanAuthorName();
            newAuthor.Added = DateTime.UtcNow;

            if (newAuthor.AddOptions != null && newAuthor.AddOptions.Monitor == MonitorTypes.None)
            {
                newAuthor.Monitored = false;
            }

            var validationResult = _addAuthorValidator.Validate(newAuthor);

            if (!validationResult.IsValid)
            {
                throw new ValidationException(validationResult.Errors);
            }

            return newAuthor;
        }
    }
}
