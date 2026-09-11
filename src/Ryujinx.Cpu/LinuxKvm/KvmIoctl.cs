namespace Ryujinx.Cpu.LinuxKvm
{
    public static class KvmIoctl
    {
        private const uint _IOC_NRBITS = 8;
        private const uint _IOC_TYPEBITS = 8;
        private const uint _IOC_SIZEBITS = 14;
        private const uint _IOC_DIRBITS = 2;

        private const uint _IOC_NRSHIFT = 0;
        private const uint _IOC_TYPESHIFT = _IOC_NRSHIFT + _IOC_NRBITS;
        private const uint _IOC_SIZESHIFT = _IOC_TYPESHIFT + _IOC_TYPEBITS;
        private const uint _IOC_DIRSHIFT = _IOC_SIZESHIFT + _IOC_SIZEBITS;

        private const uint _IOC_NONE = 0;
        private const uint _IOC_WRITE = 1;
        private const uint _IOC_READ = 2;

        private const uint KVMIO = 0xAE;

        private static ulong _IOC(uint dir, uint type, uint nr, uint size)
        {
            return ((ulong)dir << (int)_IOC_DIRSHIFT) |
                   ((ulong)type << (int)_IOC_TYPESHIFT) |
                   ((ulong)nr << (int)_IOC_NRSHIFT) |
                   ((ulong)size << (int)_IOC_SIZESHIFT);
        }

        private static ulong _IO(uint type, uint nr) => _IOC(_IOC_NONE, type, nr, 0);
        private static ulong _IOR(uint type, uint nr, uint size) => _IOC(_IOC_READ, type, nr, size);
        private static ulong _IOW(uint type, uint nr, uint size) => _IOC(_IOC_WRITE, type, nr, size);
        private static ulong _IOWR(uint type, uint nr, uint size) => _IOC(_IOC_READ | _IOC_WRITE, type, nr, size);

        // KVM System IOCTLs (operated on /dev/kvm fd)
        // KVM_GET_API_VERSION = _IO(KVMIO, 0x00) -> 0xAE00
        public static readonly ulong KVM_GET_API_VERSION = _IO(KVMIO, 0x00);

        // KVM_CREATE_VM = _IO(KVMIO, 0x01) -> 0xAE01
        public static readonly ulong KVM_CREATE_VM = _IO(KVMIO, 0x01);

        // KVM_GET_VCPU_MMAP_SIZE = _IO(KVMIO, 0x04) -> 0xAE04
        public static readonly ulong KVM_GET_VCPU_MMAP_SIZE = _IO(KVMIO, 0x04);

        // KVM_CHECK_EXTENSION = _IO(KVMIO, 0x03) -> 0xAE03
        public static readonly ulong KVM_CHECK_EXTENSION = _IO(KVMIO, 0x03);

        // KVM VM IOCTLs (operated on vm_fd)
        // KVM_SET_USER_MEMORY_REGION = _IOW(KVMIO, 0x46, sizeof(struct kvm_userspace_memory_region)) -> _IOW(0xAE, 0x46, 32)
        public static readonly ulong KVM_SET_USER_MEMORY_REGION = _IOW(KVMIO, 0x46, 32);

        // KVM_CREATE_VCPU = _IO(KVMIO, 0x41) -> 0xAE41
        public static readonly ulong KVM_CREATE_VCPU = _IO(KVMIO, 0x41);

        // KVM_ARM_PREFERRED_TARGET = _IOR(KVMIO, 0xaf, sizeof(struct kvm_vcpu_init)) -> _IOR(0xAE, 0xaf, 32)
        public static readonly ulong KVM_ARM_PREFERRED_TARGET = _IOR(KVMIO, 0xAF, 32);

        // KVM VCPU IOCTLs (operated on vcpu_fd)
        // KVM_RUN = _IO(KVMIO, 0x80) -> 0xAE80
        public static readonly ulong KVM_RUN = _IO(KVMIO, 0x80);

        // KVM_ARM_VCPU_INIT = _IOW(KVMIO, 0xae, sizeof(struct kvm_vcpu_init)) -> _IOW(0xAE, 0xae, 32)
        public static readonly ulong KVM_ARM_VCPU_INIT = _IOW(KVMIO, 0xAE, 32);

        // KVM_GET_ONE_REG = _IOW(KVMIO, 0xab, sizeof(struct kvm_one_reg)) -> _IOW(0xAE, 0xab, 16)
        public static readonly ulong KVM_GET_ONE_REG = _IOW(KVMIO, 0xAB, 16);

        // KVM_SET_ONE_REG = _IOW(KVMIO, 0xac, sizeof(struct kvm_one_reg)) -> _IOW(0xAE, 0xac, 16)
        public static readonly ulong KVM_SET_ONE_REG = _IOW(KVMIO, 0xAC, 16);
    }
}
