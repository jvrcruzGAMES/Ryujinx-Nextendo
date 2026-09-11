using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibHac.Common.Keys;
using Ryujinx.Appliance.Client;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.FileSystem;

namespace Ryujinx.Appliance.Services
{
    public class KeysService
    {
        private readonly Func<string, object, Task> _eventBroadcaster;
        private readonly object _lock = new();

        public KeysService(Func<string, object, Task> eventBroadcaster)
        {
            _eventBroadcaster = eventBroadcaster;
        }

        public KeyStatusDto GetStatus()
        {
            lock (_lock)
            {
                string prodKeysPath = Path.Combine(AppDataManager.KeysDirPath, "prod.keys");
                string titleKeysPath = Path.Combine(AppDataManager.KeysDirPath, "title.keys");

                bool hasProd = File.Exists(prodKeysPath) && new FileInfo(prodKeysPath).Length > 0;
                bool hasTitle = File.Exists(titleKeysPath) && new FileInfo(titleKeysPath).Length > 0;

                int count = 0;
                string status = "missing";

                if (hasProd)
                {
                    try
                    {
                        var lines = File.ReadAllLines(prodKeysPath);
                        count = lines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && l.Contains('='));

                        KeySet keySet = KeySet.CreateDefaultKeySet();
                        ExternalKeyReader.ReadKeyFile(keySet, prodKeysPath, null, hasTitle ? titleKeysPath : null, null, null);
                        status = (count > 0 && !keySet.HeaderKey.IsZeros()) ? "installed" : (count > 0 ? "installed" : "invalid");
                    }
                    catch
                    {
                        status = "invalid";
                    }
                }

                return new KeyStatusDto
                {
                    Status = status,
                    HasProdKeys = hasProd,
                    HasTitleKeys = hasTitle,
                    KeyCount = count
                };
            }
        }

        public async Task<InstallResultDto> InstallKeysAsync(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "keys",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = "Source keys path is empty."
                };
            }

            sourcePath = Path.GetFullPath(sourcePath);

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                return new InstallResultDto
                {
                    ContentType = "keys",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InvalidPath,
                    ErrorMessage = $"Source keys file '{sourcePath}' does not exist."
                };
            }

            string keysDir = AppDataManager.KeysDirPath;
            if (!Directory.Exists(keysDir))
            {
                Directory.CreateDirectory(keysDir);
            }

            try
            {
                if (File.Exists(sourcePath))
                {
                    string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
                    string fileName = Path.GetFileName(sourcePath);

                    if (ext != ".keys" && !fileName.EndsWith(".keys", StringComparison.OrdinalIgnoreCase))
                    {
                        return new InstallResultDto
                        {
                            ContentType = "keys",
                            Status = "FAILED",
                            ErrorCode = ErrorCodes.KeysInvalid,
                            ErrorMessage = "Keys file must have .keys extension (e.g. prod.keys or title.keys)."
                        };
                    }

                    // Verify keys parsing
                    try
                    {
                        var lines = File.ReadAllLines(sourcePath);
                        int validLines = lines.Count(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#') && l.Contains('='));
                        if (validLines == 0)
                        {
                            return new InstallResultDto
                            {
                                ContentType = "keys",
                                Status = "FAILED",
                                ErrorCode = ErrorCodes.KeysInvalid,
                                ErrorMessage = "Keys file is empty or contains no valid keys."
                            };
                        }

                        KeySet keySet = KeySet.CreateDefaultKeySet();
                        ExternalKeyReader.ReadKeyFile(keySet, sourcePath, null, null, null, null);
                    }
                    catch (Exception ex)
                    {
                        return new InstallResultDto
                        {
                            ContentType = "keys",
                            Status = "FAILED",
                            ErrorCode = ErrorCodes.KeysInvalid,
                            ErrorMessage = $"Failed to validate keys: {ex.Message}"
                        };
                    }

                    // Atomic write: write to temp file in keysDir, flush to disk, then atomic rename/replace
                    string targetName = fileName.Equals("title.keys", StringComparison.OrdinalIgnoreCase) ? "title.keys" : "prod.keys";
                    string targetPath = Path.Combine(keysDir, targetName);
                    string tempPath = Path.Combine(keysDir, $"{targetName}.tmp.{Guid.NewGuid():N}");

                    using (FileStream srcStream = File.OpenRead(sourcePath))
                    using (FileStream dstStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await srcStream.CopyToAsync(dstStream);
                        await dstStream.FlushAsync();
                        dstStream.Flush(flushToDisk: true);
                    }

                    File.Move(tempPath, targetPath, overwrite: true);
                    Logger.Notice.Print(LogClass.Application, $"Keys installed successfully to {targetName}");
                }
                else if (Directory.Exists(sourcePath))
                {
                    ContentManager.InstallKeys(sourcePath, keysDir);
                }

                KeyStatusDto status = GetStatus();
                await _eventBroadcaster("keys.changed", status);

                return new InstallResultDto
                {
                    ContentType = "keys",
                    Status = "INSTALLED",
                    TitleName = "Nintendo Switch Keys",
                    Version = $"{status.KeyCount} keys"
                };
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Keys installation failed: {ex.Message}");
                return new InstallResultDto
                {
                    ContentType = "keys",
                    Status = "FAILED",
                    ErrorCode = ErrorCodes.InstallFailed,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
