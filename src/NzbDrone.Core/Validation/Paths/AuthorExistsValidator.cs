using FluentValidation.Validators;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Validation.Paths
{
    public class AuthorExistsValidator : PropertyValidator
    {
        // Shared with the bulk import, which skips such authors instead of
        // rejecting the request, so both paths word it the same way.
        public const string AlreadyAddedMessage = "This author has already been added";

        private readonly IAuthorService _authorService;

        public AuthorExistsValidator(IAuthorService authorService)
        {
            _authorService = authorService;
        }

        protected override string GetDefaultMessageTemplate() => AlreadyAddedMessage;

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            var foreignAuthorId = context.PropertyValue.ToString();

            return _authorService.FindById(foreignAuthorId) == null;
        }
    }
}
