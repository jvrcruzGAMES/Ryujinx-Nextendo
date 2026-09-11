using Ryujinx.Common.Logging;
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    class KvmVcpuPool
    {
        private const int MaxActiveVcpus = 4;

        public static readonly KvmVcpuPool Instance = new();

        private int _totalVcpus;
        private readonly int _maxVcpus;
        private readonly nuint _vcpuMmapSize;

        public KvmVcpuPool()
        {
            int kvmFd = KvmVm.KvmFd;
            if (kvmFd >= 0)
            {
                int mmapSize = KvmApi.Ioctl(kvmFd, KvmIoctl.KVM_GET_VCPU_MMAP_SIZE, 0);
                _vcpuMmapSize = mmapSize > 0 ? (nuint)mmapSize : 4096;
            }
            else
            {
                _vcpuMmapSize = 4096;
            }

            _maxVcpus = 256; // Standard Linux KVM maximum VCPUs limit
        }

        public KvmVcpu Create(KvmAddressSpace addressSpace, IKvmExecutionContext shadowContext, Action<IKvmExecutionContext> swapContext)
        {
            KvmVcpu vcpu = CreateNew(addressSpace, shadowContext);
            vcpu.NativeContext.Load(shadowContext);
            swapContext(vcpu.NativeContext);
            return vcpu;
        }

        public void Destroy(KvmVcpu vcpu, Action<IKvmExecutionContext> swapContext)
        {
            vcpu.ShadowContext.Load(vcpu.NativeContext);
            swapContext(vcpu.ShadowContext);
            DestroyVcpu(vcpu);
        }

        public void Return(KvmVcpu vcpu, Action<IKvmExecutionContext> swapContext)
        {
            if (vcpu.IsEphemeral)
            {
                Destroy(vcpu, swapContext);
            }
        }

        public KvmVcpu Rent(KvmAddressSpace addressSpace, IKvmExecutionContext shadowContext, KvmVcpu vcpu, Action<IKvmExecutionContext> swapContext)
        {
            if (vcpu == null || vcpu.IsEphemeral)
            {
                return Create(addressSpace, shadowContext, swapContext);
            }
            else
            {
                return vcpu;
            }
        }

        private unsafe KvmVcpu CreateNew(KvmAddressSpace addressSpace, IKvmExecutionContext shadowContext)
        {
            int newCount = IncrementVcpuCount();
            bool isEphemeral = newCount > _maxVcpus - MaxActiveVcpus;

            int vmFd = KvmVm.VmFd;
            int vcpuFd = KvmApi.Ioctl(vmFd, KvmIoctl.KVM_CREATE_VCPU, newCount - 1);
            KvmApi.CheckResult(vcpuFd, $"ioctl(KVM_CREATE_VCPU, id={newCount - 1})");

            // Query preferred target and initialize VCPU
            KvmVcpuInit init = default;
            int targetRes = KvmApi.Ioctl(vmFd, KvmIoctl.KVM_ARM_PREFERRED_TARGET, &init);
            KvmApi.CheckResult(targetRes, "ioctl(KVM_ARM_PREFERRED_TARGET)");

            init.Features[0] |= 1u << KvmConstants.KVM_ARM_VCPU_PSCI_0_2;

            int initRes = KvmApi.Ioctl(vcpuFd, KvmIoctl.KVM_ARM_VCPU_INIT, &init);
            KvmApi.CheckResult(initRes, "ioctl(KVM_ARM_VCPU_INIT)");

            // Mmap KvmRun area
            IntPtr runPtr = KvmApi.Mmap(IntPtr.Zero, _vcpuMmapSize, KvmApi.PROT_READ | KvmApi.PROT_WRITE, KvmApi.MAP_SHARED, vcpuFd, 0);
            if (runPtr == KvmApi.MAP_FAILED)
            {
                KvmApi.Close(vcpuFd);
                DecrementVcpuCount();
                throw new KvmException("mmap(kvm_run)", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }

            KvmRun* exitInfo = (KvmRun*)runPtr;

            // Enable FP and SIMD instructions in CPACR_EL1 (FPEN = 0b11 << 20)
            ulong cpacrVal = 0b11UL << 20;
            KvmOneReg cpacrReg = new()
            {
                Id = KvmConstants.KvmRegArm64CpacrEl1,
                Addr = (ulong)&cpacrVal,
            };
            KvmApi.Ioctl(vcpuFd, KvmIoctl.KVM_SET_ONE_REG, &cpacrReg);

            KvmExecutionContextVcpu nativeContext = new(vcpuFd);
            KvmVcpu vcpu = new(vcpuFd, exitInfo, _vcpuMmapSize, shadowContext, nativeContext, isEphemeral);

            addressSpace.InitializeMmu(vcpu);
            vcpu.EnableAndUpdateVTimer();

            return vcpu;
        }

        private void DestroyVcpu(KvmVcpu vcpu)
        {
            vcpu.Dispose();
            DecrementVcpuCount();
        }

        private int IncrementVcpuCount()
        {
            return Interlocked.Increment(ref _totalVcpus);
        }

        private void DecrementVcpuCount()
        {
            Interlocked.Decrement(ref _totalVcpus);
        }
    }
}
