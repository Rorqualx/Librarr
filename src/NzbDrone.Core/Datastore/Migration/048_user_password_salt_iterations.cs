using FluentMigrator;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(048)]
    public class user_password_salt_iterations : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            // Storage for the salted PBKDF2 password hash that replaces the
            // inherited unsalted SHA-256 one.
            //
            // Deliberately no data migration: the plaintext is not available
            // here, so an existing row cannot be re-hashed. It keeps its
            // SHA-256 value with a NULL Salt, which is exactly how
            // UserService recognises a legacy hash; the row is upgraded in
            // place the next time that user logs in successfully.
            //
            // Iterations is 0 rather than NULL for a legacy row so the int
            // on the model needs no null handling on either engine.
            Alter.Table("Users")
                 .AddColumn("Salt").AsString().Nullable()
                 .AddColumn("Iterations").AsInt32().NotNullable().WithDefaultValue(0);
        }
    }
}
