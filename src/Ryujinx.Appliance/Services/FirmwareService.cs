using System;
using System.IO;
using System.Threading.Tasks;
using LibHac.Tools.FsSystem;
using Ryujinx.Appliance.Client;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;

namespace Ryujinx.Appliance.Services
{
    public class FirmwareService
    {
        private readonly VirtualFileSystem _vfs;
        private readonly ContentManager _contentManager;
        private readonly KeysService _keysService;
        private readonly Func<string, object, Task> _eventBroadcaster;
        private readonly object _lock = new();

        public FirmwareService(VirtualFileSystem vfs, ContentManager contentManager, KeysService keysService, Func<string, object, Task> eventBroadcaster)
        {
            _vfs = vfs;
            _contentManager = contentManager;
            _keysService = keysService;
            _eventBroadcaster = eventBroadcaster;
        }

        public FirmwareStatusDto GetStatus()
        {
            lock (_lock)
            {
                try
                {
                    SystemVersion version = _contentManager.GetCurrentFirmwareVersion();
                    if (version != null && !string.IsNullOrEmpty(version.VersionString))
                    {
                        return new FirmwareStatusDto
                        {
                            Status = "installed",
                            Version = version.VersionString,
                            InstalledPackageCount = 1
                        };
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Failed to query firmware status: {ex.Message}");
                }

                return new FirmwareStatusDto
                {
                    Status = "missing",
                    Version = string.Empty,
                    InstalledPackageCount = 0
                };
            }
        }

        public async Task<InstallResultDto> InstallFirmwareAsync(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "firmware",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = "Source firmware path is empty."
                };
            }

            sourcePath = Path.GetFullPath(sourcePath);

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "firmware",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = $"Source firmware '{sourcePath}' does not exist."
                };
            }

            // Verify keys prerequisite
            KeyStatusDto keysStatus = _keysService.GetStatus();
            if (keysStatus.Status != "installed")
            {
                return new InstallResultDto
                {
                    ContentType = "firmware",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.KeysMissing,
                    ErrorMessage = "Cannot install firmware: prod.keys must be installed first."
                };
            }

            // Reload keys into VFS
            _vfs.ReloadKeySet();

            try
            {
                await _eventBroadcaster("install.started", new InstallProgressDto
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    Phase = "validating",
                    CurrentItem = Path.GetFileName(sourcePath)
                });

                // Validate package format if file
                if (File.Exists(sourcePath))
                {
                    string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                    if (ext != ".zip" && ext != ".xci")
                    {
                        return new InstallResultDto
                        {
                            ContentType = "firmware",
                            Status = "FAILED",
                            ErrorCode = ErrorCodes.FirmwareInvalid,
                            ErrorMessage = "Firmware package must be a .zip archive or .xci cart dump."
                        };
                    }

                    try
                    {
                        SystemVersion verifiedVersion = _contentManager.VerifyFirmwarePackage(sourcePath);
                        Logger.Notice.Print(LogClass.Application, $"Valid firmware archive detected: {verifiedVersion.VersionString}");
                    }
                    catch (Exception ex)
                    {
                        return new InstallResultDto
                        {
                            ContentType = "firmware",
                            Status = "FAILED",
                            ErrorCode = ErrorCodes.FirmwareInvalid,
                            ErrorMessage = $"Firmware package validation failed: {ex.Message}"
                        };
                    }
                }

                await _eventBroadcaster("install.progress", new InstallProgressDto
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    Phase = "installing",
                    CurrentItem = Path.GetFileName(sourcePath)
                });

                lock (_lock)
                {
                    _contentManager.InstallFirmware(sourcePath);
                    _contentManager.LoadEntries();
                }

                FirmwareStatusDto status = GetStatus();

                await _eventBroadcaster("firmware.changed", status);
                await _eventBroadcaster("install.completed", new InstallProgressDto
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    Phase = "committing",
                    Percent = 100.0f
                });

                return new InstallResultDto
                {
                    ContentType = "firmware",
                    Status = "INSTALLED",
                    TitleName = "Nintendo Switch System Firmware",
                    Version = status.Version
                };
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Firmware installation failed: {ex.Message}");
                return new InstallResultDto
                {
                    ContentType = "firmware",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InstallFailed,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
