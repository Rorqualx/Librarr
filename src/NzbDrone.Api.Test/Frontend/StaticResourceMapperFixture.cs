using System;
using System.IO;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Configuration;
using NzbDrone.Test.Common;
using Readarr.Http.Frontend.Mappers;

namespace NzbDrone.Api.Test.Frontend
{
    // Every mapper may only serve files from its own folder, and a request that
    // resolves anywhere else must look exactly like a request for a missing
    // file. The disk provider here claims every file exists, and a real file
    // is planted outside each folder, so the containment check is the only
    // thing standing between a request and that file.
    [TestFixture]
    public class StaticResourceMapperFixture : TestBase
    {
        private string _startUpFolder;
        private string _appDataFolder;
        private string _uiFolder;
        private string _backupFolder;

        [SetUp]
        public void Setup()
        {
            _startUpFolder = Path.Combine(TempFolder, "app");
            _appDataFolder = Path.Combine(TempFolder, "appdata");
            _uiFolder = Path.Combine(_startUpFolder, "UI");
            _backupFolder = Path.Combine(_appDataFolder, "Backups");

            GivenFile(Path.Combine(_uiFolder, "Content", "app.js"));
            GivenFile(Path.Combine(_uiFolder, "Content", "Images", "Icons", "favicon.ico"));
            GivenFile(Path.Combine(_startUpFolder, "Readarr.dll"));
            GivenFile(Path.Combine(TempFolder, "secret.txt"));
            GivenFile(Path.Combine(_appDataFolder, "config.xml"));
            GivenFile(Path.Combine(_appDataFolder, "readarr.db"));
            GivenFile(Path.Combine(_appDataFolder, "readarr_backup_2026.01.01.zip"));
            GivenFile(Path.Combine(_appDataFolder, "MediaCover", "Books", "4", "cover.jpg"));
            GivenFile(Path.Combine(_appDataFolder, "logs", "readarr.txt"));
            GivenFile(Path.Combine(_backupFolder, "manual", "readarr_backup_2026.01.01.zip"));

            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.StartUpFolder).Returns(_startUpFolder);
            Mocker.GetMock<IAppFolderInfo>().SetupGet(c => c.AppDataFolder).Returns(_appDataFolder);
            Mocker.GetMock<IConfigFileProvider>().SetupGet(c => c.UiFolder).Returns("UI");
            Mocker.GetMock<IBackupService>().Setup(c => c.GetBackupFolder()).Returns(_backupFolder);

            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>(), It.IsAny<StringComparison>())).Returns(true);
            Mocker.GetMock<IDiskProvider>().Setup(c => c.GetFileSize(It.IsAny<string>())).Returns(100);
        }

        private static void GivenFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "content");
        }

        private void ShouldServe<TMapper>(string resourceUrl)
            where TMapper : StaticResourceMapperBase
        {
            var mapper = Mocker.Resolve<TMapper>();

            mapper.CanHandle(resourceUrl).Should().BeTrue();

            var result = mapper.GetResponse(resourceUrl);

            result.Should().BeOfType<FileStreamResult>();
            ((FileStreamResult)result).FileStream.Dispose();
        }

        private void ShouldRefuse<TMapper>(string resourceUrl)
            where TMapper : StaticResourceMapperBase
        {
            Mocker.Resolve<TMapper>().GetResponse(resourceUrl).Should().BeNull();

            // Never even looked at.
            Mocker.GetMock<IDiskProvider>().Verify(c => c.FileExists(It.IsAny<string>()), Times.Never());
            Mocker.GetMock<IDiskProvider>().Verify(c => c.FileExists(It.IsAny<string>(), It.IsAny<StringComparison>()), Times.Never());

            ExceptionVerification.ExpectedWarns(1);
        }

        [Test]
        public void static_resource_mapper_should_serve_file_inside_ui_folder()
        {
            ShouldServe<StaticResourceMapper>("/Content/app.js");
        }

        [TestCase("/Content/../../Readarr.dll")]
        [TestCase("/Content/../../../secret.txt")]
        [TestCase("/Content/../../../appdata/config.xml")]
        [TestCase(@"/Content/..\..\Readarr.dll")]
        [TestCase(@"/Content/..\..\..\secret.txt")]
        [TestCase(@"/Content\..\..\..\appdata\config.xml")]
        [TestCase(@"/Content/Images/..\../..\..\Readarr.dll")]
        public void static_resource_mapper_should_refuse_path_outside_ui_folder(string resourceUrl)
        {
            ShouldRefuse<StaticResourceMapper>(resourceUrl);
        }

        [Test]
        public void static_resource_mapper_should_refuse_the_ui_folder_itself()
        {
            ShouldRefuse<StaticResourceMapper>("/Content/..");
        }

        [Test]
        public void refused_path_should_be_answered_like_a_missing_file()
        {
            Mocker.GetMock<IDiskProvider>().Setup(c => c.FileExists(It.IsAny<string>(), It.IsAny<StringComparison>())).Returns(false);

            var mapper = Mocker.Resolve<StaticResourceMapper>();

            var missing = mapper.GetResponse("/Content/missing.js");
            var refused = mapper.GetResponse("/Content/../../Readarr.dll");

            missing.Should().BeNull();
            refused.Should().BeNull();

            ExceptionVerification.ExpectedWarns(2);
        }

        [Test]
        public void media_cover_mapper_should_serve_file_inside_media_cover_folder()
        {
            ShouldServe<MediaCoverMapper>("/MediaCover/Books/4/cover.jpg");
        }

        [TestCase("/MediaCover/../readarr.db")]
        [TestCase("/MediaCover/../config.xml")]
        [TestCase("/MediaCover/Books/../../../secret.txt")]
        [TestCase(@"/MediaCover/..\readarr.db")]
        [TestCase(@"/MediaCover/Books\..\..\config.xml")]
        public void media_cover_mapper_should_refuse_path_outside_media_cover_folder(string resourceUrl)
        {
            ShouldRefuse<MediaCoverMapper>(resourceUrl);
        }

        [Test]
        public void backup_mapper_should_serve_file_inside_backup_folder()
        {
            ShouldServe<BackupFileMapper>("/backup/manual/readarr_backup_2026.01.01.zip");
        }

        [TestCase("/backup/../readarr_backup_2026.01.01.zip")]
        [TestCase(@"/backup/..\readarr_backup_2026.01.01.zip")]
        [TestCase(@"/backup/manual\..\..\..\readarr_backup_2026.01.01.zip")]
        public void backup_mapper_should_refuse_path_outside_backup_folder(string resourceUrl)
        {
            ShouldRefuse<BackupFileMapper>(resourceUrl);
        }

        [Test]
        public void log_file_mapper_should_serve_file_inside_log_folder()
        {
            ShouldServe<LogFileMapper>("/logfile/readarr.txt");
        }

        [TestCase("/logfile/../config.xml")]
        [TestCase("/logfile/../../secret.txt")]
        [TestCase("/logfile/sub/../../readarr.txt")]
        public void log_file_mapper_should_only_ever_map_into_log_folder(string resourceUrl)
        {
            var logFolder = Path.Combine(_appDataFolder, "logs");

            var mapped = Mocker.Resolve<LogFileMapper>().Map(resourceUrl);

            Path.GetDirectoryName(Path.GetFullPath(mapped)).Should().Be(logFolder);
        }

        [TestCase(@"/logfile/..\config.xml")]
        [TestCase(@"/logfile/..\..\secret.txt")]
        public void log_file_mapper_should_refuse_backslash_path_outside_log_folder(string resourceUrl)
        {
            ShouldRefuse<LogFileMapper>(resourceUrl);
        }

        [TestCase(@"/updatelogfile/..\config.xml")]
        [TestCase(@"/updatelogfile/..\..\secret.txt")]
        public void update_log_file_mapper_should_refuse_backslash_path_outside_log_folder(string resourceUrl)
        {
            ShouldRefuse<UpdateLogFileMapper>(resourceUrl);
        }

        [Test]
        public void favicon_mapper_should_still_serve_its_fixed_file()
        {
            GivenFile(Path.Combine(_uiFolder, "Content", "Images", "Icons", "favicon-debug.ico"));

            ShouldServe<FaviconMapper>("/favicon.ico");
        }
    }
}
