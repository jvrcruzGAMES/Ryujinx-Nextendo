using ARMeilleure.State;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    class KvmExecutionContextVcpu : IKvmExecutionContext
    {
        public ulong ThreadUid { get; set; }

        private readonly int _vcpuFd;
        private int _interruptRequested;
        private readonly Lock _registerLock = new();

        public ulong Pc
        {
            get => GetReg64(KvmConstants.KvmRegArm64Pc);
            set => SetReg64(KvmConstants.KvmRegArm64Pc, value);
        }

        public ulong ElrEl1
        {
            get => GetReg64(KvmConstants.KvmRegArm64ElrEl1);
            set => SetReg64(KvmConstants.KvmRegArm64ElrEl1, value);
        }

        public ulong EsrEl1
        {
            get => GetReg64(KvmConstants.KvmRegArm64EsrEl1);
            set => SetReg64(KvmConstants.KvmRegArm64EsrEl1, value);
        }

        public ulong FarEl1
        {
            get => GetReg64(KvmConstants.KvmRegArm64FarEl1);
            set => SetReg64(KvmConstants.KvmRegArm64FarEl1, value);
        }

        public ulong SpsrEl1
        {
            get => GetReg64(KvmConstants.KvmRegArm64SpsrEl1);
            set => SetReg64(KvmConstants.KvmRegArm64SpsrEl1, value);
        }

        public ulong MdscrEl1
        {
            get => GetReg64(KvmConstants.KvmRegArm64MdscrEl1);
            set => SetReg64(KvmConstants.KvmRegArm64MdscrEl1, value);
        }

        public ulong SpEl0
        {
            get => GetReg64(KvmConstants.KvmRegArm64SpEl0);
            set => SetReg64(KvmConstants.KvmRegArm64SpEl0, value);
        }

        public long TpidrEl0
        {
            get => (long)GetReg64(KvmConstants.KvmRegArm64TpidrEl0);
            set => SetReg64(KvmConstants.KvmRegArm64TpidrEl0, (ulong)value);
        }

        public long TpidrroEl0
        {
            get => (long)GetReg64(KvmConstants.KvmRegArm64TpidrroEl0);
            set => SetReg64(KvmConstants.KvmRegArm64TpidrroEl0, (ulong)value);
        }

        public uint Pstate
        {
            get => (uint)GetReg64(KvmConstants.KvmRegArm64Pstate);
            set => SetReg64(KvmConstants.KvmRegArm64Pstate, value);
        }

        public uint Fpcr
        {
            get => GetReg32(KvmConstants.KvmRegArm64Fpcr);
            set => SetReg32(KvmConstants.KvmRegArm64Fpcr, value);
        }

        public uint Fpsr
        {
            get => GetReg32(KvmConstants.KvmRegArm64Fpsr);
            set => SetReg32(KvmConstants.KvmRegArm64Fpsr, value);
        }

        public KvmExecutionContextVcpu(int vcpuFd)
        {
            _vcpuFd = vcpuFd;
        }

        public ulong GetX(int index)
        {
            if (index == 31)
            {
                return GetReg64(KvmConstants.KvmRegArm64Sp);
            }

            return GetReg64(KvmConstants.KvmRegArm64X(index));
        }

        public void SetX(int index, ulong value)
        {
            if (index == 31)
            {
                SetReg64(KvmConstants.KvmRegArm64Sp, value);
                return;
            }

            SetReg64(KvmConstants.KvmRegArm64X(index), value);
        }

        public V128 GetV(int index)
        {
            return GetReg128(KvmConstants.KvmRegArm64V(index));
        }

        public void SetV(int index, V128 value)
        {
            SetReg128(KvmConstants.KvmRegArm64V(index), value);
        }

        public void RequestInterrupt()
        {
            Interlocked.Exchange(ref _interruptRequested, 1);
        }

        public bool GetAndClearInterruptRequested()
        {
            return Interlocked.Exchange(ref _interruptRequested, 0) != 0;
        }

        private ulong GetReg64(ulong regId)
        {
            lock (_registerLock)
            {
                ulong val = 0;
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_GET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_GET_ONE_REG, id=0x{regId:X})");
                }
                return val;
            }
        }

        private void SetReg64(ulong regId, ulong val)
        {
            lock (_registerLock)
            {
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_SET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_SET_ONE_REG, id=0x{regId:X})");
                }
            }
        }

        private uint GetReg32(ulong regId)
        {
            lock (_registerLock)
            {
                uint val = 0;
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_GET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_GET_ONE_REG, id=0x{regId:X})");
                }
                return val;
            }
        }

        private void SetReg32(ulong regId, uint val)
        {
            lock (_registerLock)
            {
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_SET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_SET_ONE_REG, id=0x{regId:X})");
                }
            }
        }

        private V128 GetReg128(ulong regId)
        {
            lock (_registerLock)
            {
                V128 val = default;
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_GET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_GET_ONE_REG, id=0x{regId:X})");
                }
                return val;
            }
        }

        private void SetReg128(ulong regId, V128 val)
        {
            lock (_registerLock)
            {
                unsafe
                {
                    KvmOneReg reg = new()
                    {
                        Id = regId,
                        Addr = (ulong)&val,
                    };

                    int res = KvmApi.Ioctl(_vcpuFd, KvmIoctl.KVM_SET_ONE_REG, &reg);
                    KvmApi.CheckResult(res, $"ioctl(KVM_SET_ONE_REG, id=0x{regId:X})");
                }
            }
        }
    }
}
