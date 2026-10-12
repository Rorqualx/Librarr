using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Common.Exceptions;
using NzbDrone.Host;
using NzbDrone.Test.Common;

namespace NzbDrone.App.Test
{
    [TestFixture]
    public class SslCertificateValidationFixture : TestBase
    {
        private const string Password = "correct horse";

        private string GivenCertificateFile()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=librarr-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            var path = Path.Combine(TempFolder, "cert.pfx");
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));

            return path;
        }

        [Test]
        public void should_say_the_file_does_not_exist_when_it_is_missing()
        {
            var path = Path.Combine(TempFolder, "missing.pfx");

            Action act = () => Bootstrap.ValidateSslCertificate(path, Password);

            act.Should().Throw<ReadarrStartupException>()
               .Which.Message.Should().Contain($"The SSL certificate file {path} does not exist");
        }

        [Test]
        public void should_say_the_file_does_not_exist_when_its_folder_is_missing()
        {
            var path = Path.Combine(TempFolder, "no-such-folder", "missing.pfx");

            Action act = () => Bootstrap.ValidateSslCertificate(path, Password);

            act.Should().Throw<ReadarrStartupException>()
               .Which.Message.Should().Contain("does not exist");
        }

        [Test]
        public void should_name_the_file_and_the_password_when_the_password_is_wrong()
        {
            var path = GivenCertificateFile();

            Action act = () => Bootstrap.ValidateSslCertificate(path, "wrong");

            var message = act.Should().Throw<ReadarrStartupException>().Which.Message;

            message.Should().Contain(path);
            message.Should().Contain("password");
            message.Should().NotContain("does not exist");
        }

        [Test]
        public void should_name_the_file_when_it_is_not_a_certificate()
        {
            var path = Path.Combine(TempFolder, "garbage.pfx");
            File.WriteAllText(path, "not a certificate");

            Action act = () => Bootstrap.ValidateSslCertificate(path, Password);

            var message = act.Should().Throw<ReadarrStartupException>().Which.Message;

            message.Should().Contain(path);
            message.Should().NotContain("does not exist");
        }

        [Test]
        public void should_load_a_certificate_with_the_right_password()
        {
            var path = GivenCertificateFile();

            using var certificate = Bootstrap.ValidateSslCertificate(path, Password);

            certificate.Subject.Should().Be("CN=librarr-test");
        }
    }
}
