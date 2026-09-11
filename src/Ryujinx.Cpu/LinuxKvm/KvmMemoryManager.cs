using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto ARM64 Stage-1 page tables and Linux KVM memory slots.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public sealed class KvmMemoryManager : VirtualMemoryManagerRefCountedBase, IMemoryManager, IVirtualMemoryManagerTracked
    {
        private readonly InvalidAccessHandler _invalidAccessHandler;
        private readonly KvmAddressSpace _addressSpace;

        internal KvmAddressSpace AddressSpace => _addressSpace;

        private readonly MemoryBlock _backingMemory;
        private readonly PageTable<ulong> _pageTable;
        private readonly ManagedPageFlags _pages;

        public bool UsesPrivateAllocations => false;
        public int AddressSpaceBits { get; }
        public nint PageTablePointer => nint.Zero;
        public MemoryManagerType Type => MemoryManagerType.SoftwarePageTable;
        public MemoryTracking Tracking { get; }
        public event Action<ulong, ulong> UnmapEvent;

        protected override ulong AddressSpaceSize { get; }

        public KvmMemoryManager(MemoryBlock backingMemory, ulong addressSpaceSize, InvalidAccessHandler invalidAccessHandler = null)
        {
            _backingMemory = backingMemory;
            _pageTable = new PageTable<ulong>();
            _invalidAccessHandler = invalidAccessHandler;
            AddressSpaceSize = addressSpaceSize;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < addressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;
            _pages = new ManagedPageFlags(AddressSpaceBits);
            Tracking = new MemoryTracking(this, PageSize, invalidAccessHandler);
            _addressSpace = new KvmAddressSpace(backingMemory, asSize);
        }

        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            AssertValidAddressAndSize(va, size);

            PtMap(va, pa, size);
            _addressSpace.MapUser(va, pa, size, MemoryPermission.ReadWriteExecute);
            _pages.AddMapping(va, size);

            Tracking.Map(va, size);
        }

        private void PtMap(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        public override void MapForeign(ulong va, nuint hostPointer, ulong size)
        {
            throw new NotSupportedException("MapForeign is not supported in KVM mode.");
        }

        public void Unmap(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);

            _pages.RemoveMapping(va, size);
            _addressSpace.UnmapUser(va, size);
            PtUnmap(va, size);
        }

        private void PtUnmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Unmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        public override T ReadTracked<T>(ulong va)
        {
            try
            {
                return base.ReadTracked<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return default;
            }
        }

        public override void Read(ulong va, Span<byte> data)
        {
            try
            {
                base.Read(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override bool TryReadUnsafe(ulong va, int length, out Span<byte> data)
        {
            data = default;
            if (!IsMapped(va) || !IsRangeMapped(va, (ulong)length))
            {
                return false;
            }

            nuint pa = GetPhysicalAddressChecked(va);
            data = _backingMemory.GetSpan(pa, length);
            return true;
        }

        public override void Write(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                base.Write(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override void WriteUntracked(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                base.WriteUntracked(va, data);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override ReadOnlySequence<byte> GetReadOnlySequence(ulong va, int size, bool tracked = false)
        {
            try
            {
                return base.GetReadOnlySequence(va, size, tracked);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return ReadOnlySequence<byte>.Empty;
            }
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            return ref _backingMemory.GetRef<T>(GetPhysicalAddressChecked(va));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        public bool IsRangeMapped(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                yield break;
            }

            IEnumerable<MemoryRange> guestRegions = GetPhysicalRegionsImpl(va, size);
            if (guestRegions == null)
            {
                yield break;
            }

            foreach (MemoryRange guestRegion in guestRegions)
            {
                nint pointer = _backingMemory.GetPointer(guestRegion.Address, guestRegion.Size);
                yield return new HostMemoryRange((nuint)(ulong)pointer, guestRegion.Size);
            }
        }

        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            if (size == 0)
            {
                yield break;
            }

            foreach (MemoryRange physicalRegion in GetPhysicalRegionsImpl(va, size))
            {
                yield return physicalRegion;
            }
        }

        private IEnumerable<MemoryRange> GetPhysicalRegionsImpl(ulong va, ulong size)
        {
            if (!ValidateAddress(va) || !ValidateAddressAndSize(va, size))
            {
                yield break;
            }

            int pages = GetPagesCount(va, (uint)size, out va);

            ulong regionStart = (ulong)GetPhysicalAddressInternal(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    yield break;
                }

                ulong newPa = (ulong)GetPhysicalAddressInternal(va + PageSize);

                if ((ulong)GetPhysicalAddressInternal(va) + PageSize != newPa)
                {
                    yield return new MemoryRange(regionStart, regionSize);
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            yield return new MemoryRange(regionStart, regionSize);
        }

        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            AssertValidAddressAndSize(va, size);

            _addressSpace.ReprotectUser(va, size, protection);
            _pages.TrackingReprotect(va, size, protection);
        }

        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            if (guest)
            {
                _addressSpace.ReprotectUser(va, size, protection);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginTracking(address, size, id, flags);
        }

        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        public PageTableWalkResult WalkGuestPageTable(ulong va)
        {
            return _addressSpace.WalkUserPageTable(va);
        }

        private nuint GetPhysicalAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private nuint GetPhysicalAddressInternal(ulong va)
        {
            return (nuint)(_pageTable.Read(va) + (va & PageMask));
        }

        protected override void Destroy()
        {
            _addressSpace.Dispose();
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
            => _backingMemory.GetMemory(pa, size);

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
            => _backingMemory.GetSpan(pa, size);

        protected override nuint TranslateVirtualAddressChecked(ulong va)
            => GetPhysicalAddressChecked(va);

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            => GetPhysicalAddressInternal(va);
    }
}
