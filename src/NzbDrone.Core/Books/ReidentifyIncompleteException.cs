using NzbDrone.Common.Exceptions;

namespace NzbDrone.Core.Books
{
    public class ReidentifyIncompleteException : NzbDroneException
    {
        public ReidentifyIncompleteException(int unreachableLookups)
            : base("Reidentify could not reach the metadata source for {0} lookups; run it again to finish", unreachableLookups)
        {
        }
    }
}
