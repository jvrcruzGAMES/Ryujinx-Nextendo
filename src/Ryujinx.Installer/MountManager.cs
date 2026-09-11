using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer
{
    /// <summary>
    /// Manages safe mounting and unmounting of USB block devices.
    /// Creates dedicated per-device mount points at /run/media/usb/<devname>.
    /// Supports FAT32, exFAT, ext4, NTFS.
    /// </summary>
    public class MountManager : IDisposable
    {
        public const string MountRoot = "/run/media/usb";
        private static readonly string[] SupportedFsTypes = ["vfat", "exfat", "ext4", "ntfs3", "ntfs"];
        private readonly Dictionary<string, string> _activeMounts = new();
        private readonly object _lock = new();

        public void EnsureMountRootExists()
        {
            Directory.CreateDirectory(MountRoot);
        }

        /// <summary>
        /// Attempts to mount a block device. Returns mount path or null on failure.
        /// Tries each supported filesystem type in order until one succeeds.
        /// </summary>
        public MountResult TryMount(string deviceNode, string? preferredFsType = null)
        {
            if (string.IsNullOrEmpty(deviceNode))
                return MountResult.Fail("Device node is empty", ErrorCodes.InvalidPath);

            // Canonicalize and sanitize device node name for mount path component
            string devName = Path.GetFileName(deviceNode);
            if (string.IsNullOrEmpty(devName) || devName.Contains('/') || devName.Contains('\\') || devName.Contains('\0'))
                return MountResult.Fail("Invalid device node name", ErrorCodes.InvalidPath);

            string mountPath = Path.Combine(MountRoot, devName);

            lock (_lock)
            {
                if (_activeMounts.ContainsKey(deviceNode))
                    return MountResult.Success(mountPath, _activeMounts[deviceNode]);
            }

            EnsureMountRootExists();
            Directory.CreateDirectory(mountPath);

            // Determine filesystem types to try
            string[] fsTypes = preferredFsType != null
                ? [preferredFsType]
                : SupportedFsTypes;

            foreach (string fsType in fsTypes)
            {
                string[] mountArgs = BuildMountArgs(deviceNode, mountPath, fsType);
                (int exitCode, string stderr) = RunProcess("/bin/mount", mountArgs);

                if (exitCode == 0)
                {
                    // Verify we can write (not read-only)
                    string testFile = Path.Combine(mountPath, $".switchpi-rw-test-{Guid.NewGuid():N}");
                    try
                    {
                        File.WriteAllText(testFile, "rw");
                        File.Delete(testFile);
                    }
                    catch
                    {
                        // Unmount and report write-protected
                        RunProcess("/bin/umount", [mountPath]);
                        CleanMountDirectory(mountPath);
                        return MountResult.Fail("Filesystem is read-only; cannot write result flags", ErrorCodes.WriteProtected);
                    }

                    string uuid = DetectUuid(deviceNode);
                    lock (_lock)
                    {
                        _activeMounts[deviceNode] = mountPath;
                    }

                    return MountResult.Success(mountPath, uuid, fsType);
                }
            }

            CleanMountDirectory(mountPath);
            return MountResult.Fail($"Could not mount {deviceNode} with any supported filesystem", ErrorCodes.UnsupportedFs);
        }

        private static string[] BuildMountArgs(string deviceNode, string mountPath, string fsType)
        {
            // nodev,nosuid,noexec are safe defaults; allow rw for result flag writing
            string options = fsType switch
            {
                "vfat" => "rw,nodev,nosuid,noexec,uid=0,gid=0,umask=022,shortname=mixed",
                "exfat" => "rw,nodev,nosuid,noexec,uid=0,gid=0,umask=022",
                "ext4" => "rw,nodev,nosuid,noexec",
                "ntfs3" => "rw,nodev,nosuid,noexec",
                "ntfs" => "rw,nodev,nosuid,noexec",
                _ => "rw,nodev,nosuid,noexec"
            };
            return ["-t", fsType, "-o", options, deviceNode, mountPath];
        }

        public void Unmount(string deviceNode)
        {
            string? mountPath;
            lock (_lock)
            {
                if (!_activeMounts.TryGetValue(deviceNode, out mountPath))
                    return;
            }

            // sync before unmount
            RunProcess("/bin/sync", []);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                (int code, _) = RunProcess("/bin/umount", [mountPath]);
                if (code == 0) break;
                Thread.Sleep(500);
            }

            lock (_lock)
            {
                _activeMounts.Remove(deviceNode);
            }

            CleanMountDirectory(mountPath);
        }

        private static void CleanMountDirectory(string mountPath)
        {
            try
            {
                if (Directory.Exists(mountPath))
                    Directory.Delete(mountPath, recursive: false);
            }
            catch { }
        }

        /// <summary>Detect filesystem UUID via blkid. Argument-array invocation, no shell interpolation.</summary>
        public static string DetectUuid(string deviceNode)
        {
            (int code, string stdout) = RunProcess("/sbin/blkid", ["-s", "UUID", "-o", "value", deviceNode]);
            return code == 0 ? stdout.Trim() : string.Empty;
        }

        /// <summary>Detect filesystem type via blkid.</summary>
        public static string DetectFsType(string deviceNode)
        {
            (int code, string stdout) = RunProcess("/sbin/blkid", ["-s", "TYPE", "-o", "value", deviceNode]);
            return code == 0 ? stdout.Trim() : string.Empty;
        }

        /// <summary>
        /// Detect if device is removable by reading /sys/block/.../removable.
        /// Avoids modifying fixed system partitions.
        /// </summary>
        public static bool IsRemovable(string deviceNode)
        {
            // /dev/sda1 -> /sys/block/sda/removable
            string devName = Path.GetFileName(deviceNode);
            // Strip trailing partition number to get base device
            string baseDevice = devName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            string sysPath = $"/sys/block/{baseDevice}/removable";

            try
            {
                string content = File.ReadAllText(sysPath).Trim();
                return content == "1";
            }
            catch
            {
                // If we can't determine, fall back to checking /sys/block/*/uevent
                return false;
            }
        }

        private static (int exitCode, string stdout) RunProcess(string program, string[] args)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(program)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (string a in args) psi.ArgumentList.Add(a);

                using var proc = System.Diagnostics.Process.Start(psi)!;
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return (proc.ExitCode, stdout);
            }
            catch
            {
                return (-1, string.Empty);
            }
        }

        public void Dispose()
        {
            List<string> devices;
            lock (_lock)
            {
                devices = [.. _activeMounts.Keys];
            }
            foreach (string dev in devices)
                Unmount(dev);
        }
    }

    public class MountResult
    {
        public bool Ok { get; private init; }
        public string Path { get; private init; } = string.Empty;
        public string Uuid { get; private init; } = string.Empty;
        public string FsType { get; private init; } = string.Empty;
        public string ErrorMessage { get; private init; } = string.Empty;
        public string ErrorCode { get; private init; } = string.Empty;

        public static MountResult Success(string path, string uuid = "", string fsType = "")
            => new() { Ok = true, Path = path, Uuid = uuid, FsType = fsType };

        public static MountResult Fail(string message, string errorCode)
            => new() { Ok = false, ErrorMessage = message, ErrorCode = errorCode };
    }
}
