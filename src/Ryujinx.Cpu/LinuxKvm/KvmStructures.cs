using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.LinuxKvm
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KvmUserspaceMemoryRegion
    {
        public uint Slot;
        public uint Flags;
        public ulong GuestPhysAddr;
        public ulong MemorySize;
        public ulong UserspaceAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct KvmVcpuInit
    {
        public uint Target;
        public fixed uint Features[7];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KvmOneReg
    {
        public ulong Id;
        public ulong Addr;
    }

    [StructLayout(LayoutKind.Explicit, Size = 2048)]
    public unsafe struct KvmRun
    {
        // 0x00
        [FieldOffset(0)]
        public byte RequestInterruptWindow;

        [FieldOffset(1)]
        public fixed byte ImmediateExitPadding[7];

        // 0x08
        [FieldOffset(8)]
        public uint ExitReason;

        // 0x0C
        [FieldOffset(12)]
        public byte ReadyForInterruptInjection;

        [FieldOffset(13)]
        public byte IfFlag;

        [FieldOffset(14)]
        public fixed byte Flags[2];

        // 0x10
        [FieldOffset(16)]
        public ulong Cr8;

        // 0x18
        [FieldOffset(24)]
        public ulong ApicBase;

        // Exit union starts at offset 32 (0x20)
        [FieldOffset(32)]
        public KvmExitHw Hardware;

        [FieldOffset(32)]
        public KvmExitFailEntry FailEntry;

        [FieldOffset(32)]
        public KvmExitException Exception;

        [FieldOffset(32)]
        public KvmExitIo Io;

        [FieldOffset(32)]
        public KvmExitMmio Mmio;

        [FieldOffset(32)]
        public KvmExitHypercall Hypercall;

        [FieldOffset(32)]
        public KvmExitSystemEvent SystemEvent;

        [FieldOffset(32)]
        public KvmExitInternalError Internal;

        // immediate_exit flag (used to request interruption)
        // struct kvm_run: __u8 immediate_exit at offset (0x1000 - 1) or offset within header
        // In Linux kernel:
        // offsetof(struct kvm_run, immediate_exit) is at byte 0x12 / field after request_interrupt_window in newer kernels,
        // or accessible via field offset 18.
        [FieldOffset(18)]
        public byte ImmediateExit;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KvmExitHw
    {
        public ulong HardwareExitReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KvmExitFailEntry
    {
        public ulong HardwareEntryFailureReason;
        public uint Cpu;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KvmExitException
    {
        public uint Exception;
        public uint ErrorCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KvmExitIo
    {
        public byte Direction;
        public byte Size;
        public ushort Port;
        public uint Count;
        public ulong DataOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct KvmExitMmio
    {
        public ulong PhysAddr;
        public fixed byte Data[8];
        public uint Len;
        public byte IsWrite;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct KvmExitHypercall
    {
        public ulong Nr;
        public fixed ulong Args[6];
        public ulong Ret;
        public uint LongMode;
        public uint Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct KvmExitSystemEvent
    {
        public uint Type;
        public uint NData;
        public fixed ulong Data[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct KvmExitInternalError
    {
        public uint SubError;
        public uint NData;
        public fixed ulong Data[16];
    }
}
