using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Appliance.Client;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using ContentType = LibHac.Ncm.ContentType;
using Path = System.IO.Path;
using SpanHelpers = LibHac.Common.SpanHelpers;

namespace Ryujinx.Appliance.Services
{
    public class InstallService
    {
        private readonly VirtualFileSystem _vfs;
        private readonly KeysService _keysService;
        private readonly GameLibraryService _libraryService;
        private readonly Func<string, object, Task> _eventBroadcaster;
        private readonly string _gamesDir;
        private readonly string _storageMountPoint;
        private readonly object _lock = new();

        public InstallService(VirtualFileSystem vfs, KeysService keysService, GameLibraryService libraryService, Func<string, object, Task> eventBroadcaster, string storageMountPoint = null)
        {
            _vfs = vfs;
            _keysService = keysService;
            _libraryService = libraryService;
            _eventBroadcaster = eventBroadcaster;
            _storageMountPoint = storageMountPoint ?? "/var/lib/console";
            _gamesDir = Path.Combine(AppDataManager.BaseDirPath, "games");

            if (!Directory.Exists(_gamesDir))
            {
                try { Directory.CreateDirectory(_gamesDir); } catch { }
            }
        }

        public StorageStatusDto GetStorageStatus()
        {
            try
            {
                string targetDir = Directory.Exists(_storageMountPoint) ? _storageMountPoint : AppDataManager.BaseDirPath;
                DriveInfo drive = new(targetDir);
                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                long used = total - free;

                return new StorageStatusDto
                {
                    MountPoint = targetDir,
                    TotalBytes = total,
                    UsedBytes = used,
                    FreeBytes = free
                };
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Failed to query drive info: {ex.Message}");
                return new StorageStatusDto
                {
                    MountPoint = _storageMountPoint,
                    TotalBytes = 32L * 1024 * 1024 * 1024,
                    UsedBytes = 0,
                    FreeBytes = 32L * 1024 * 1024 * 1024
                };
            }
        }

        public async Task<InstallResultDto> InstallTitleAsync(string sourcePath, bool deferRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = "Source game path is empty."
                };
            }

            sourcePath = Path.GetFullPath(sourcePath);

            if (!File.Exists(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = $"Source file '{sourcePath}' does not exist."
                };
            }

            KeyStatusDto keysStatus = _keysService.GetStatus();
            if (keysStatus.Status != "installed")
            {
                return new InstallResultDto
                {
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.KeysMissing,
                    ErrorMessage = "Cannot install title: prod.keys must be installed first."
                };
            }

            FileInfo info = new(sourcePath);
            StorageStatusDto storage = GetStorageStatus();
            if (storage.FreeBytes < info.Length + 50 * 1024 * 1024)
            {
                return new InstallResultDto
                {
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InsufficientSpace,
                    ErrorMessage = $"Insufficient disk space. Required: {info.Length} bytes, Free: {storage.FreeBytes} bytes."
                };
            }

            _vfs.ReloadKeySet();

            string opId = Guid.NewGuid().ToString("N");
            await _eventBroadcaster("install.started", new InstallProgressDto
            {
                OperationId = opId,
                Phase = "validating",
                CurrentItem = info.Name,
                BytesTotal = info.Length
            });

            // Validate format and parse metadata
            GameTitleDto parsed = _libraryService.ParseTitleFile(sourcePath);
            if (parsed == null)
            {
                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.ContentInvalid,
                    ErrorMessage = "Invalid or unreadable Nintendo Switch title package."
                };
            }

            string targetFileName = Path.GetFileName(sourcePath);
            string targetFilePath = Path.Combine(_gamesDir, targetFileName);

            // If source is already in the target directory
            if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetFilePath), StringComparison.OrdinalIgnoreCase))
            {
                if (!deferRefresh)
                {
                    await _libraryService.RefreshLibraryAsync();
                }

                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "base",
                    Status = "ALREADY_INSTALLED",
                    TitleId = parsed.TitleId,
                    TitleName = parsed.Name,
                    Version = parsed.DisplayVersion
                };
            }

            try
            {
                await _eventBroadcaster("install.progress", new InstallProgressDto
                {
                    OperationId = opId,
                    Phase = "installing",
                    CurrentItem = targetFileName,
                    BytesTotal = info.Length
                });

                // Atomic copy: temp file -> fsync -> rename
                string tempFilePath = Path.Combine(_gamesDir, $"{targetFileName}.tmp.{Guid.NewGuid():N}");

                using (FileStream src = File.OpenRead(sourcePath))
                using (FileStream dst = new(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024]; // 1MB buffer
                    long totalRead = 0;
                    int read;

                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read));
                        totalRead += read;

                        await _eventBroadcaster("install.progress", new InstallProgressDto
                        {
                            OperationId = opId,
                            Phase = "installing",
                            BytesProcessed = totalRead,
                            BytesTotal = info.Length,
                            Percent = (float)totalRead / info.Length * 100.0f,
                            CurrentItem = targetFileName
                        });
                    }

                    await dst.FlushAsync();
                    dst.Flush(flushToDisk: true);
                }

                File.Move(tempFilePath, targetFilePath, overwrite: true);
                Logger.Notice.Print(LogClass.Application, $"Title installed successfully to {targetFilePath}");

                if (!deferRefresh)
                {
                    await _libraryService.RefreshLibraryAsync();
                }

                await _eventBroadcaster("install.completed", new InstallProgressDto
                {
                    OperationId = opId,
                    Phase = "committing",
                    Percent = 100.0f
                });

                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "base",
                    Status = "INSTALLED",
                    TitleId = parsed.TitleId,
                    TitleName = parsed.Name,
                    Version = parsed.DisplayVersion
                };
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Title installation failed: {ex.Message}");
                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "base",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InstallFailed,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<InstallResultDto> InstallUpdateAsync(string sourcePath, bool deferRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "update",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = "Source update path is empty."
                };
            }

            sourcePath = Path.GetFullPath(sourcePath);

            if (!File.Exists(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "update",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = $"Source update file '{sourcePath}' does not exist."
                };
            }

            KeyStatusDto keysStatus = _keysService.GetStatus();
            if (keysStatus.Status != "installed")
            {
                return new InstallResultDto
                {
                    ContentType = "update",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.KeysMissing,
                    ErrorMessage = "Cannot install update: prod.keys must be installed first."
                };
            }

            _vfs.ReloadKeySet();

            string opId = Guid.NewGuid().ToString("N");

            try
            {
                using FileStream stream = File.OpenRead(sourcePath);
                using IStorage storage = stream.AsStorage();
                PartitionFileSystem pfs = new();
                pfs.Initialize(storage);

                Dictionary<ulong, ContentMetaData> updates = pfs.GetContentData(ContentMetaType.Patch, _vfs, IntegrityCheckLevel.None);
                if (updates.Count == 0)
                {
                    return new InstallResultDto
                    {
                        OperationId = opId,
                        ContentType = "update",
                        Status = "FAILED",
                        ErrorCode = ErrorCodes.ContentInvalid,
                        ErrorMessage = "NSP file does not contain a Title Update (Patch)."
                    };
                }

                KeyValuePair<ulong, ContentMetaData> updateEntry = updates.First();
                ulong titleIdBase = updateEntry.Key;
                ContentMetaData content = updateEntry.Value;
                uint newVersion = content.Version.Version;

                Nca controlNca = content.GetNcaByType(_vfs.KeySet, ContentType.Control);
                string displayVersion = $"v{newVersion}";

                if (controlNca != null)
                {
                    ApplicationControlProperty controlData = new();
                    using UniqueRef<IFile> nacpFile = new();
                    if (controlNca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None)
                        .OpenFile(ref nacpFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
                    {
                        nacpFile.Get.Read(out _, 0, SpanHelpers.AsByteSpan(ref controlData), ReadOption.None);
                        displayVersion = controlData.DisplayVersionString.ToString();
                    }
                }

                // Check existing title updates for this title
                var existingUpdates = TitleUpdatesHelper.LoadTitleUpdatesJson(_vfs, titleIdBase);
                TitleUpdateModel existingSelected = existingUpdates.FirstOrDefault(u => u.IsSelected).Update;

                if (existingSelected != null && existingSelected.Version > newVersion)
                {
                    return new InstallResultDto
                    {
                        OperationId = opId,
                        ContentType = "update",
                        Status = "FAILED",
                        ErrorCode = ErrorCodes.UpdateOlderThanInstalled,
                        ErrorMessage = $"An update with a higher version ({existingSelected.DisplayVersion}) is already installed.",
                        TitleId = titleIdBase.ToString("X16"),
                        Version = displayVersion
                    };
                }

                // Copy update to persistent updates directory
                string updatesDir = Path.Combine(AppDataManager.BaseDirPath, "updates");
                if (!Directory.Exists(updatesDir))
                {
                    Directory.CreateDirectory(updatesDir);
                }

                string targetFileName = Path.GetFileName(sourcePath);
                string targetPath = Path.Combine(updatesDir, targetFileName);

                if (!sourcePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }

                TitleUpdateModel newModel = new(content.ApplicationId, newVersion, displayVersion, targetPath);

                // Update the JSON updates config
                List<(TitleUpdateModel, bool)> updatedList = [];
                foreach (var item in existingUpdates)
                {
                    if (item.Update.Version != newVersion)
                    {
                        updatedList.Add((item.Update, false));
                    }
                }
                updatedList.Add((newModel, true));

                TitleUpdatesHelper.SaveTitleUpdatesJson(titleIdBase, updatedList);

                Logger.Notice.Print(LogClass.Application, $"Title update {displayVersion} installed for {titleIdBase:X16}");

                if (!deferRefresh)
                {
                    await _libraryService.RefreshLibraryAsync();
                }

                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "update",
                    Status = "INSTALLED",
                    TitleId = titleIdBase.ToString("X16"),
                    Version = displayVersion
                };
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Title update installation failed: {ex.Message}");
                return new InstallResultDto
                {
                    OperationId = opId,
                    ContentType = "update",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InstallFailed,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
