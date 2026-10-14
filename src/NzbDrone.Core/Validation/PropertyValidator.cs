using FluentValidation;
using FluentValidation.Internal;

namespace NzbDrone.Core.Validation
{
    // FluentValidation 10 replaced the non-generic PropertyValidator with
    // PropertyValidator<T, TProperty>, typed on the object being validated as
    // well as the property. Ours cannot be: most are resolved from the
    // container and attached to properties of several different resources
    // (PathExistsValidator alone sits on root folders, remote path mappings,
    // media management config and rTorrent settings), and
    // IPropertyValidator<T, TProperty> is invariant in T.
    //
    // So the 9.x shape is kept here, and the validators built on it are
    // unchanged. SetValidator below wraps one in a PropertyValidator<T, TProperty>
    // at the point it is attached to a rule, which is when T is known.
    public abstract class PropertyValidator
    {
        protected abstract string GetDefaultMessageTemplate();

        protected abstract bool IsValid(PropertyValidatorContext context);

        internal string DefaultMessageTemplate => GetDefaultMessageTemplate();

        internal bool Validate(PropertyValidatorContext context) => IsValid(context);
    }

    public class PropertyValidatorContext
    {
        public PropertyValidatorContext(IValidationContext parentContext, object propertyValue, MessageFormatter messageFormatter)
        {
            ParentContext = parentContext;
            PropertyValue = propertyValue;
            MessageFormatter = messageFormatter;
        }

        // The context of the object that owns the property, as in 9.x:
        // ParentContext.InstanceToValidate is that object.
        public IValidationContext ParentContext { get; }

        public object PropertyValue { get; }

        // Arguments appended here fill the {placeholders} in the message
        // template, as they did in 9.x.
        public MessageFormatter MessageFormatter { get; }
    }

    public static class PropertyValidatorExtensions
    {
        public static IRuleBuilderOptions<T, TProperty> SetValidator<T, TProperty>(this IRuleBuilder<T, TProperty> ruleBuilder, PropertyValidator validator)
        {
            return ruleBuilder.SetValidator(new PropertyValidatorAdapter<T, TProperty>(validator));
        }
    }

    internal sealed class PropertyValidatorAdapter<T, TProperty> : FluentValidation.Validators.PropertyValidator<T, TProperty>
    {
        private readonly PropertyValidator _validator;

        public PropertyValidatorAdapter(PropertyValidator validator)
        {
            _validator = validator;
        }

        // FluentValidation takes the failure's ErrorCode from this. 9.x used
        // the validator's type name, so use the wrapped one's, not ours.
        public override string Name => _validator.GetType().Name;

        public override bool IsValid(ValidationContext<T> context, TProperty value)
        {
            return _validator.Validate(new PropertyValidatorContext(context, value, context.MessageFormatter));
        }

        protected override string GetDefaultMessageTemplate(string errorCode)
        {
            return _validator.DefaultMessageTemplate;
        }
    }
}
