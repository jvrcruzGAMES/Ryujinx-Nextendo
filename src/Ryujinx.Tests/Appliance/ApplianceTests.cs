using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Ryujinx.Appliance.Client;
using Ryujinx.Appliance.Services;
using Ryujinx.Common.Configuration;
using Ryujinx.HLE.FileSystem;

namespace Ryujinx.Tests.Appliance
{
    [TestFixture]
    public class ApplianceTests
    {
        private string _tempDir;
        private string _socketPath;
        private string _gamesDir;
        private string _keysDir;

        [SetUp]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"ryujinx_test_{Guid.NewGuid():N}");
            _socketPath = Path.Combine(_tempDir, "test.sock");
            _gamesDir = Path.Combine(_tempDir, "games");
            _keysDir = Path.Combine(_tempDir, "system");

            Directory.CreateDirectory(_tempDir);
            Directory.CreateDirectory(_gamesDir);
            Directory.CreateDirectory(_keysDir);

            AppDataManager.Initialize(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { }
            }
        }

        [Test]
        public void ErrorCodes_AreStandardizedAndNotEmpty()
        {
            Assert.That(ErrorCodes.KeysMissing, Is.EqualTo("KEYS_MISSING"));
            Assert.That(ErrorCodes.FirmwareMissing, Is.EqualTo("FIRMWARE_MISSING"));
            Assert.That(ErrorCodes.GameAlreadyRunning, Is.EqualTo("GAME_ALREADY_RUNNING"));
            Assert.That(ErrorCodes.InsufficientSpace, Is.EqualTo("INSUFFICIENT_SPACE"));
            Assert.That(ErrorCodes.UpdateOlderThanInstalled, Is.EqualTo("UPDATE_OLDER_THAN_INSTALLED"));
        }

        [Test]
        public void IpcMessage_Serialization_RoundTripsAccurately()
        {
            IpcRequest request = new()
            {
                Version = 1,
                Id = "req-12345",
                Method = "library.list",
                Params = null
            };

            string json = JsonSerializer.Serialize(request);
            IpcRequest deserialized = JsonSerializer.Deserialize<IpcRequest>(json);

            Assert.That(deserialized, Is.Not.Null);
            Assert.That(deserialized.Version, Is.EqualTo(1));
            Assert.That(deserialized.Id, Is.EqualTo("req-12345"));
            Assert.That(deserialized.Method, Is.EqualTo("library.list"));
        }

        [Test]
        public void KeysService_MissingKeys_ReportsMissingStatus()
        {
            KeysService service = new((ev, data) => Task.CompletedTask);
            KeyStatusDto status = service.GetStatus();

            Assert.That(status.Status, Is.EqualTo("missing"));
            Assert.That(status.HasProdKeys, Is.False);
            Assert.That(status.HasTitleKeys, Is.False);
            Assert.That(status.KeyCount, Is.EqualTo(0));
        }

        [Test]
        public async Task KeysService_InstallInvalidPath_ReturnsInvalidPathError()
        {
            KeysService service = new((ev, data) => Task.CompletedTask);
            InstallResultDto result = await service.InstallKeysAsync(Path.Combine(_tempDir, "non_existent.keys"));

            Assert.That(result.Status, Is.EqualTo("FAILED"));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidPath));
        }

        private static readonly Lazy<VirtualFileSystem> _vfs = new(() => VirtualFileSystem.CreateInstance());

        [Test]
        public async Task FirmwareService_MissingKeys_FailsSafelyWithKeysMissing()
        {
            VirtualFileSystem vfs = _vfs.Value;
            ContentManager cm = new(vfs);
            KeysService keysService = new((ev, data) => Task.CompletedTask);
            FirmwareService firmwareService = new(vfs, cm, keysService, (ev, data) => Task.CompletedTask);

            // Create dummy firmware zip
            string dummyFw = Path.Combine(_tempDir, "Firmware.zip");
            File.WriteAllBytes(dummyFw, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

            InstallResultDto result = await firmwareService.InstallFirmwareAsync(dummyFw);

            Assert.That(result.Status, Is.EqualTo("FAILED"));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.KeysMissing));
        }

        [Test]
        public async Task InstallService_MissingKeys_FailsSafelyWithKeysMissing()
        {
            VirtualFileSystem vfs = _vfs.Value;
            KeysService keysService = new((ev, data) => Task.CompletedTask);
            GameLibraryService libraryService = new(vfs, (ev, data) => Task.CompletedTask);
            InstallService installService = new(vfs, keysService, libraryService, (ev, data) => Task.CompletedTask, _tempDir);

            string dummyNsp = Path.Combine(_tempDir, "test.nsp");
            File.WriteAllBytes(dummyNsp, new byte[1024]);

            InstallResultDto result = await installService.InstallTitleAsync(dummyNsp);

            Assert.That(result.Status, Is.EqualTo("FAILED"));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.KeysMissing));
        }

        [Test]
        public void StorageStatus_ReportsValidPartitionMetrics()
        {
            VirtualFileSystem vfs = _vfs.Value;
            KeysService keysService = new((ev, data) => Task.CompletedTask);
            GameLibraryService libraryService = new(vfs, (ev, data) => Task.CompletedTask);
            InstallService installService = new(vfs, keysService, libraryService, (ev, data) => Task.CompletedTask, _tempDir);

            StorageStatusDto storage = installService.GetStorageStatus();

            Assert.That(storage.TotalBytes, Is.GreaterThan(0));
            Assert.That(storage.FreeBytes, Is.GreaterThan(0));
        }
    }
}
