using ARMeilleure.Memory;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    public class KvmEngine : ICpuEngine
    {
        private readonly ITickSource _tickSource;

        public KvmEngine(ITickSource tickSource)
        {
            _tickSource = tickSource;
        }

        public ICpuContext CreateCpuContext(IMemoryManager memoryManager, bool for64Bit)
        {
            if (!for64Bit)
            {
                throw new NotSupportedException("32-bit ARM execution is not supported on Linux KVM backend.");
            }

            return new KvmCpuContext(_tickSource, memoryManager, for64Bit);
        }
    }
}
