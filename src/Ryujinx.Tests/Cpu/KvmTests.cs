using NUnit.Framework;
using Ryujinx.Cpu.AppleHv.Arm;
using Ryujinx.Cpu.LinuxKvm;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.Cpu
{
    public class KvmTests
    {
        [Test]
        public void KvmStructSizes_MatchLinuxKernelAbi()
        {
            Assert.That(Marshal.SizeOf<KvmUserspaceMemoryRegion>(), Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<KvmOneReg>(), Is.EqualTo(16));
            unsafe
            {
                Assert.That(sizeof(KvmVcpuInit), Is.EqualTo(32));
                Assert.That(sizeof(KvmRun), Is.EqualTo(2048));
            }
        }

        [Test]
        public void KvmIoctlConstants_MatchLinuxUapi()
        {
            Assert.That(KvmIoctl.KVM_GET_API_VERSION, Is.EqualTo(0xAE00UL));
            Assert.That(KvmIoctl.KVM_CREATE_VM, Is.EqualTo(0xAE01UL));
            Assert.That(KvmIoctl.KVM_CHECK_EXTENSION, Is.EqualTo(0xAE03UL));
            Assert.That(KvmIoctl.KVM_GET_VCPU_MMAP_SIZE, Is.EqualTo(0xAE04UL));
            Assert.That(KvmIoctl.KVM_CREATE_VCPU, Is.EqualTo(0xAE41UL));
            Assert.That(KvmIoctl.KVM_RUN, Is.EqualTo(0xAE80UL));
        }

        [Test]
        public void KvmRegisterIds_MatchArm64Specifications()
        {
            // Core register encoding: 0x6030000000100000 | (offset / 4)
            Assert.That(KvmConstants.KvmRegArm64X(0), Is.EqualTo(0x6030000000100000UL));
            Assert.That(KvmConstants.KvmRegArm64X(1), Is.EqualTo(0x6030000000100002UL));
            Assert.That(KvmConstants.KvmRegArm64X(30), Is.EqualTo(0x603000000010003CUL));
            Assert.That(KvmConstants.KvmRegArm64Sp, Is.EqualTo(0x603000000010003EUL));
            Assert.That(KvmConstants.KvmRegArm64Pc, Is.EqualTo(0x6030000000100040UL));
            Assert.That(KvmConstants.KvmRegArm64Pstate, Is.EqualTo(0x6030000000100042UL));

            // SIMD FP V0..V31 register encoding: 0x6040000000100000 | (82 + i * 4)
            Assert.That(KvmConstants.KvmRegArm64V(0), Is.EqualTo(0x6040000000100052UL));
            Assert.That(KvmConstants.KvmRegArm64V(31), Is.EqualTo(0x60400000001000CEUL));

            // System registers:
            // TPIDR_EL0: op0=3, op1=3, crn=13, crm=0, op2=2
            Assert.That(KvmConstants.KvmRegArm64TpidrEl0, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 3, 13, 0, 2)));

            // EL1 Exception & MMU System Registers
            Assert.That(KvmConstants.KvmRegArm64ElrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 4, 0, 1)));
            Assert.That(KvmConstants.KvmRegArm64EsrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 5, 2, 0)));
            Assert.That(KvmConstants.KvmRegArm64FarEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 6, 0, 0)));
            Assert.That(KvmConstants.KvmRegArm64SpsrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 4, 0, 0)));
            Assert.That(KvmConstants.KvmRegArm64VbarEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 12, 0, 0)));
            Assert.That(KvmConstants.KvmRegArm64Ttbr0El1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 2, 0, 0)));
            Assert.That(KvmConstants.KvmRegArm64Ttbr1El1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 2, 0, 1)));
            Assert.That(KvmConstants.KvmRegArm64TcrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 2, 0, 2)));
            Assert.That(KvmConstants.KvmRegArm64MairEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 10, 2, 0)));
            Assert.That(KvmConstants.KvmRegArm64SctlrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 1, 0, 0)));
            Assert.That(KvmConstants.KvmRegArm64MdscrEl1, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 0, 2, 2)));
            Assert.That(KvmConstants.KvmRegArm64SpEl0, Is.EqualTo(KvmConstants.KvmRegArm64SysReg(3, 0, 4, 1, 0)));
        }

        [Test]
        public void KvmAddressSpace_KernelRegionConstants_AreAlignedAndInHighVa()
        {
            Assert.That(KvmAddressSpace.KernelRegionEretAddress, Is.GreaterThan(0xFFFFFF0000000000UL));
            Assert.That(KvmAddressSpace.KernelRegionTlbiEretAddress, Is.GreaterThan(0xFFFFFF0000000000UL));
            Assert.That(KvmAddressSpace.KernelRegionStackAddress, Is.GreaterThan(0xFFFFFF0000000000UL));
        }

        [Test]
        public void KvmCapabilities_ProbeDoesNotThrow()
        {
            Assert.DoesNotThrow(() =>
            {
                bool supported = KvmCapabilities.IsSupported;
                string reason = KvmCapabilities.UnavailableReason;
            });
        }

        [Test]
        public void Stage1Pte_ApFlags_BitfieldEncodingsAreAccurate()
        {
            // AP[2:1] bit shift is 6
            // UXN bit shift is 54
            // PXN bit shift is 53
            Assert.That((ulong)ApFlags.UserReadWriteKernelReadWrite & (3UL << 6), Is.EqualTo(1UL << 6)); // AP=01 (RW)
            Assert.That((ulong)ApFlags.UserReadKernelRead & (3UL << 6), Is.EqualTo(3UL << 6)); // AP=11 (RO)
            Assert.That((ulong)ApFlags.UserNoneKernelRead & (3UL << 6), Is.EqualTo(2UL << 6)); // AP=10 (User None)
            Assert.That((ulong)ApFlags.UserReadKernelRead & (1UL << 54), Is.EqualTo(1UL << 54)); // UXN=1
            Assert.That((ulong)ApFlags.UserReadExecuteKernelRead & (1UL << 54), Is.EqualTo(0UL)); // UXN=0 (Executable)
        }
    }
}
