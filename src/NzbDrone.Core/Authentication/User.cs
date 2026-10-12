using System;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Authentication
{
    public class User : ModelBase
    {
        public Guid Identifier { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        // Base64 PBKDF2 salt. Empty for a user whose Password is still the
        // legacy unsalted SHA-256 hash; see UserService.
        public string Salt { get; set; }
        public int Iterations { get; set; }
    }
}
