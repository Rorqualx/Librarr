using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    public class OpenLibraryAuthorImportListValidator : AbstractValidator<OpenLibraryAuthorImportListSettings>
    {
        public OpenLibraryAuthorImportListValidator()
        {
            RuleFor(c => c.AuthorKey).NotEmpty();
        }
    }

    public class OpenLibraryAuthorImportListSettings : IImportListSettings
    {
        private static readonly OpenLibraryAuthorImportListValidator Validator = new();

        public OpenLibraryAuthorImportListSettings()
        {
            BaseUrl = "https://openlibrary.org";
            Limit = 100;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Author OL Key", HelpText = "OpenLibrary author key (e.g. OL23919A). Find it in any OL author page URL: openlibrary.org/authors/OL23919A/Isaac_Asimov")]
        public string AuthorKey { get; set; }

        [FieldDefinition(1, Label = "Limit", HelpText = "Maximum works to import per refresh (OL paginates at ~50/page; this is the total cap).")]
        public int Limit { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
