using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    public class OpenLibrarySubjectImportListValidator : AbstractValidator<OpenLibrarySubjectImportListSettings>
    {
        public OpenLibrarySubjectImportListValidator()
        {
            RuleFor(c => c.Subject).NotEmpty();
        }
    }

    public class OpenLibrarySubjectImportListSettings : IImportListSettings
    {
        private static readonly OpenLibrarySubjectImportListValidator Validator = new();

        public OpenLibrarySubjectImportListSettings()
        {
            BaseUrl = "https://openlibrary.org";
            Limit = 50;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Subject", HelpText = "OL subject tag (e.g., science_fiction, fantasy). Underscores for spaces. Lowercase recommended.")]
        public string Subject { get; set; }

        [FieldDefinition(1, Label = "Limit", HelpText = "Maximum works to import per refresh (OL caps individual responses around 1000).")]
        public int Limit { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
