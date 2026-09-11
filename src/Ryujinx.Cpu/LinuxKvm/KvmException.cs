using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Cpu.LinuxKvm
{
    public class KvmException : Exception
    {
        public string Operation { get; }
        public int Errno { get; }
        public string ErrnoString { get; }

        public KvmException(string operation, int errno, string message = null)
            : base(FormatMessage(operation, errno, message))
        {
            Operation = operation;
            Errno = errno;
            ErrnoString = Marshal.PtrToStringAnsi(strerror(errno)) ?? $"Errno {errno}";
        }

        private static string FormatMessage(string operation, int errno, string message)
        {
            string errStr = Marshal.PtrToStringAnsi(strerror(errno)) ?? $"Errno {errno}";
            if (string.IsNullOrEmpty(message))
            {
                return $"{operation} failed: errno={errno} ({errStr})";
            }
            return $"{operation} failed: errno={errno} ({errStr}) - {message}";
        }

        [DllImport("libc", EntryPoint = "strerror", SetLastError = false)]
        private static extern IntPtr strerror(int errnum);
    }
}
