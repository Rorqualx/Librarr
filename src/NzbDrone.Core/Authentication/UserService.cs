using System;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Authentication
{
    public interface IUserService
    {
        User Add(string username, string password);
        User Update(User user);
        User Upsert(string username, string password);
        User FindUser();
        User FindUser(string username, string password);
        User FindUser(Guid identifier);
    }

    public class UserService : IUserService
    {
        // PBKDF2-HMAC-SHA512. The count is stored per user, so raising it
        // here upgrades each user on their next successful login.
        public const int ITERATIONS = 210000;
        private const int SALT_SIZE = 128 / 8;
        private const int NUMBER_OF_BYTES = 256 / 8;

        private readonly IUserRepository _repo;
        private readonly Logger _logger;

        public UserService(IUserRepository repo, Logger logger)
        {
            _repo = repo;
            _logger = logger;
        }

        public User Add(string username, string password)
        {
            var user = new User
            {
                Identifier = Guid.NewGuid(),
                Username = username.ToLowerInvariant()
            };

            SetUserHashedPassword(user, password);

            return _repo.Insert(user);
        }

        public User Update(User user)
        {
            return _repo.Update(user);
        }

        // A null or empty password means "leave the password alone": the
        // caller has no way to tell whether a password it was never shown has
        // changed, so it has to say so explicitly.
        public User Upsert(string username, string password)
        {
            var user = FindUser();

            if (user == null)
            {
                return password.IsNullOrWhiteSpace() ? null : Add(username, password);
            }

            if (password.IsNotNullOrWhiteSpace())
            {
                SetUserHashedPassword(user, password);
            }

            user.Username = username.ToLowerInvariant();

            return Update(user);
        }

        public User FindUser()
        {
            return _repo.SingleOrDefault();
        }

        public User FindUser(string username, string password)
        {
            if (username.IsNullOrWhiteSpace() || password.IsNullOrWhiteSpace())
            {
                return null;
            }

            var user = _repo.FindUser(username.ToLowerInvariant());

            if (user == null)
            {
                return null;
            }

            if (!VerifyPassword(user, password))
            {
                return null;
            }

            if (NeedsRehash(user))
            {
                UpgradeHashedPassword(user, password);
            }

            return user;
        }

        public User FindUser(Guid identifier)
        {
            return _repo.FindUser(identifier);
        }

        private static bool IsLegacyHash(User user)
        {
            return user.Salt.IsNullOrWhiteSpace();
        }

        private static bool NeedsRehash(User user)
        {
            return IsLegacyHash(user) || user.Iterations < ITERATIONS;
        }

        private static bool VerifyPassword(User user, string password)
        {
            if (user.Password.IsNullOrWhiteSpace())
            {
                return false;
            }

            // Hashes written before migration 048 are unsalted SHA-256. They
            // are still accepted so nobody is locked out by upgrading, and
            // are replaced as soon as they have been verified once.
            var hashedPassword = IsLegacyHash(user)
                ? password.SHA256Hash()
                : GetHashedPassword(password, Convert.FromBase64String(user.Salt), user.Iterations);

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(user.Password), Encoding.UTF8.GetBytes(hashedPassword));
        }

        private void UpgradeHashedPassword(User user, string password)
        {
            var previous = new { user.Password, user.Salt, user.Iterations };

            try
            {
                SetUserHashedPassword(user, password);
                _repo.Update(user);
            }
            catch (Exception ex)
            {
                // The password was correct; failing to store the stronger hash
                // must not turn a good login into a failed one.
                user.Password = previous.Password;
                user.Salt = previous.Salt;
                user.Iterations = previous.Iterations;

                _logger.Warn(ex, "Unable to upgrade the stored password hash, it will be retried on the next login");
            }
        }

        private static void SetUserHashedPassword(User user, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SALT_SIZE);

            user.Iterations = ITERATIONS;
            user.Salt = Convert.ToBase64String(salt);
            user.Password = GetHashedPassword(password, salt, ITERATIONS);
        }

        private static string GetHashedPassword(string password, byte[] salt, int iterations)
        {
            return Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, NUMBER_OF_BYTES));
        }
    }
}
