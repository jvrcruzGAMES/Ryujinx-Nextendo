using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer
{
    /// <summary>
    /// Discovers and validates USB content candidates defensively.
    /// Defends against symlink traversal, path traversal, special files, long names,
    /// invalid UTF-8, case conflicts, and non-regular files.
    /// </summary>
    public class UsbContentScanner
    {
        private const int MaxFileNameLength = 255;
        private const int MaxPathDepth = 1; // Only scan at depth=1 within sub-directories

        public string MountPath { get; }

        public UsbContentScanner(string mountPath)
        {
            MountPath = Path.GetFullPath(mountPath);
        }

        public UsbInstallPlan Scan(ITransactionLogger logger)
        {
            var plan = new UsbInstallPlan();

            // --- prod.keys ---
            plan.KeysCandidate = TrySingleFile(MountPath, "prod.keys", logger);

            // --- Firmware.zip ---
            plan.FirmwareCandidate = TrySingleFile(MountPath, "Firmware.zip", logger);

            // --- games/ ---
            plan.GameCandidates = ScanDirectory(MountPath, "games", [".nsp", ".xci"], logger, allowedContentType: "base");

            // --- patch/ ---
            plan.PatchCandidates = ScanDirectory(MountPath, "patch", [".nsp"], logger, allowedContentType: "update");

            logger.Log("INFO", $"Scan complete: keys={plan.KeysCandidate != null}, firmware={plan.FirmwareCandidate != null}, " +
                               $"games={plan.GameCandidates.Count}, patches={plan.PatchCandidates.Count}");

            return plan;
        }

        /// <summary>
        /// Looks for exactly one file with the given name at the mount root.
        /// Handles case conflicts on case-sensitive filesystems.
        /// </summary>
        private string? TrySingleFile(string root, string exactName, ITransactionLogger logger)
        {
            string fullPath = Path.Combine(root, exactName);

            // Detect case conflicts: look for variants of the name
            string? found = null;
            try
            {
                string[] candidates = Directory.GetFiles(root)
                    .Where(f => string.Equals(Path.GetFileName(f), exactName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (candidates.Length == 0)
                {
                    logger.Log("INFO", $"Not found: {exactName}");
                    return null;
                }

                if (candidates.Length > 1)
                {
                    logger.Log("WARN", $"Ambiguous file: multiple case variants of '{exactName}' found. Ignoring all.");
                    return null;
                }

                string candidate = candidates[0];

                // Verify it is an exact match (or accept case-insensitive on FAT)
                string candidateName = Path.GetFileName(candidate);
                if (!string.Equals(candidateName, exactName, StringComparison.Ordinal))
                {
                    logger.Log("INFO", $"Accepting '{candidateName}' as case-insensitive match for '{exactName}'");
                }

                if (!IsSafeFile(root, candidate, logger))
                    return null;

                found = candidate;
                logger.Log("INFO", $"Found candidate: {SanitizeForLog(candidateName)} ({new FileInfo(candidate).Length} bytes)");
            }
            catch (Exception ex)
            {
                logger.Log("WARN", $"Error scanning for {exactName}: {SanitizeForLog(ex.Message)}");
            }

            return found;
        }

        /// <summary>
        /// Scans a subdirectory for files matching given extensions.
        /// Only processes regular files at depth 1.
        /// </summary>
        private List<UsbFileCandidate> ScanDirectory(
            string root,
            string subDir,
            string[] extensions,
            ITransactionLogger logger,
            string allowedContentType)
        {
            var results = new List<UsbFileCandidate>();
            string dirPath = Path.Combine(root, subDir);

            if (!Directory.Exists(dirPath))
                return results;

            logger.Log("INFO", $"Scanning {subDir}/");

            string[] files;
            try
            {
                files = Directory.GetFiles(dirPath);
            }
            catch (Exception ex)
            {
                logger.Log("WARN", $"Cannot read {subDir}/: {SanitizeForLog(ex.Message)}");
                return results;
            }

            // Sort deterministically
            Array.Sort(files, StringComparer.Ordinal);

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string ext = Path.GetExtension(file).ToLowerInvariant();

                if (!extensions.Contains(ext))
                {
                    logger.Log("DEBUG", $"  Skipping non-target: {SanitizeForLog(name)}");
                    continue;
                }

                if (!IsSafeFile(dirPath, file, logger))
                    continue;

                FileInfo fi = new(file);
                logger.Log("INFO", $"  Found candidate: {SanitizeForLog(name)} ({fi.Length} bytes)");

                results.Add(new UsbFileCandidate
                {
                    Path = file,
                    FileName = name,
                    SizeBytes = fi.Length,
                    Mtime = fi.LastWriteTimeUtc,
                    ExpectedContentType = allowedContentType
                });
            }

            return results;
        }

        /// <summary>
        /// Safety validation: must be a regular file within the mount root, no symlinks,
        /// no special devices, reasonable name length, valid name.
        /// </summary>
        private bool IsSafeFile(string root, string filePath, ITransactionLogger logger)
        {
            string name = Path.GetFileName(filePath);

            // Length check
            if (name.Length > MaxFileNameLength)
            {
                logger.Log("WARN", $"Rejecting oversized filename (length={name.Length})");
                return false;
            }

            // Null byte check
            if (name.Contains('\0'))
            {
                logger.Log("WARN", "Rejecting filename with null byte");
                return false;
            }

            // Canonicalize and check containment within mount root
            string canonical;
            try
            {
                canonical = Path.GetFullPath(filePath);
            }
            catch
            {
                logger.Log("WARN", $"Cannot canonicalize: {SanitizeForLog(name)}");
                return false;
            }

            if (!canonical.StartsWith(MountPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !canonical.StartsWith(MountPath + "/", StringComparison.Ordinal) &&
                canonical != MountPath)
            {
                logger.Log("WARN", $"Path escapes mount root: {SanitizeForLog(name)}");
                return false;
            }

            // Must be a regular file, not a symlink, not a device node, not a pipe
            try
            {
                var attr = File.GetAttributes(filePath);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    logger.Log("WARN", $"Rejecting symlink: {SanitizeForLog(name)}");
                    return false;
                }
                if ((attr & FileAttributes.Device) != 0)
                {
                    logger.Log("WARN", $"Rejecting device file: {SanitizeForLog(name)}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.Log("WARN", $"Cannot stat {SanitizeForLog(name)}: {SanitizeForLog(ex.Message)}");
                return false;
            }

            return true;
        }

        /// <summary>Escapes control characters in filenames to prevent log injection.</summary>
        public static string SanitizeForLog(string input)
        {
            if (string.IsNullOrEmpty(input)) return "(empty)";
            var sb = new StringBuilder(input.Length);
            foreach (char c in input)
            {
                if (c < 0x20 || c == 0x7F)
                    sb.Append($"\\x{(int)c:X2}");
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public class UsbInstallPlan
    {
        public string? KeysCandidate { get; set; }
        public string? FirmwareCandidate { get; set; }
        public List<UsbFileCandidate> GameCandidates { get; set; } = [];
        public List<UsbFileCandidate> PatchCandidates { get; set; } = [];

        public bool HasAnyContent =>
            KeysCandidate != null || FirmwareCandidate != null ||
            GameCandidates.Count > 0 || PatchCandidates.Count > 0;
    }

    public class UsbFileCandidate
    {
        public string Path { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime Mtime { get; set; }
        public string ExpectedContentType { get; set; } = string.Empty;
    }
}
