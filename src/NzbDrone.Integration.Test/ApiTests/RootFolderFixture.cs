using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Readarr.Api.V1.RootFolders;

namespace NzbDrone.Integration.Test.ApiTests
{
    [TestFixture]
    public class RootFolderFixture : IntegrationTest
    {
        [Test]
        public void should_have_no_root_folder_initially()
        {
            RootFolders.All().Should().BeEmpty();
        }

        [Test]
        [Ignore("SignalR on CI seems unstable")]
        public void should_add_and_delete_root_folders()
        {
            ConnectSignalR().Wait();

            var rootFolder = new RootFolderResource
            {
                Path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            };

            var postResponse = RootFolders.Post(rootFolder);

            postResponse.Id.Should().NotBe(0);
            postResponse.FreeSpace.Should().NotBe(0);

            RootFolders.All().Should().OnlyContain(c => c.Id == postResponse.Id);

            RootFolders.Delete(postResponse.Id);

            RootFolders.All().Should().BeEmpty();

            SignalRMessages.Should().Contain(c => c.Name == "rootfolder");
        }

        // The Library Import wizard is driven entirely by this field: every
        // folder it lists comes from RootFolderResource.unmappedFolders, and
        // it auto-selects nothing the backend didn't report. Worth an
        // end-to-end assertion rather than only the unit-level one, because
        // the field is populated in RootFolderService.GetDetails() after the
        // row is already inserted — a serialization or TableMapping mistake
        // would show up as a silently empty wizard, not as an error.
        [Test]
        public void should_report_unmapped_folders_for_a_new_root_folder()
        {
            var root = GetTempDirectory("UnmappedRoot");

            Directory.CreateDirectory(Path.Combine(root, "Brandon Sanderson"));
            Directory.CreateDirectory(Path.Combine(root, "Ursula K. Le Guin"));

            // Blocklisted in RootFolderService.SpecialFolders — a real
            // library on a Synology or a Windows volume has these sitting
            // next to the author folders.
            Directory.CreateDirectory(Path.Combine(root, "@eaDir"));

            // Profile ids 1/1 are the seeded defaults, same as EnsureAuthor
            // uses. RootFolderResource validation rejects a post without
            // them.
            var postResponse = RootFolders.Post(new RootFolderResource
            {
                Name = "UnmappedRoot",
                Path = root,
                DefaultQualityProfileId = 1,
                DefaultMetadataProfileId = 1
            });

            try
            {
                postResponse.UnmappedFolders.Should().NotBeNull();

                postResponse.UnmappedFolders.Select(f => f.Name)
                            .Should().BeEquivalentTo("Brandon Sanderson", "Ursula K. Le Guin");

                postResponse.UnmappedFolders.Select(f => f.Path)
                            .Should().OnlyContain(p => p.StartsWith(root));

                // GET has to agree with the POST response — they take
                // different paths through RootFolderService.
                RootFolders.Get(postResponse.Id)
                           .UnmappedFolders.Select(f => f.Name)
                           .Should().BeEquivalentTo("Brandon Sanderson", "Ursula K. Le Guin");
            }
            finally
            {
                RootFolders.Delete(postResponse.Id);
            }
        }

        // ApiContract: asserts a 4xx, and needs no third party to do it.
        // The push pipeline runs exactly the tests carrying this category
        // (build.yml, "API 4xx contract tests"). Do not put it on a test
        // that reaches OpenLibrary -- see _AssemblyGate.
        [Test]
        [Category("ApiContract")]
        public void invalid_path_should_return_bad_request()
        {
            var rootFolder = new RootFolderResource
            {
                Path = "invalid_path"
            };

            var postResponse = RootFolders.InvalidPost(rootFolder);
            postResponse.Should().NotBeNull();
        }
    }
}
