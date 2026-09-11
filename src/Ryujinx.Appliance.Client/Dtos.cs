using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Ryujinx.Appliance.Client
{
    public class BackendStatusDto
    {
        [JsonPropertyName("protocolVersion")]
        public int ProtocolVersion { get; set; } = 1;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "READY";

        [JsonPropertyName("uptimeSeconds")]
        public double UptimeSeconds { get; set; }

        [JsonPropertyName("ryujinxVersion")]
        public string RyujinxVersion { get; set; } = string.Empty;

        [JsonPropertyName("architecture")]
        public string Architecture { get; set; } = string.Empty;

        [JsonPropertyName("cpuBackendRequested")]
        public string CpuBackendRequested { get; set; } = "Auto";

        [JsonPropertyName("cpuBackendActive")]
        public string CpuBackendActive { get; set; } = "LinuxKvm";

        [JsonPropertyName("kvmAvailable")]
        public bool KvmAvailable { get; set; }

        [JsonPropertyName("keysStatus")]
        public string KeysStatus { get; set; } = "missing"; // "missing", "installed", "invalid"

        [JsonPropertyName("firmwareStatus")]
        public string FirmwareStatus { get; set; } = "missing"; // "missing", "installed", "invalid"

        [JsonPropertyName("firmwareVersion")]
        public string FirmwareVersion { get; set; } = string.Empty;

        [JsonPropertyName("libraryCount")]
        public int LibraryCount { get; set; }

        [JsonPropertyName("currentGame")]
        public string CurrentGame { get; set; } = "none";

        [JsonPropertyName("activeSessionId")]
        public string ActiveSessionId { get; set; }
    }

    public class KeyStatusDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "missing"; // "missing", "installed", "invalid"

        [JsonPropertyName("hasProdKeys")]
        public bool HasProdKeys { get; set; }

        [JsonPropertyName("hasTitleKeys")]
        public bool HasTitleKeys { get; set; }

        [JsonPropertyName("keyCount")]
        public int KeyCount { get; set; }
    }

    public class FirmwareStatusDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "missing"; // "missing", "installed", "invalid"

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("installedPackageCount")]
        public int InstalledPackageCount { get; set; }
    }

    public class GameTitleDto
    {
        [JsonPropertyName("titleId")]
        public string TitleId { get; set; } = string.Empty;

        [JsonPropertyName("titleIdBase")]
        public string TitleIdBase { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = "Unknown";

        [JsonPropertyName("developer")]
        public string Developer { get; set; } = "Unknown";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "0";

        [JsonPropertyName("displayVersion")]
        public string DisplayVersion { get; set; } = "1.0.0";

        [JsonPropertyName("fileType")]
        public string FileType { get; set; } = string.Empty; // "NSP", "XCI", "NCA", "NRO"

        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("fileSizeBytes")]
        public long FileSizeBytes { get; set; }

        [JsonPropertyName("iconPath")]
        public string IconPath { get; set; } = string.Empty;

        [JsonPropertyName("favorite")]
        public bool Favorite { get; set; }

        [JsonPropertyName("timePlayedSeconds")]
        public double TimePlayedSeconds { get; set; }

        [JsonPropertyName("lastPlayedUtc")]
        public DateTime? LastPlayedUtc { get; set; }

        [JsonPropertyName("compatibilityStatus")]
        public string CompatibilityStatus { get; set; } = string.Empty;

        [JsonPropertyName("activeUpdateVersion")]
        public string ActiveUpdateVersion { get; set; } = string.Empty;

        [JsonPropertyName("dlcCount")]
        public int DlcCount { get; set; }
    }

    public class StorageStatusDto
    {
        [JsonPropertyName("mountPoint")]
        public string MountPoint { get; set; } = "/var/lib/console";

        [JsonPropertyName("totalBytes")]
        public long TotalBytes { get; set; }

        [JsonPropertyName("usedBytes")]
        public long UsedBytes { get; set; }

        [JsonPropertyName("freeBytes")]
        public long FreeBytes { get; set; }
    }

    public class GameSessionDto
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("titleId")]
        public string TitleId { get; set; } = string.Empty;

        [JsonPropertyName("titleName")]
        public string TitleName { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = "Stopped"; // "Stopped", "Starting", "Running", "Stopping", "Crashed"

        [JsonPropertyName("startTimeUtc")]
        public DateTime? StartTimeUtc { get; set; }

        [JsonPropertyName("endTimeUtc")]
        public DateTime? EndTimeUtc { get; set; }

        [JsonPropertyName("exitCode")]
        public int? ExitCode { get; set; }

        [JsonPropertyName("logPath")]
        public string LogPath { get; set; } = string.Empty;
    }

    public class CrashReportDto
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("titleId")]
        public string TitleId { get; set; } = string.Empty;

        [JsonPropertyName("titleName")]
        public string TitleName { get; set; } = string.Empty;

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; }

        [JsonPropertyName("errorCategory")]
        public string ErrorCategory { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("logPath")]
        public string LogPath { get; set; } = string.Empty;
    }

    public class InputDeviceDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "Gamepad"; // "Gamepad", "Keyboard", "Joystick"

        [JsonPropertyName("connected")]
        public bool Connected { get; set; }
    }

    public class SettingsDto
    {
        [JsonPropertyName("cpuBackend")]
        public string CpuBackend { get; set; } = "Auto";

        [JsonPropertyName("graphicsBackend")]
        public string GraphicsBackend { get; set; } = "Vulkan";

        [JsonPropertyName("vsync")]
        public bool VSync { get; set; } = true;

        [JsonPropertyName("resolutionScale")]
        public float ResolutionScale { get; set; } = 1.0f;

        [JsonPropertyName("audioVolume")]
        public float AudioVolume { get; set; } = 1.0f;

        [JsonPropertyName("systemLanguage")]
        public string SystemLanguage { get; set; } = "AmericanEnglish";

        [JsonPropertyName("systemRegion")]
        public string SystemRegion { get; set; } = "USA";

        [JsonPropertyName("systemTimeZone")]
        public string SystemTimeZone { get; set; } = "UTC";

        [JsonPropertyName("dockedMode")]
        public bool DockedMode { get; set; } = true;
    }

    public class InstallResultDto
    {
        [JsonPropertyName("operationId")]
        public string OperationId { get; set; } = string.Empty;

        [JsonPropertyName("contentType")]
        public string ContentType { get; set; } = "base"; // "keys", "firmware", "base", "update", "dlc"

        [JsonPropertyName("titleId")]
        public string TitleId { get; set; } = string.Empty;

        [JsonPropertyName("titleName")]
        public string TitleName { get; set; } = string.Empty;

        [JsonPropertyName("titleIdBase")]
        public string? TitleIdBase { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "INSTALLED"; // "INSTALLED", "ALREADY_INSTALLED", "UPDATED", "SKIPPED_SAME_VERSION", "FAILED"

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = [];

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    public class InstallProgressDto
    {
        [JsonPropertyName("operationId")]
        public string OperationId { get; set; } = string.Empty;

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = "validating"; // "validating", "extracting", "installing", "verifying", "committing"

        [JsonPropertyName("bytesProcessed")]
        public long BytesProcessed { get; set; }

        [JsonPropertyName("bytesTotal")]
        public long BytesTotal { get; set; }

        [JsonPropertyName("percent")]
        public float Percent { get; set; }

        [JsonPropertyName("currentItem")]
        public string CurrentItem { get; set; } = string.Empty;
    }

    /// <summary>
    /// Installer state published by console-usb-installer daemon for Prompt 7 frontend subscription.
    /// </summary>
    public class InstallerStatusDto
    {
        [JsonPropertyName("transactionId")]
        public string TransactionId { get; set; } = string.Empty;

        /// <summary>Installer state: idle, detected, mounting, preparing, checking_backend,
        /// checking_keys, installing_keys, preflight, installing_firmware, installing_games,
        /// installing_patches, verifying, refreshing_library, finalizing, unmounting,
        /// success, failed</summary>
        [JsonPropertyName("state")]
        public string State { get; set; } = "idle";

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonPropertyName("currentFile")]
        public string CurrentFile { get; set; } = string.Empty;

        [JsonPropertyName("itemIndex")]
        public int ItemIndex { get; set; }

        [JsonPropertyName("itemCount")]
        public int ItemCount { get; set; }

        [JsonPropertyName("percent")]
        public float Percent { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("errorCode")]
        public string ErrorCode { get; set; }

        [JsonPropertyName("safeToRemove")]
        public bool SafeToRemove { get; set; }

        [JsonPropertyName("deviceNode")]
        public string DeviceNode { get; set; } = string.Empty;

        [JsonPropertyName("mountPath")]
        public string MountPath { get; set; } = string.Empty;

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Event notification broadcast over /run/console/installer.sock for Prompt 7 frontend.
    /// </summary>
    public class InstallerEventDto
    {
        [JsonPropertyName("transactionId")]
        public string TransactionId { get; set; } = string.Empty;

        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public InstallerStatusDto Status { get; set; }

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }
}
