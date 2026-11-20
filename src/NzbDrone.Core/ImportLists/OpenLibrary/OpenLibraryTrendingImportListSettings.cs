using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    public class OpenLibraryTrendingImportListValidator : AbstractValidator<OpenLibraryTrendingImportListSettings>
    {
        public OpenLibraryTrendingImportListValidator()
        {
            RuleFor(c => c.Period).NotEmpty();
        }
    }

    public class OpenLibraryTrendingImportListSettings : IImportListSettings
    {
        private static readonly OpenLibraryTrendingImportListValidator Validator = new();

        public OpenLibraryTrendingImportListSettings()
        {
            BaseUrl = "https://openlibrary.org";
            Period = "weekly";
            Limit = 50;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Period", HelpText = "OL trending window: now, daily, weekly, monthly, yearly, forever.")]
        public string Period { get; set; }

        [FieldDefinition(1, Label = "Limit", HelpText = "Maximum works to import per refresh.")]
        public int Limit { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
