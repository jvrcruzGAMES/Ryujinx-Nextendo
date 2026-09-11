using ARMeilleure.State;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.AppleHv.Arm;
using Ryujinx.Memory.Tracking;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    class KvmExecutionContext : IExecutionContext
    {
        private long _running;
        private long _interruptRequested;
        private int _shouldStep;

        public bool Running
        {
            get => Interlocked.Read(ref _running) != 0;
            private set => Interlocked.Exchange(ref _running, value ? 1L : 0L);
        }

        private readonly ITickSource _tickSource;
        private readonly ExceptionCallbacks _exceptionCallbacks;
        private readonly KvmExecutionContextShadow _shadow;
        private IKvmExecutionContext _impl;

        public ulong Pc
        {
            get => _impl.Pc;
            set => _impl.Pc = value;
        }

        public ulong DebugPc
        {
            get => _impl.Pc;
            set => _impl.Pc = value;
        }

        public bool IsAarch32
        {
            get => false;
            set
            {
                if (value)
                {
                    throw new NotSupportedException("AArch32 is not supported on Linux KVM backend.");
                }
            }
        }

        public ulong ThreadUid
        {
            get => _impl.ThreadUid;
            set => _impl.ThreadUid = value;
        }

        public ulong Sp
        {
            get => _impl.SpEl0;
            set => _impl.SpEl0 = value;
        }

        public ulong SpEl0
        {
            get => _impl.SpEl0;
            set => _impl.SpEl0 = value;
        }

        public ulong ElrEl1
        {
            get => _impl.ElrEl1;
            set => _impl.ElrEl1 = value;
        }

        public ulong EsrEl1
        {
            get => _impl.EsrEl1;
            set => _impl.EsrEl1 = value;
        }

        public ulong FarEl1
        {
            get => _impl.FarEl1;
            set => _impl.FarEl1 = value;
        }

        public ulong SpsrEl1
        {
            get => _impl.SpsrEl1;
            set => _impl.SpsrEl1 = value;
        }

        public ulong MdscrEl1
        {
            get => _impl.MdscrEl1;
            set => _impl.MdscrEl1 = value;
        }

        public uint Pstate
        {
            get => _impl.Pstate;
            set => _impl.Pstate = value;
        }

        public uint Fpcr
        {
            get => _impl.Fpcr;
            set => _impl.Fpcr = value;
        }

        public uint Fpsr
        {
            get => _impl.Fpsr;
            set => _impl.Fpsr = value;
        }

        public long TpidrEl0
        {
            get => _impl.TpidrEl0;
            set => _impl.TpidrEl0 = value;
        }

        public long TpidrroEl0
        {
            get => _impl.TpidrroEl0;
            set => _impl.TpidrroEl0 = value;
        }

        public ulong GetX(int index) => _impl.GetX(index);
        public void SetX(int index, ulong value) => _impl.SetX(index, value);

        public V128 GetV(int index) => _impl.GetV(index);
        public void SetV(int index, V128 value) => _impl.SetV(index, value);

        public ulong Counter => (ulong)_tickSource.Counter;

        public KvmExecutionContext(ITickSource tickSource, ExceptionCallbacks exceptionCallbacks)
        {
            _tickSource = tickSource;
            _exceptionCallbacks = exceptionCallbacks;
            _shadow = new KvmExecutionContextShadow();
            _impl = _shadow;
        }

        public unsafe void Execute(KvmMemoryManager memoryManager, ulong address)
        {
            KvmVcpu vcpu = KvmVcpuPool.Instance.Create(memoryManager.AddressSpace, _shadow, SwapContext);

            Running = true;
            
            // Set guest entry point in ELR_EL1 and set entry PC to ERET trampoline
            vcpu.NativeContext.ElrEl1 = address;
            vcpu.NativeContext.SpsrEl1 = 0x00000000UL; // EL0t mode
            vcpu.NativeContext.Pc = KvmAddressSpace.KernelRegionEretAddress;

            try
            {
                while (Running)
                {
                    if (Interlocked.CompareExchange(ref _shouldStep, 0, 1) == 1)
                    {
                        uint currentEl = Pstate & ~((uint)ExceptionLevel.PstateMask);
                        if (currentEl == (uint)ExceptionLevel.EL1h)
                        {
                            ulong spsr = vcpu.NativeContext.SpsrEl1;
                            spsr |= 1UL << 21; // SS (Software Step) bit
                            vcpu.NativeContext.SpsrEl1 = spsr;
                        }
                        else
                        {
                            Pstate |= 1U << 21;
                        }

                        vcpu.NativeContext.MdscrEl1 = 1UL; // KDE (Kernel Debug Enable) / SS enable
                    }

                    if (GetAndClearInterruptRequested())
                    {
                        ReturnToPool(vcpu);
                        InterruptHandler();
                        vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);

                        if (!Running)
                        {
                            break;
                        }
                    }

                    int ret = vcpu.Run();

                    if (ret < 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno == 4) // EINTR
                        {
                            ReturnToPool(vcpu);
                            InterruptHandler();
                            vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                            continue;
                        }

                        throw new KvmException("KVM_RUN", errno);
                    }

                    uint exitReason = vcpu.ExitInfo->ExitReason;

                    if (exitReason == KvmConstants.KVM_EXIT_INTR)
                    {
                        if (GetAndClearInterruptRequested())
                        {
                            ReturnToPool(vcpu);
                            InterruptHandler();
                            vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                        }
                    }
                    else if (exitReason == KvmConstants.KVM_EXIT_HYPERCALL ||
                             exitReason == KvmConstants.KVM_EXIT_MMIO ||
                             exitReason == KvmConstants.KVM_EXIT_UNKNOWN)
                    {
                        ulong returnAddress = SynchronousException(memoryManager, ref vcpu);
                        vcpu.NativeContext.Pc = returnAddress;
                    }
                    else if (exitReason == KvmConstants.KVM_EXIT_HLT || exitReason == KvmConstants.KVM_EXIT_SYSTEM_EVENT)
                    {
                        Running = false;
                        break;
                    }
                    else if (exitReason == KvmConstants.KVM_EXIT_FAIL_ENTRY)
                    {
                        ulong failReason = vcpu.ExitInfo->FailEntry.HardwareEntryFailureReason;
                        throw new KvmException("KVM_EXIT_FAIL_ENTRY", (int)failReason, $"Hardware entry failure reason: 0x{failReason:X}");
                    }
                    else if (exitReason == KvmConstants.KVM_EXIT_INTERNAL_ERROR)
                    {
                        uint subError = vcpu.ExitInfo->Internal.SubError;
                        throw new KvmException("KVM_EXIT_INTERNAL_ERROR", (int)subError, $"Internal error subError: 0x{subError:X}");
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.Cpu, $"[KVM] Unexpected exit reason={exitReason} pc=0x{Pc:X} x0=0x{GetX(0):X} x1=0x{GetX(1):X} pstate=0x{Pstate:X}");
                        throw new KvmException("KVM_RUN", (int)exitReason, $"Unhandled exit reason: {exitReason}");
                    }
                }
            }
            finally
            {
                KvmVcpuPool.Instance.Destroy(vcpu, SwapContext);
            }
        }

        private ulong SynchronousException(KvmMemoryManager memoryManager, ref KvmVcpu vcpu)
        {
            ulong elr = vcpu.NativeContext.ElrEl1;
            ulong esr = vcpu.NativeContext.EsrEl1;

            ExceptionClass ec = (ExceptionClass)((uint)esr >> 26);

            switch (ec)
            {
                case ExceptionClass.DataAbortLowerEl:
                    DataAbort(memoryManager.Tracking, vcpu, (uint)esr);
                    break;

                case ExceptionClass.TrappedMsrMrsSystem:
                    InstructionTrap((uint)esr);
                    vcpu.NativeContext.ElrEl1 = elr + 4UL;
                    break;

                case ExceptionClass.SvcAarch64:
                    ReturnToPool(vcpu);
                    ushort imm = (ushort)esr;
                    SupervisorCallHandler(elr - 4UL, imm);
                    Thread.Yield();
                    vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                    break;

                case ExceptionClass.SoftwareStepLowerEl:
                    ulong spsr = vcpu.NativeContext.SpsrEl1;
                    spsr &= ~(1UL << 21); // Clear SS bit
                    vcpu.NativeContext.SpsrEl1 = spsr;
                    vcpu.NativeContext.MdscrEl1 = 0;
                    ReturnToPool(vcpu);
                    StepHandler();
                    vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                    break;

                case ExceptionClass.BrkAarch64:
                    ReturnToPool(vcpu);
                    BreakHandler(elr, (ushort)esr);
                    vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                    break;

                case ExceptionClass.InstructionAbortLowerEl:
                    InstructionAbort(vcpu, (uint)esr, elr);
                    break;

                case ExceptionClass.Unknown:
                    ReturnToPool(vcpu);
                    UndefinedHandler(elr, 0);
                    vcpu = RentFromPool(memoryManager.AddressSpace, vcpu);
                    break;

                default:
                    Logger.Error?.Print(LogClass.Cpu, $"[KVM-EXCEPTION] Unhandled exception class {ec} (ESR: 0x{esr:X16}, ELR: 0x{elr:X16})");
                    throw new Exception($"Unhandled guest exception class {ec} (ESR: 0x{esr:X16}, ELR: 0x{elr:X16})");
            }

            if (memoryManager.AddressSpace.GetAndClearUserTlbInvalidationPending())
            {
                return KvmAddressSpace.KernelRegionTlbiEretAddress;
            }

            return KvmAddressSpace.KernelRegionEretAddress;
        }

        private static void DataAbort(MemoryTracking tracking, KvmVcpu vcpu, uint esr)
        {
            bool write = (esr & (1u << 6)) != 0;
            bool farValid = (esr & (1u << 10)) == 0;
            bool isv = (esr & (1u << 24)) != 0;
            ulong size = isv ? (1UL << (int)((esr >> 22) & 3)) : 4096UL;

            if (farValid)
            {
                ulong far = vcpu.NativeContext.FarEl1;

                if (tracking != null && !tracking.VirtualMemoryEvent(far, size, write))
                {
                    string rw = write ? "write" : "read";
                    throw new Exception($"[KVM] Unhandled invalid memory access at VA 0x{far:X} with size 0x{size:X} ({rw}).");
                }
            }
            else
            {
                throw new Exception($"[KVM] Unhandled invalid memory access at unknown VA with ESR 0x{esr:X}.");
            }
        }

        private static void InstructionAbort(KvmVcpu vcpu, uint esr, ulong elr)
        {
            ulong far = vcpu.NativeContext.FarEl1;
            int ifsc = (int)(esr & 0x3f);
            Logger.Error?.Print(LogClass.Cpu, $"[KVM] Instruction abort at ELR 0x{elr:X16} FAR 0x{far:X16} IFSC 0x{ifsc:X2} ESR 0x{esr:X8}");
            throw new Exception($"[KVM] Unhandled instruction abort at 0x{elr:X16} (FAR: 0x{far:X16}, IFSC: 0x{ifsc:X2}, ESR: 0x{esr:X8})");
        }

        private void InstructionTrap(uint esr)
        {
            bool read = (esr & 1) != 0;
            uint rt = (esr >> 5) & 0x1f;

            if (read)
            {
                // Op0 Op2 Op1 CRn 00000 CRm
                switch ((esr >> 1) & 0x1ffe0f)
                {
                    case 0b11_000_011_1110_00000_0000: // CNTFRQ_EL0
                        WriteRt(rt, _tickSource.Frequency);
                        break;
                    case 0b11_001_011_1110_00000_0000: // CNTPCT_EL0
                        WriteRt(rt, (ulong)_tickSource.Counter);
                        break;
                    case 0b11_010_011_1110_00000_0000: // CNTVCT_EL0
                        WriteRt(rt, (ulong)_tickSource.Counter);
                        break;
                    case 0b11_010_011_1101_00000_0000: // TPIDR_EL0
                        WriteRt(rt, (ulong)TpidrEl0);
                        break;
                    case 0b11_011_011_1101_00000_0000: // TPIDRRO_EL0
                        WriteRt(rt, (ulong)TpidrroEl0);
                        break;
                    default:
                        Logger.Error?.Print(LogClass.Cpu, $"[KVM-EXCEPTION] Unhandled system register read with ESR: 0x{esr:X}");
                        throw new Exception($"Unhandled system register read with ESR: 0x{esr:X}");
                }
            }
            else
            {
                switch ((esr >> 1) & 0x1ffe0f)
                {
                    case 0b11_010_011_1101_00000_0000: // TPIDR_EL0
                        TpidrEl0 = (long)ReadRt(rt);
                        break;
                    default:
                        Logger.Error?.Print(LogClass.Cpu, $"[KVM-EXCEPTION] Unhandled system register write with ESR: 0x{esr:X}");
                        throw new Exception($"Unhandled system register write with ESR: 0x{esr:X}");
                }
            }
        }

        private ulong ReadRt(uint rt)
        {
            if (rt == 31)
            {
                return 0UL;
            }

            return GetX((int)rt);
        }

        private void WriteRt(uint rt, ulong value)
        {
            if (rt != 31)
            {
                SetX((int)rt, value);
            }
        }

        private void SwapContext(IKvmExecutionContext newContext)
        {
            _impl = newContext;
        }

        private void ReturnToPool(KvmVcpu vcpu)
        {
            KvmVcpuPool.Instance.Return(vcpu, SwapContext);
        }

        private KvmVcpu RentFromPool(KvmAddressSpace addressSpace, KvmVcpu vcpu)
        {
            return KvmVcpuPool.Instance.Rent(addressSpace, _shadow, vcpu, SwapContext);
        }

        public void RequestInterrupt()
        {
            Interlocked.Exchange(ref _interruptRequested, 1L);
        }

        public bool GetAndClearInterruptRequested()
        {
            return Interlocked.Exchange(ref _interruptRequested, 0L) != 0;
        }

        public void RequestDebugStep()
        {
            Interlocked.Exchange(ref _shouldStep, 1);
        }

        public void StopRunning()
        {
            Running = false;
        }

        public void Dispose()
        {
        }

        private void BreakHandler(ulong address, int imm) => _exceptionCallbacks.BreakCallback?.Invoke(this, address, imm);
        private void SupervisorCallHandler(ulong address, int imm) => _exceptionCallbacks.SupervisorCallback?.Invoke(this, address, imm);
        private void UndefinedHandler(ulong address, int opCode) => _exceptionCallbacks.UndefinedCallback?.Invoke(this, address, opCode);
        private void InterruptHandler() => _exceptionCallbacks.InterruptCallback?.Invoke(this);
        private void StepHandler() => _exceptionCallbacks.StepCallback?.Invoke(this);
    }
}
