using ARMeilleure.Memory;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    class KvmCpuContext : ICpuContext
    {
        private readonly ITickSource _tickSource;
        private readonly KvmMemoryManager _memoryManager;

        public KvmCpuContext(ITickSource tickSource, IMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _memoryManager = (KvmMemoryManager)memory;
        }

        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            return new KvmExecutionContext(_tickSource, exceptionCallbacks);
        }

        public void Execute(IExecutionContext context, ulong address)
        {
            ((KvmExecutionContext)context).Execute(_memoryManager, address);
        }

        public void InvalidateCacheRegion(ulong address, ulong size)
        {
        }

        public IDiskCacheLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled, string cacheSelector)
        {
            return new DummyDiskCacheLoadState();
        }

        public void PrepareCodeRange(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
        }
    }
}
