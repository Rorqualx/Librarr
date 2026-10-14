using System.Collections.Generic;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Validation;

namespace Readarr.Http.Validation
{
    public class EmptyCollectionValidator<T> : PropertyValidator
    {
        protected override string GetDefaultMessageTemplate() => "Collection Must Be Empty";

        protected override bool IsValid(PropertyValidatorContext context)
        {
            if (context.PropertyValue == null)
            {
                return true;
            }

            var collection = context.PropertyValue as IEnumerable<T>;

            return collection != null && collection.Empty();
        }
    }
}
