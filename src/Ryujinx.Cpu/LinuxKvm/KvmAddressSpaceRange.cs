using Ryujinx.Cpu.AppleHv.Arm;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Cpu.LinuxKvm
{
    public struct PageTableWalkResult
    {
        public bool Mapped;
        public int Level;
        public ulong L1Entry;
        public ulong L2Entry;
        public ulong L3Entry;
        public ulong ResolvedIpa;
        public ulong Attributes;
        public ApFlags Ap;

        public override string ToString()
        {
            if (!Mapped) return "Unmapped";
            return $"Level {Level}: IPA 0x{ResolvedIpa:X16}, Attr 0x{Attributes:X}, AP {Ap} (L1: 0x{L1Entry:X16}, L2: 0x{L2Entry:X16}, L3: 0x{L3Entry:X16})";
        }
    }

    [SupportedOSPlatform("linux")]
    class KvmAddressSpaceRange : IDisposable
    {
        private const ulong AllocationGranule = 1UL << 14;

        private const ulong AttributesMask = (0x3ffUL << 2) | (0x3fffUL << 50);

        private const ulong BaseAttributes = (1UL << 10) | (3UL << 8); // Access flag set, inner shareable.

        private const int LevelBits = 9;
        private const int LevelCount = 1 << LevelBits;
        private const int LevelMask = LevelCount - 1;
        private const int PageBits = 12;
        private const int PageSize = 1 << PageBits;
        private const int PageMask = PageSize - 1;
        private const int AllLevelsMask = PageMask | (LevelMask << PageBits) | (LevelMask << (PageBits + LevelBits));

        private class PtLevel
        {
            public ulong Address => Allocation.Ipa + Allocation.Offset;
            public int EntriesCount;
            public readonly KvmMemoryBlockAllocation Allocation;
            public readonly PtLevel[] Next;

            public PtLevel(KvmMemoryBlockAllocator blockAllocator, int count, bool hasNext)
            {
                ulong size = (ulong)count * sizeof(ulong);
                Allocation = blockAllocator.Allocate(size, PageSize);

                AsSpan().Clear();

                if (hasNext)
                {
                    Next = new PtLevel[count];
                }
            }

            public Span<ulong> AsSpan()
            {
                return MemoryMarshal.Cast<byte, ulong>(Allocation.Memory.GetSpan(Allocation.Offset, (int)Allocation.Size));
            }
        }

        private PtLevel _level0;

        private int _tlbInvalidationPending;

        private readonly KvmMemoryBlockAllocator _blockAllocator;

        public KvmAddressSpaceRange(KvmIpaAllocator ipaAllocator)
        {
            _blockAllocator = new KvmMemoryBlockAllocator(ipaAllocator, AllocationGranule);
        }

        public ulong GetIpaBase()
        {
            return EnsureLevel0().Address;
        }

        public bool GetAndClearTlbInvalidationPending()
        {
            return Interlocked.Exchange(ref _tlbInvalidationPending, 0) != 0;
        }

        public void Map(ulong va, ulong pa, ulong size, ApFlags accessPermission)
        {
            MapImpl(va, pa, size, (ulong)accessPermission | BaseAttributes);
        }

        public void Unmap(ulong va, ulong size)
        {
            UnmapImpl(EnsureLevel0(), 0, va, size);
            Interlocked.Exchange(ref _tlbInvalidationPending, 1);
        }

        public void Reprotect(ulong va, ulong size, ApFlags accessPermission)
        {
            UpdateAttributes(va, size, (ulong)accessPermission | BaseAttributes);
        }

        private void MapImpl(ulong va, ulong pa, ulong size, ulong attr)
        {
            PtLevel level0 = EnsureLevel0();

            ulong endVa = va + size;

            while (va < endVa)
            {
                (ulong mapSize, int depth) = GetMapSizeAndDepth(va, pa, endVa);

                PtLevel currentLevel = level0;

                for (int i = 0; i < depth; i++)
                {
                    int l = (int)(va >> (PageBits + (2 - i) * LevelBits)) & LevelMask;
                    EnsureTable(currentLevel, l, i == 0);
                    currentLevel = currentLevel.Next[l];
                }

                (ulong blockSize, int blockShift) = GetBlockSizeAndShift(depth);

                for (ulong i = 0; i < mapSize; i += blockSize)
                {
                    if ((va >> blockShift) << blockShift != va ||
                        (pa >> blockShift) << blockShift != pa)
                    {
                        Debug.Fail($"Block size 0x{blockSize:X} (log2: {blockShift}) is invalid for VA 0x{va:X} or PA 0x{pa:X}.");
                    }

                    WriteBlock(currentLevel, (int)(va >> blockShift) & LevelMask, depth, pa, attr);

                    va += blockSize;
                    pa += blockSize;
                }
            }
        }

        private void UnmapImpl(PtLevel level, int depth, ulong va, ulong size)
        {
            ulong endVa = (va + size + PageMask) & ~((ulong)PageMask);
            va &= ~((ulong)PageMask);

            (ulong blockSize, _) = GetBlockSizeAndShift(depth);

            while (va < endVa)
            {
                ulong nextEntryVa = GetNextAddress(va, blockSize);
                ulong chunkSize = Math.Min(endVa - va, nextEntryVa - va);

                int l = (int)(va >> (PageBits + (2 - depth) * LevelBits)) & LevelMask;

                PtLevel nextTable = level.Next?[l];

                if (nextTable != null)
                {
                    UnmapImpl(nextTable, depth + 1, va, chunkSize);
                }
                else if (chunkSize != blockSize && (level.AsSpan()[l] & 1) != 0)
                {
                    ref ulong pte = ref level.AsSpan()[l];
                    nextTable = CreateTable(pte, depth + 1);
                    level.Next[l] = nextTable;

                    UnmapImpl(nextTable, depth + 1, va, chunkSize);

                    pte = (nextTable.Address & ~(ulong)PageMask) | 3UL;
                }

                if (nextTable == null || nextTable.EntriesCount == 0)
                {
                    if (nextTable != null)
                    {
                        nextTable.Allocation.Dispose();
                        level.Next[l] = null;
                    }

                    if (level.AsSpan()[l] != 0)
                    {
                        level.AsSpan()[l] = 0UL;
                        level.EntriesCount--;
                        ValidateEntriesCount(level.EntriesCount);
                    }
                }

                va += chunkSize;
            }
        }

        private void UpdateAttributes(ulong va, ulong size, ulong newAttr)
        {
            UpdateAttributes(EnsureLevel0(), 0, va, size, newAttr);
            Interlocked.Exchange(ref _tlbInvalidationPending, 1);
        }

        private void UpdateAttributes(PtLevel level, int depth, ulong va, ulong size, ulong newAttr)
        {
            ulong endVa = (va + size + PageSize - 1) & ~((ulong)PageSize - 1);
            va &= ~((ulong)PageSize - 1);

            (ulong blockSize, _) = GetBlockSizeAndShift(depth);

            while (va < endVa)
            {
                ulong nextEntryVa = GetNextAddress(va, blockSize);
                ulong chunkSize = Math.Min(endVa - va, nextEntryVa - va);

                int l = (int)(va >> (PageBits + (2 - depth) * LevelBits)) & LevelMask;

                ref ulong pte = ref level.AsSpan()[l];

                if ((pte & 3) != 0)
                {
                    PtLevel nextTable = level.Next?[l];

                    if (nextTable != null)
                    {
                        UpdateAttributes(nextTable, depth + 1, va, chunkSize, newAttr);
                    }
                    else if (chunkSize != blockSize)
                    {
                        nextTable = CreateTable(pte, depth + 1);
                        level.Next[l] = nextTable;

                        UpdateAttributes(nextTable, depth + 1, va, chunkSize, newAttr);

                        pte = (nextTable.Address & ~(ulong)PageMask) | 3UL;
                    }
                    else
                    {
                        pte = (pte & ~AttributesMask) | newAttr;
                    }
                }

                va += chunkSize;
            }
        }

        private PtLevel CreateTable(ulong pte, int depth)
        {
            pte &= ~3UL;
            pte |= (depth == 2 ? 3UL : 1UL);

            PtLevel level = new(_blockAllocator, LevelCount, depth < 2);
            Span<ulong> currentLevel = level.AsSpan();

            (_, int blockShift) = GetBlockSizeAndShift(depth);

            for (int i = 0; i < LevelCount; i++)
            {
                ulong offset = (ulong)i << blockShift;
                currentLevel[i] = pte + offset;
            }

            level.EntriesCount = LevelCount;

            return level;
        }

        private static (ulong, int) GetBlockSizeAndShift(int depth)
        {
            int blockShift = PageBits + (2 - depth) * LevelBits;
            ulong blockSize = 1UL << blockShift;

            return (blockSize, blockShift);
        }

        private static (ulong, int) GetMapSizeAndDepth(ulong va, ulong pa, ulong endVa)
        {
            ulong combinedAddress = va | pa;

            ulong l0Alignment = 1UL << (PageBits + LevelBits * 2);
            ulong l1Alignment = 1UL << (PageBits + LevelBits);

            if ((combinedAddress & (l0Alignment - 1)) == 0 && AlignDown(endVa, l0Alignment) > va)
            {
                return (AlignDown(endVa, l0Alignment) - va, 0);
            }
            else if ((combinedAddress & (l1Alignment - 1)) == 0 && AlignDown(endVa, l1Alignment) > va)
            {
                ulong nextOrderVa = GetNextAddress(va, l0Alignment);

                if (nextOrderVa <= endVa)
                {
                    return (nextOrderVa - va, 1);
                }
                else
                {
                    return (AlignDown(endVa, l1Alignment) - va, 1);
                }
            }
            else
            {
                ulong nextOrderVa = GetNextAddress(va, l1Alignment);

                if (nextOrderVa <= endVa)
                {
                    return (nextOrderVa - va, 2);
                }
                else
                {
                    return (endVa - va, 2);
                }
            }
        }

        private static ulong AlignDown(ulong va, ulong alignment)
        {
            return va & ~(alignment - 1);
        }

        private static ulong GetNextAddress(ulong va, ulong alignment)
        {
            return (va + alignment) & ~(alignment - 1);
        }

        private PtLevel EnsureLevel0()
        {
            PtLevel level0 = _level0;

            if (level0 == null)
            {
                level0 = new PtLevel(_blockAllocator, LevelCount, true);
                _level0 = level0;
            }

            return level0;
        }

        private void EnsureTable(PtLevel level, int index, bool hasNext)
        {
            Span<ulong> currentTable = level.AsSpan();

            if ((currentTable[index] & 1) == 0)
            {
                PtLevel nextLevel = new(_blockAllocator, LevelCount, hasNext);

                currentTable[index] = (nextLevel.Address & ~(ulong)PageMask) | 3UL;
                level.Next[index] = nextLevel;
                level.EntriesCount++;
                ValidateEntriesCount(level.EntriesCount);
            }
            else if (level.Next[index] == null)
            {
                Debug.Fail($"Index {index} is block, expected a table.");
            }
        }

        private static void WriteBlock(PtLevel level, int index, int depth, ulong pa, ulong attr)
        {
            Span<ulong> currentTable = level.AsSpan();

            currentTable[index] = (pa & ~((ulong)AllLevelsMask >> (depth * LevelBits))) | (depth == 2 ? 3UL : 1UL) | attr;

            level.EntriesCount++;
            ValidateEntriesCount(level.EntriesCount);
        }

        private static void ValidateEntriesCount(int count)
        {
            Debug.Assert(count is >= 0 and <= LevelCount, $"Entries count {count} is invalid.");
        }

        public PageTableWalkResult WalkPageTable(ulong va)
        {
            PageTableWalkResult result = new();
            PtLevel level0 = _level0;
            if (level0 == null)
            {
                return result;
            }

            int l0Index = (int)(va >> (PageBits + 2 * LevelBits)) & LevelMask;
            ulong l0Pte = level0.AsSpan()[l0Index];
            result.L1Entry = l0Pte;

            if ((l0Pte & 1) == 0)
            {
                return result;
            }

            if ((l0Pte & 2) == 0) // 1GB Block (depth 0)
            {
                result.Mapped = true;
                result.Level = 1;
                result.ResolvedIpa = (l0Pte & ~((1UL << (PageBits + 2 * LevelBits)) - 1)) | (va & ((1UL << (PageBits + 2 * LevelBits)) - 1));
                result.Attributes = l0Pte & AttributesMask;
                result.Ap = (ApFlags)(l0Pte & (ulong)ApFlags.UserReadWriteKernelReadWrite);
                return result;
            }

            PtLevel level1 = level0.Next?[l0Index];
            if (level1 == null)
            {
                return result;
            }

            int l1Index = (int)(va >> (PageBits + LevelBits)) & LevelMask;
            ulong l1Pte = level1.AsSpan()[l1Index];
            result.L2Entry = l1Pte;

            if ((l1Pte & 1) == 0)
            {
                return result;
            }

            if ((l1Pte & 2) == 0) // 2MB Block (depth 1)
            {
                result.Mapped = true;
                result.Level = 2;
                result.ResolvedIpa = (l1Pte & ~((1UL << (PageBits + LevelBits)) - 1)) | (va & ((1UL << (PageBits + LevelBits)) - 1));
                result.Attributes = l1Pte & AttributesMask;
                result.Ap = (ApFlags)(l1Pte & (ulong)ApFlags.UserReadWriteKernelReadWrite);
                return result;
            }

            PtLevel level2 = level1.Next?[l1Index];
            if (level2 == null)
            {
                return result;
            }

            int l2Index = (int)(va >> PageBits) & LevelMask;
            ulong l2Pte = level2.AsSpan()[l2Index];
            result.L3Entry = l2Pte;

            if ((l2Pte & 1) != 0 && (l2Pte & 2) != 0) // 4KB Page (depth 2)
            {
                result.Mapped = true;
                result.Level = 3;
                result.ResolvedIpa = (l2Pte & ~(ulong)PageMask) | (va & (ulong)PageMask);
                result.Attributes = l2Pte & AttributesMask;
                result.Ap = (ApFlags)(l2Pte & (ulong)ApFlags.UserReadWriteKernelReadWrite);
            }

            return result;
        }

        public void Dispose()
        {
            _blockAllocator.Dispose();
        }
    }
}
