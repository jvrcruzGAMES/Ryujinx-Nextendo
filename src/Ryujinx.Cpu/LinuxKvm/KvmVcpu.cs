using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    unsafe class KvmVcpu : IDisposable
    {
        public readonly int Handle;
        public readonly KvmRun* ExitInfo;
        public readonly nuint MmapSize;
        public readonly IKvmExecutionContext ShadowContext;
        public readonly IKvmExecutionContext NativeContext;
        public readonly bool IsEphemeral;

        public KvmVcpu(
            int handle,
            KvmRun* exitInfo,
            nuint mmapSize,
            IKvmExecutionContext shadowContext,
            IKvmExecutionContext nativeContext,
            bool isEphemeral)
        {
            Handle = handle;
            ExitInfo = exitInfo;
            MmapSize = mmapSize;
            ShadowContext = shadowContext;
            NativeContext = nativeContext;
            IsEphemeral = isEphemeral;
        }

        public void SetSysReg(ulong regId, ulong val)
        {
            KvmOneReg reg = new()
            {
                Id = regId,
                Addr = (ulong)&val,
            };

            int res = KvmApi.Ioctl(Handle, KvmIoctl.KVM_SET_ONE_REG, &reg);
            KvmApi.CheckResult(res, $"ioctl(KVM_SET_ONE_REG, id=0x{regId:X})");
        }

        public ulong GetSysReg(ulong regId)
        {
            ulong val = 0;
            KvmOneReg reg = new()
            {
                Id = regId,
                Addr = (ulong)&val,
            };

            int res = KvmApi.Ioctl(Handle, KvmIoctl.KVM_GET_ONE_REG, &reg);
            KvmApi.CheckResult(res, $"ioctl(KVM_GET_ONE_REG, id=0x{regId:X})");
            return val;
        }

        public int Run()
        {
            if (ExitInfo != null)
            {
                ExitInfo->ImmediateExit = 0;
            }

            return KvmApi.Ioctl(Handle, KvmIoctl.KVM_RUN, 0);
        }

        public void RequestInterrupt()
        {
            if (ExitInfo != null)
            {
                ExitInfo->ImmediateExit = 1;
            }
        }

        public void EnableAndUpdateVTimer()
        {
            // Enable guest virtual timer
            try
            {
                SetSysReg(KvmConstants.KvmRegArm64CntvCtlEl0, 1UL);
            }
            catch
            {
                // Fallback / ignore if timer is managed by in-kernel vGIC
            }
        }

        public void Dispose()
        {
            if (ExitInfo != null && MmapSize > 0)
            {
                KvmApi.Munmap((IntPtr)ExitInfo, MmapSize);
            }

            if (Handle >= 0)
            {
                KvmApi.Close(Handle);
            }
        }
    }
}
