using Ryujinx.Memory;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    class KvmMemoryBlockAllocator : PrivateMemoryAllocatorImpl<KvmMemoryBlockAllocator.Block>
    {
        public class Block : PrivateMemoryAllocator.Block
        {
            private readonly KvmIpaAllocator _ipaAllocator;
            public ulong Ipa { get; }

            public Block(KvmIpaAllocator ipaAllocator, MemoryBlock memory, ulong size) : base(memory, size)
            {
                _ipaAllocator = ipaAllocator;

                lock (ipaAllocator)
                {
                    Ipa = ipaAllocator.Allocate(size);
                }

                KvmVm.MapUserMemoryRegion((ulong)Memory.Pointer, Ipa, size, false);
            }

            public override void Destroy()
            {
                KvmVm.UnmapUserMemoryRegion(Ipa, Size);

                lock (_ipaAllocator)
                {
                    _ipaAllocator.Free(Ipa, Size);
                }

                base.Destroy();
            }
        }

        private readonly KvmIpaAllocator _ipaAllocator;

        public KvmMemoryBlockAllocator(KvmIpaAllocator ipaAllocator, ulong blockAlignment) : base(blockAlignment, MemoryAllocationFlags.None)
        {
            _ipaAllocator = ipaAllocator;
        }

        public KvmMemoryBlockAllocation Allocate(ulong size, ulong alignment)
        {
            Allocation allocation = Allocate(size, alignment, CreateBlock);

            return new KvmMemoryBlockAllocation(this, allocation.Block, allocation.Offset, allocation.Size);
        }

        private Block CreateBlock(MemoryBlock memory, ulong size)
        {
            return new Block(_ipaAllocator, memory, size);
        }
    }
}
