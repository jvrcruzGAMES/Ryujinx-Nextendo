namespace Ryujinx.Cpu.LinuxKvm
{
    public static class KvmConstants
    {
        public const int KvmApiVersion = 12;

        // KVM Extensions / Capabilities
        public const int KVM_CAP_ARM_PSCI_0_2 = 102;
        public const int KVM_CAP_ARM_VM_IPA_SIZE = 165;
        public const int KVM_CAP_IMMEDIATE_EXIT = 136;
        public const int KVM_CAP_ONE_REG = 70;
        public const int KVM_CAP_USER_MEMORY = 3;

        // KVM Memory Region Flags
        public const uint KVM_MEM_LOG_DIRTY_PAGES = 1u << 0;
        public const uint KVM_MEM_READONLY = 1u << 1;

        // KVM VCPU Targets
        public const uint KVM_ARM_TARGET_AEM_V8 = 0;
        public const uint KVM_ARM_TARGET_FOUNDATION_V8 = 1;
        public const uint KVM_ARM_TARGET_CORTEX_A57 = 2;
        public const uint KVM_ARM_TARGET_XGENE_POTENZA = 3;
        public const uint KVM_ARM_TARGET_CORTEX_A53 = 4;
        public const uint KVM_ARM_TARGET_GENERIC_V8 = 5;

        // KVM VCPU Features
        public const int KVM_ARM_VCPU_POWER_OFF = 0;
        public const int KVM_ARM_VCPU_EL1_32BIT = 1;
        public const int KVM_ARM_VCPU_PSCI_0_2 = 2;
        public const int KVM_ARM_VCPU_PMU_V3 = 3;

        // KVM Exit Reasons
        public const uint KVM_EXIT_UNKNOWN = 0;
        public const uint KVM_EXIT_EXCEPTION = 1;
        public const uint KVM_EXIT_IO = 2;
        public const uint KVM_EXIT_HYPERCALL = 3;
        public const uint KVM_EXIT_DEBUG = 4;
        public const uint KVM_EXIT_HLT = 5;
        public const uint KVM_EXIT_MMIO = 6;
        public const uint KVM_EXIT_IRQ_WINDOW_OPEN = 7;
        public const uint KVM_EXIT_SHUTDOWN = 8;
        public const uint KVM_EXIT_FAIL_ENTRY = 9;
        public const uint KVM_EXIT_INTR = 10;
        public const uint KVM_EXIT_SET_TPR = 11;
        public const uint KVM_EXIT_TPR_ACCESS = 12;
        public const uint KVM_EXIT_S390_SIEIC = 13;
        public const uint KVM_EXIT_S390_RESET = 14;
        public const uint KVM_EXIT_DCR = 15;
        public const uint KVM_EXIT_NMI = 16;
        public const uint KVM_EXIT_INTERNAL_ERROR = 17;
        public const uint KVM_EXIT_OSI = 18;
        public const uint KVM_EXIT_PAPR_HCALL = 19;
        public const uint KVM_EXIT_S390_UCONTROL = 20;
        public const uint KVM_EXIT_WATCHDOG = 21;
        public const uint KVM_EXIT_S390_TSCH = 22;
        public const uint KVM_EXIT_EPR = 23;
        public const uint KVM_EXIT_SYSTEM_EVENT = 24;
        public const uint KVM_EXIT_ARM_NISV = 28;

        // ARM64 Register ID Prefixes
        public const ulong KVM_REG_ARM64 = 0x6000000000000000UL;
        public const ulong KVM_REG_SIZE_U32 = 0x0020000000000000UL;
        public const ulong KVM_REG_SIZE_U64 = 0x0030000000000000UL;
        public const ulong KVM_REG_SIZE_U128 = 0x0040000000000000UL;

        public const ulong KVM_REG_ARM_COPROC_SHIFT = 16;
        public const ulong KVM_REG_ARM_CORE = 0x0010UL << (int)KVM_REG_ARM_COPROC_SHIFT;
        public const ulong KVM_REG_ARM64_SYSREG = 0x0013UL << (int)KVM_REG_ARM_COPROC_SHIFT;

        // Core Register IDs
        // offset in struct kvm_regs / 4
        // regs.regs[0..30] -> offset (0..30) * 8 -> / 4 = 0..60 (step 2)
        public static ulong KvmRegArm64X(int index) => KVM_REG_ARM64 | KVM_REG_SIZE_U64 | KVM_REG_ARM_CORE | (ulong)(uint)(index * 2);
        public const ulong KvmRegArm64Sp = KVM_REG_ARM64 | KVM_REG_SIZE_U64 | KVM_REG_ARM_CORE | 62UL;
        public const ulong KvmRegArm64Pc = KVM_REG_ARM64 | KVM_REG_SIZE_U64 | KVM_REG_ARM_CORE | 64UL;
        public const ulong KvmRegArm64Pstate = KVM_REG_ARM64 | KVM_REG_SIZE_U64 | KVM_REG_ARM_CORE | 66UL;

        // FP/SIMD Register IDs
        // fp_regs.vregs[0..31] -> offset 328 + i * 16 -> / 4 = 82 + i * 4
        public static ulong KvmRegArm64V(int index) => KVM_REG_ARM64 | KVM_REG_SIZE_U128 | KVM_REG_ARM_CORE | (ulong)(uint)(82 + index * 4);
        public const ulong KvmRegArm64Fpsr = KVM_REG_ARM64 | KVM_REG_SIZE_U32 | KVM_REG_ARM_CORE | 210UL;
        public const ulong KvmRegArm64Fpcr = KVM_REG_ARM64 | KVM_REG_SIZE_U32 | KVM_REG_ARM_CORE | 211UL;

        // System Register Encoding Helper
        public static ulong KvmRegArm64SysReg(int op0, int op1, int crn, int crm, int op2)
        {
            return KVM_REG_ARM64 | KVM_REG_SIZE_U64 | KVM_REG_ARM64_SYSREG |
                   ((ulong)(op0 & 0x3) << 14) |
                   ((ulong)(op1 & 0x7) << 11) |
                   ((ulong)(crn & 0xF) << 7) |
                   ((ulong)(crm & 0xF) << 3) |
                   ((ulong)(op2 & 0x7) << 0);
        }

        // Common System Registers
        public static readonly ulong KvmRegArm64TpidrEl0 = KvmRegArm64SysReg(3, 3, 13, 0, 2);
        public static readonly ulong KvmRegArm64TpidrroEl0 = KvmRegArm64SysReg(3, 3, 13, 0, 3);
        public static readonly ulong KvmRegArm64CpacrEl1 = KvmRegArm64SysReg(3, 0, 1, 0, 2);
        public static readonly ulong KvmRegArm64CntvCtlEl0 = KvmRegArm64SysReg(3, 3, 14, 3, 1);
        public static readonly ulong KvmRegArm64CntvCvalEl0 = KvmRegArm64SysReg(3, 3, 14, 3, 0);
        public static readonly ulong KvmRegArm64CntfrqEl0 = KvmRegArm64SysReg(3, 3, 14, 0, 0);
        public static readonly ulong KvmRegArm64MidrEl1 = KvmRegArm64SysReg(3, 0, 0, 0, 0);

        // Exception & MMU System Registers
        public static readonly ulong KvmRegArm64ElrEl1 = KvmRegArm64SysReg(3, 0, 4, 0, 1);
        public static readonly ulong KvmRegArm64EsrEl1 = KvmRegArm64SysReg(3, 0, 5, 2, 0);
        public static readonly ulong KvmRegArm64FarEl1 = KvmRegArm64SysReg(3, 0, 6, 0, 0);
        public static readonly ulong KvmRegArm64SpsrEl1 = KvmRegArm64SysReg(3, 0, 4, 0, 0);
        public static readonly ulong KvmRegArm64VbarEl1 = KvmRegArm64SysReg(3, 0, 12, 0, 0);
        public static readonly ulong KvmRegArm64Ttbr0El1 = KvmRegArm64SysReg(3, 0, 2, 0, 0);
        public static readonly ulong KvmRegArm64Ttbr1El1 = KvmRegArm64SysReg(3, 0, 2, 0, 1);
        public static readonly ulong KvmRegArm64TcrEl1 = KvmRegArm64SysReg(3, 0, 2, 0, 2);
        public static readonly ulong KvmRegArm64MairEl1 = KvmRegArm64SysReg(3, 0, 10, 2, 0);
        public static readonly ulong KvmRegArm64SctlrEl1 = KvmRegArm64SysReg(3, 0, 1, 0, 0);
        public static readonly ulong KvmRegArm64MdscrEl1 = KvmRegArm64SysReg(3, 0, 0, 2, 2);
        public static readonly ulong KvmRegArm64SpEl0 = KvmRegArm64SysReg(3, 0, 4, 1, 0);
    }
}
