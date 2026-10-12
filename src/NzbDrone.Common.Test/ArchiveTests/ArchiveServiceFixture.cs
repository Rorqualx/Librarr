using System;
using System.IO;
using System.Reflection;
using System.Text;
using FluentAssertions;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
using NUnit.Framework;
using NzbDrone.Test.Common;

namespace NzbDrone.Common.Test.ArchiveTests
{
    [TestFixture]
    public class ArchiveServiceFixture : TestBase<ArchiveService>
    {
        private string _workFolder;
        private string _destination;

        [SetUp]
        public void Setup()
        {
            // The destination sits two levels down so an entry that climbs out of
            // it still lands inside the per-test temp folder.
            _workFolder = Path.Combine(TempFolder, "work");
            _destination = Path.Combine(_workFolder, "nested", "extract");

            Directory.CreateDirectory(_destination);
        }

        private string GivenZip(params string[] entryNames)
        {
            var path = Path.Combine(TempFolder, "archive.zip");

            using (var zip = new ZipOutputStream(File.Create(path)))
            {
                foreach (var name in entryNames)
                {
                    // ZipEntry's public constructor cleans the name it is given,
                    // so set the field to keep the name exactly as written.
                    var entry = new ZipEntry("placeholder");
                    typeof(ZipEntry).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)
                                    .SetValue(entry, name);

                    zip.PutNextEntry(entry);

                    var bytes = Encoding.UTF8.GetBytes("content");
                    zip.Write(bytes, 0, bytes.Length);
                    zip.CloseEntry();
                }
            }

            return path;
        }

        private string GivenTgz(params string[] entryNames)
        {
            var path = Path.Combine(TempFolder, "archive.tar.gz");

            using (var tar = new TarOutputStream(new GZipOutputStream(File.Create(path)), Encoding.UTF8))
            {
                foreach (var name in entryNames)
                {
                    var bytes = Encoding.UTF8.GetBytes("content");
                    var entry = TarEntry.CreateTarEntry("placeholder");
                    entry.TarHeader.Name = name;
                    entry.Size = bytes.Length;

                    tar.PutNextEntry(entry);
                    tar.Write(bytes, 0, bytes.Length);
                    tar.CloseEntry();
                }
            }

            return path;
        }

        private string[] FilesUnderWorkFolder()
        {
            return Directory.GetFiles(_workFolder, "*", SearchOption.AllDirectories);
        }

        [Test]
        public void should_extract_zip_entries_inside_destination()
        {
            var archive = GivenZip("readarr.db", "sub/config.xml");

            Subject.Extract(archive, _destination);

            File.Exists(Path.Combine(_destination, "readarr.db")).Should().BeTrue();
            File.Exists(Path.Combine(_destination, "sub", "config.xml")).Should().BeTrue();
        }

        [TestCase("../outside.txt")]
        [TestCase("../../outside.txt")]
        [TestCase("sub/../../outside.txt")]
        [TestCase(@"..\outside.txt")]
        [TestCase(@"sub\..\..\outside.txt")]
        [TestCase("../extract-sibling/outside.txt")]
        public void should_refuse_zip_entry_outside_destination(string entryName)
        {
            var archive = GivenZip(entryName);

            Assert.Throws<IOException>(() => Subject.Extract(archive, _destination));

            FilesUnderWorkFolder().Should().BeEmpty();
        }

        [Test]
        public void should_keep_zip_entry_with_absolute_path_inside_destination()
        {
            // SharpZipLib drops the root from a rooted entry name when it reads
            // the archive, so the entry ends up below the destination instead of
            // at the path it names.
            var target = Path.Combine(_workFolder, "absolute.txt");
            var archive = GivenZip(target);

            Subject.Extract(archive, _destination);

            File.Exists(target).Should().BeFalse();
            FilesUnderWorkFolder().Should().OnlyContain(f => f.StartsWith(_destination + Path.DirectorySeparatorChar));
        }

        [Test]
        public void should_not_write_any_zip_entry_when_one_is_outside_destination()
        {
            var archive = GivenZip("readarr.db", "../outside.txt");

            Assert.Throws<IOException>(() => Subject.Extract(archive, _destination));

            FilesUnderWorkFolder().Should().BeEmpty();
        }

        [Test]
        public void should_extract_tgz_entries_inside_destination()
        {
            var archive = GivenTgz("Readarr/Readarr.dll", "Readarr/UI/index.html");

            Subject.Extract(archive, _destination);

            File.Exists(Path.Combine(_destination, "Readarr", "Readarr.dll")).Should().BeTrue();
            File.Exists(Path.Combine(_destination, "Readarr", "UI", "index.html")).Should().BeTrue();
        }

        [TestCase("../outside.txt")]
        [TestCase("../../outside.txt")]
        [TestCase("Readarr/../../outside.txt")]
        [TestCase(@"..\outside.txt")]
        [TestCase("../extract-sibling/outside.txt")]
        public void should_refuse_tgz_entry_outside_destination(string entryName)
        {
            var archive = GivenTgz(entryName);

            Assert.Catch<Exception>(() => Subject.Extract(archive, _destination));

            FilesUnderWorkFolder().Should().BeEmpty();
        }

        [Test]
        public void should_not_write_any_tgz_entry_when_one_is_outside_destination()
        {
            var archive = GivenTgz("Readarr/Readarr.dll", "../outside.txt");

            Assert.Catch<Exception>(() => Subject.Extract(archive, _destination));

            FilesUnderWorkFolder().Should().BeEmpty();
        }
    }
}
