using Ryujinx.Cpu.AppleHv.Arm;
using Ryujinx.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    public class KvmAddressSpace : IDisposable
    {
        private const ulong KernelRegionBase = unchecked((ulong)-(1L << 39));
        private const ulong KernelRegionCodeOffset = 0UL;
        private const ulong KernelRegionCodeSize = 0x2000UL;
        private const ulong KernelRegionTlbiEretOffset = KernelRegionCodeOffset + 0x1000UL;
        private const ulong KernelRegionEretOffset = KernelRegionTlbiEretOffset + 4UL;
        private const ulong KernelRegionStackOffset = 0x2000UL;
        private const ulong KernelRegionStackSize = 0x1000UL;

        public const ulong KernelRegionEretAddress = KernelRegionBase + KernelRegionEretOffset;
        public const ulong KernelRegionTlbiEretAddress = KernelRegionBase + KernelRegionTlbiEretOffset;
        public const ulong KernelRegionStackAddress = KernelRegionBase + KernelRegionStackOffset + KernelRegionStackSize;

        private const ulong AllocationGranule = 1UL << 14;

        private readonly ulong _asBase;
        private readonly ulong _backingSize;
        private readonly ulong _tcrEl1;

        private readonly KvmAddressSpaceRange _userRange;
        private readonly KvmAddressSpaceRange _kernelRange;

        private readonly MemoryBlock _kernelCodeBlock;
        private readonly MemoryBlock _kernelStackBlock;

        public ulong UserIpaBase => _userRange.GetIpaBase();

        public KvmAddressSpace(MemoryBlock backingMemory, ulong asSize)
        {
            (_asBase, KvmIpaAllocator ipaAllocator) = KvmVm.CreateAddressSpace(backingMemory);
            _backingSize = backingMemory.Size;

            int asBits = 39;
            ulong temp = 1UL << 39;
            while (temp > asSize && asBits > 32)
            {
                temp >>= 1;
                asBits--;
            }
            if (temp < asSize)
            {
                asBits++;
            }
            asBits = Math.Clamp(asBits, 36, 39);

            ulong t0sz = (ulong)(64 - asBits);
            // Base TCR: TG1=4K (0b10<<30), TG0=4K (0b00<<14), SH0=IS (3<<12), ORGN0=WB/WA (1<<10), IRGN0=WB/WA (1<<8), IPS=40-bit (2<<32)
            // TCR_EL1 = 0x00000011B5193500 | (T1SZ=25 << 16) | T0SZ
            _tcrEl1 = 0x00000011B5193500UL | (25UL << 16) | (t0sz & 0x3FUL);

            _userRange = new KvmAddressSpaceRange(ipaAllocator);
            _kernelRange = new KvmAddressSpaceRange(ipaAllocator);

            _kernelCodeBlock = new MemoryBlock(AllocationGranule);
            _kernelStackBlock = new MemoryBlock(AllocationGranule);

            InitializeKernelCode(ipaAllocator);
        }

        private void InitializeKernelCode(KvmIpaAllocator ipaAllocator)
        {
            // 1. Current EL with SP0 (Offsets 0x000, 0x080, 0x100, 0x180) -> Fatal trap
            for (ulong offset = 0x000; offset < 0x200; offset += 0x80)
            {
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset, 0xD41FE002u); // HVC #0xFF00
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset + 4, 0xD69F03E0u); // ERET
            }

            // 2. Current EL with SPx (Offsets 0x200, 0x280, 0x300, 0x380) -> Fatal trap
            for (ulong offset = 0x200; offset < 0x400; offset += 0x80)
            {
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset, 0xD41FE402u); // HVC #0xFF20
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset + 4, 0xD69F03E0u); // ERET
            }

            // 3. Lower EL using AArch64 (Offsets 0x400, 0x480, 0x500, 0x580)
            // 0x400: Lower EL AArch64 Synchronous Exception (SVC, BRK, Data/Instr abort, Step, SysReg)
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x400, 0xD41FFFE2u); // HVC #0xFFFF
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x400 + 4, 0xD69F03E0u); // ERET

            // 0x480: Lower EL AArch64 IRQ (Virtual timer / interrupt)
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x480, 0xD41FFFC2u); // HVC #0xFFFE
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x480 + 4, 0xD69F03E0u); // ERET

            // 0x500: Lower EL AArch64 FIQ
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x500, 0xD41FFFA2u); // HVC #0xFFFD
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x500 + 4, 0xD69F03E0u); // ERET

            // 0x580: Lower EL AArch64 SError
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x580, 0xD41FFF82u); // HVC #0xFFFC
            _kernelCodeBlock.Write(KernelRegionCodeOffset + 0x580 + 4, 0xD69F03E0u); // ERET

            // 4. Lower EL using AArch32 (Offsets 0x600, 0x680, 0x700, 0x780) -> Unsupported / Panic
            for (ulong offset = 0x600; offset < 0x800; offset += 0x80)
            {
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset, 0xD41FE642u); // HVC #0xFF32
                _kernelCodeBlock.Write(KernelRegionCodeOffset + offset + 4, 0xD69F03E0u); // ERET
            }

            // Return / TLBI trampolines
            _kernelCodeBlock.Write(KernelRegionTlbiEretOffset, 0xD508831Fu); // TLBI VMALLE1IS
            _kernelCodeBlock.Write(KernelRegionEretOffset, 0xD69F03E0u); // ERET

            // Map code block
            ulong kernelCodePa = ipaAllocator.Allocate(AllocationGranule);
            KvmVm.MapUserMemoryRegion((ulong)_kernelCodeBlock.Pointer, kernelCodePa, AllocationGranule, false);
            _kernelRange.Map(KernelRegionCodeOffset, kernelCodePa, KernelRegionCodeSize, ApFlags.UserNoneKernelReadExecute);

            // Map supervisor stack block
            ulong kernelStackPa = ipaAllocator.Allocate(AllocationGranule);
            KvmVm.MapUserMemoryRegion((ulong)_kernelStackBlock.Pointer, kernelStackPa, AllocationGranule, false);
            _kernelRange.Map(KernelRegionStackOffset, kernelStackPa, KernelRegionStackSize, ApFlags.UserNoneKernelReadWrite);
        }

        internal void InitializeMmu(KvmVcpu vcpu)
        {
            // Set System registers for MMU, Exception Base Vector Address, and EL1 Stack
            vcpu.SetSysReg(KvmConstants.KvmRegArm64VbarEl1, KernelRegionBase + KernelRegionCodeOffset);
            vcpu.SetSysReg(KvmConstants.KvmRegArm64Sp, KernelRegionStackAddress); // SP_EL1
            vcpu.SetSysReg(KvmConstants.KvmRegArm64CpacrEl1, 3UL << 20); // FPEN = 0b11 (Enable FP/SIMD)
            vcpu.SetSysReg(KvmConstants.KvmRegArm64Ttbr0El1, _userRange.GetIpaBase());
            vcpu.SetSysReg(KvmConstants.KvmRegArm64Ttbr1El1, _kernelRange.GetIpaBase());
            vcpu.SetSysReg(KvmConstants.KvmRegArm64MairEl1, 0xffUL);
            vcpu.SetSysReg(KvmConstants.KvmRegArm64TcrEl1, _tcrEl1);
            vcpu.SetSysReg(KvmConstants.KvmRegArm64SctlrEl1, 0x0000000034D5D925UL);
        }

        public bool GetAndClearUserTlbInvalidationPending()
        {
            return _userRange.GetAndClearTlbInvalidationPending();
        }

        public void MapUser(ulong va, ulong pa, ulong size, MemoryPermission permission)
        {
            pa += _asBase;

            lock (_userRange)
            {
                _userRange.Map(va, pa, size, GetApFlags(permission));
            }
        }

        public void UnmapUser(ulong va, ulong size)
        {
            lock (_userRange)
            {
                _userRange.Unmap(va, size);
            }
        }

        public void ReprotectUser(ulong va, ulong size, MemoryPermission permission)
        {
            lock (_userRange)
            {
                _userRange.Reprotect(va, size, GetApFlags(permission));
            }
        }

        public PageTableWalkResult WalkUserPageTable(ulong va)
        {
            lock (_userRange)
            {
                return _userRange.WalkPageTable(va);
            }
        }

        private static ApFlags GetApFlags(MemoryPermission permission)
        {
            return permission switch
            {
                MemoryPermission.None => ApFlags.UserNoneKernelRead,
                MemoryPermission.Execute => ApFlags.UserExecuteKernelRead,
                MemoryPermission.Read => ApFlags.UserReadKernelRead,
                MemoryPermission.ReadAndWrite => ApFlags.UserReadWriteKernelReadWrite,
                MemoryPermission.ReadAndExecute => ApFlags.UserReadExecuteKernelRead,
                MemoryPermission.ReadWriteExecute => ApFlags.UserReadWriteExecuteKernelReadWrite,
                _ => throw new ArgumentException($"Permission \"{permission}\" is invalid."),
            };
        }

        public void Dispose()
        {
            _userRange.Dispose();
            _kernelRange.Dispose();
            KvmVm.DestroyAddressSpace(_asBase, _backingSize);
        }
    }
}
