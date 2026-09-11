using Ryujinx.Common.Logging;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    static class KvmVm
    {
        private const ulong AsIpaAlignment = 1UL << 30;

        private static int _addressSpaces;
        private static int _kvmFd = -1;
        private static int _vmFd = -1;
        private static uint _nextSlot = 0;
        private static KvmIpaAllocator _ipaAllocator;
        private static readonly Dictionary<ulong, uint> _memslots = new();
        private static readonly Lock _lock = new();

        public static int VmFd => _vmFd;
        public static int KvmFd => _kvmFd;

        public static (ulong, KvmIpaAllocator) CreateAddressSpace(MemoryBlock block)
        {
            KvmIpaAllocator ipaAllocator;

            lock (_lock)
            {
                if (++_addressSpaces == 1)
                {
                    _kvmFd = KvmApi.Open("/dev/kvm", KvmApi.O_RDWR | KvmApi.O_CLOEXEC);
                    KvmApi.CheckResult(_kvmFd, "open(/dev/kvm)");

                    _vmFd = KvmApi.Ioctl(_kvmFd, KvmIoctl.KVM_CREATE_VM, 0);
                    KvmApi.CheckResult(_vmFd, "ioctl(KVM_CREATE_VM)");

                    _nextSlot = 0;
                    _memslots.Clear();
                    _ipaAllocator = ipaAllocator = new KvmIpaAllocator();

                    Logger.Info?.Print(LogClass.Cpu, $"[KVM] Created KVM VM (vmFd={_vmFd})");
                }
                else
                {
                    ipaAllocator = _ipaAllocator;
                }
            }

            ulong baseAddress;

            lock (ipaAllocator)
            {
                baseAddress = ipaAllocator.Allocate(block.Size, AsIpaAlignment);
            }

            MapUserMemoryRegion((ulong)block.Pointer, baseAddress, block.Size, false);

            return (baseAddress, ipaAllocator);
        }

        public static void MapUserMemoryRegion(ulong hostAddress, ulong guestPhysAddr, ulong size, bool readOnly)
        {
            lock (_lock)
            {
                uint slot = _nextSlot++;
                _memslots[guestPhysAddr] = slot;

                unsafe
                {
                    KvmUserspaceMemoryRegion region = new()
                    {
                        Slot = slot,
                        Flags = readOnly ? KvmConstants.KVM_MEM_READONLY : 0,
                        GuestPhysAddr = guestPhysAddr,
                        MemorySize = size,
                        UserspaceAddr = hostAddress,
                    };

                    int res = KvmApi.Ioctl(_vmFd, KvmIoctl.KVM_SET_USER_MEMORY_REGION, &region);
                    KvmApi.CheckResult(res, $"ioctl(KVM_SET_USER_MEMORY_REGION, slot={slot}, gpa=0x{guestPhysAddr:X}, size=0x{size:X})");
                }
            }
        }

        public static void UnmapUserMemoryRegion(ulong guestPhysAddr, ulong size)
        {
            lock (_lock)
            {
                if (_memslots.Remove(guestPhysAddr, out uint slot))
                {
                    unsafe
                    {
                        KvmUserspaceMemoryRegion region = new()
                        {
                            Slot = slot,
                            Flags = 0,
                            GuestPhysAddr = guestPhysAddr,
                            MemorySize = 0, // size 0 deletes the slot
                            UserspaceAddr = 0,
                        };

                        int res = KvmApi.Ioctl(_vmFd, KvmIoctl.KVM_SET_USER_MEMORY_REGION, &region);
                        KvmApi.CheckResult(res, $"ioctl(KVM_SET_USER_MEMORY_REGION [delete], slot={slot})");
                    }
                }
            }
        }

        public static void DestroyAddressSpace(ulong address, ulong size)
        {
            UnmapUserMemoryRegion(address, size);

            KvmIpaAllocator ipaAllocator;

            lock (_lock)
            {
                if (--_addressSpaces == 0)
                {
                    if (_vmFd >= 0)
                    {
                        KvmApi.Close(_vmFd);
                        _vmFd = -1;
                    }

                    if (_kvmFd >= 0)
                    {
                        KvmApi.Close(_kvmFd);
                        _kvmFd = -1;
                    }

                    Logger.Info?.Print(LogClass.Cpu, "[KVM] Destroyed KVM VM");
                }

                ipaAllocator = _ipaAllocator;
            }

            lock (ipaAllocator)
            {
                ipaAllocator.Free(address, size);
            }
        }
    }
}
