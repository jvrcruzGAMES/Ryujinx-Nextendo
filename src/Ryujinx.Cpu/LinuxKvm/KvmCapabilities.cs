using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    public static class KvmCapabilities
    {
        private static bool? _isSupported;
        private static string _unavailableReason;
        private static readonly object _lock = new();

        public static bool IsSupported
        {
            get
            {
                EnsureProbed();
                return _isSupported.GetValueOrDefault();
            }
        }

        public static string UnavailableReason
        {
            get
            {
                EnsureProbed();
                return _unavailableReason;
            }
        }

        public static void EnsureProbed()
        {
            if (_isSupported.HasValue) return;

            lock (_lock)
            {
                if (_isSupported.HasValue) return;

                if (!OperatingSystem.IsLinux())
                {
                    _isSupported = false;
                    _unavailableReason = "Host OS is not Linux.";
                    return;
                }

                if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                {
                    _isSupported = false;
                    _unavailableReason = $"Host architecture is {RuntimeInformation.ProcessArchitecture}, expected ARM64 for direct AArch64 hardware execution.";
                    return;
                }

                if (!File.Exists("/dev/kvm"))
                {
                    _isSupported = false;
                    _unavailableReason = "/dev/kvm device node is missing. Ensure KVM kernel module is loaded (or run on hardware exposing KVM).";
                    return;
                }

                int kvmFd = KvmApi.Open("/dev/kvm", KvmApi.O_RDWR | KvmApi.O_CLOEXEC);
                if (kvmFd < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    _isSupported = false;
                    _unavailableReason = $"Failed to open /dev/kvm (errno {errno}). Check read/write permissions on /dev/kvm.";
                    return;
                }

                try
                {
                    int apiVersion = KvmApi.Ioctl(kvmFd, KvmIoctl.KVM_GET_API_VERSION);
                    if (apiVersion != KvmConstants.KvmApiVersion)
                    {
                        _isSupported = false;
                        _unavailableReason = $"Unsupported KVM API version: {apiVersion}, expected {KvmConstants.KvmApiVersion}.";
                        return;
                    }

                    // Check basic VM creation capability
                    int vmFd = KvmApi.Ioctl(kvmFd, KvmIoctl.KVM_CREATE_VM, 0);
                    if (vmFd < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        _isSupported = false;
                        _unavailableReason = $"KVM_CREATE_VM failed (errno {errno}).";
                        return;
                    }

                    try
                    {
                        unsafe
                        {
                            KvmVcpuInit preferredTarget = default;
                            int targetRes = KvmApi.Ioctl(vmFd, KvmIoctl.KVM_ARM_PREFERRED_TARGET, &preferredTarget);
                            if (targetRes < 0)
                            {
                                int errno = Marshal.GetLastPInvokeError();
                                _isSupported = false;
                                _unavailableReason = $"KVM_ARM_PREFERRED_TARGET failed (errno {errno}).";
                                return;
                            }
                        }
                    }
                    finally
                    {
                        KvmApi.Close(vmFd);
                    }

                    _isSupported = true;
                    _unavailableReason = null;
                }
                catch (Exception ex)
                {
                    _isSupported = false;
                    _unavailableReason = $"Unexpected error during KVM capability probing: {ex.Message}";
                }
                finally
                {
                    KvmApi.Close(kvmFd);
                }
            }
        }
    }
}
