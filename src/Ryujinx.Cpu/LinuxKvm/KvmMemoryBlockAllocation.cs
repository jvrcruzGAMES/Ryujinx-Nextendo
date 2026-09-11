using Ryujinx.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    readonly struct KvmMemoryBlockAllocation : IDisposable
    {
        private readonly KvmMemoryBlockAllocator _owner;
        private readonly KvmMemoryBlockAllocator.Block _block;

        public bool IsValid => _owner != null;
        public MemoryBlock Memory => _block.Memory;
        public ulong Ipa => _block.Ipa;
        public ulong Offset { get; }
        public ulong Size { get; }

        public KvmMemoryBlockAllocation(
            KvmMemoryBlockAllocator owner,
            KvmMemoryBlockAllocator.Block block,
            ulong offset,
            ulong size)
        {
            _owner = owner;
            _block = block;
            Offset = offset;
            Size = size;
        }

        public void Dispose()
        {
            _owner.Free(_block, Offset, Size);
        }
    }
}
