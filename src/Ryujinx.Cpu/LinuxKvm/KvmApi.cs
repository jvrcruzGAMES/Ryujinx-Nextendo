using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Cpu.LinuxKvm
{
    [SupportedOSPlatform("linux")]
    public static unsafe class KvmApi
    {
        private const string LibC = "libc";

        public const int O_RDWR = 0x0002;
        public const int O_CLOEXEC = 0x80000;

        public const int PROT_READ = 0x1;
        public const int PROT_WRITE = 0x2;
        public const int PROT_EXEC = 0x4;

        public const int MAP_SHARED = 0x01;
        public const int MAP_PRIVATE = 0x02;
        public const int MAP_ANONYMOUS = 0x20;
        public const int MAP_NORESERVE = 0x4000;

        public static readonly IntPtr MAP_FAILED = new(-1);

        [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

        [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int fd);

        [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
        public static extern int Ioctl(int fd, ulong request);

        [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
        public static extern int Ioctl(int fd, ulong request, int arg);

        [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
        public static extern int Ioctl(int fd, ulong request, ulong arg);

        [DllImport(LibC, EntryPoint = "ioctl", SetLastError = true)]
        public static extern int Ioctl(int fd, ulong request, void* arg);

        [DllImport(LibC, EntryPoint = "mmap", SetLastError = true)]
        public static extern IntPtr Mmap(IntPtr addr, nuint length, int prot, int flags, int fd, long offset);

        [DllImport(LibC, EntryPoint = "munmap", SetLastError = true)]
        public static extern int Munmap(IntPtr addr, nuint length);

        [DllImport(LibC, EntryPoint = "mprotect", SetLastError = true)]
        public static extern int Mprotect(IntPtr addr, nuint length, int prot);

        public static void CheckResult(int result, string operation)
        {
            if (result < 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new KvmException(operation, errno);
            }
        }
    }
}
