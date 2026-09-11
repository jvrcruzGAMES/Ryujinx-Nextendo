using System;
using System.IO;
using System.Text;
using System.Threading;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer
{
    /// <summary>
    /// Writes human-readable timestamped log entries to both the USB install.log
    /// and a persistent shadow log in /var/lib/console/installer/logs/.
    /// Progressive flush/fsync at milestones.
    /// </summary>
    public class TransactionLogger : ITransactionLogger, IDisposable
    {
        public const string PersistentLogRoot = "/var/lib/console/installer/logs";

        private readonly string _usbLogPath;
        private readonly string? _shadowLogPath;

        private StreamWriter? _usbWriter;
        private StreamWriter? _shadowWriter;
        private readonly object _lock = new();

        public string TransactionId { get; }

        public TransactionLogger(string transactionId, string usbMountPath, string persistentLogRoot = PersistentLogRoot)
        {
            TransactionId = transactionId;
            _usbLogPath = Path.Combine(usbMountPath, "install.log");

            try
            {
                Directory.CreateDirectory(persistentLogRoot);
                _shadowLogPath = Path.Combine(persistentLogRoot, $"{transactionId}.log");
            }
            catch
            {
                // Fallback gracefully when running unprivileged in test runners
                _shadowLogPath = null;
            }
        }

        /// <summary>Creates fresh install.log on USB. Call before any backend mutation.</summary>
        public void Initialize()
        {
            lock (_lock)
            {
                // Open USB log (overwrite/create)
                _usbWriter = new StreamWriter(_usbLogPath, append: false, Encoding.UTF8)
                    { AutoFlush = false };

                // Shadow log (always append so power-loss doesn't lose history)
                if (_shadowLogPath != null)
                {
                    try
                    {
                        _shadowWriter = new StreamWriter(_shadowLogPath, append: true, Encoding.UTF8)
                            { AutoFlush = false };
                    }
                    catch { }
                }
            }
        }

        public void Log(string level, string message)
        {
            string ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string line = $"[{ts}] {level,-5} {UsbContentScanner.SanitizeForLog(message)}";

            lock (_lock)
            {
                try { _usbWriter?.WriteLine(line); } catch { }
                try { _shadowWriter?.WriteLine(line); } catch { }
            }
        }

        public void Flush()
        {
            lock (_lock)
            {
                try { _usbWriter?.Flush(); } catch { }
                try { _shadowWriter?.Flush(); } catch { }
            }
        }

        public void FSync()
        {
            Flush();
            // fsync the underlying file streams to ensure data reaches disk
            lock (_lock)
            {
                FlushStream(_usbWriter);
                FlushStream(_shadowWriter);
            }
        }

        private static void FlushStream(StreamWriter? writer)
        {
            if (writer == null) return;
            try
            {
                writer.Flush();
                // Access underlying stream for fsync
                var stream = writer.BaseStream;
                if (stream is FileStream fs)
                    fs.Flush(flushToDisk: true);
            }
            catch { }
        }

        public void Dispose()
        {
            Flush();
            lock (_lock)
            {
                _usbWriter?.Dispose();
                _shadowWriter?.Dispose();
                _usbWriter = null;
                _shadowWriter = null;
            }
        }
    }

    public interface ITransactionLogger
    {
        string TransactionId { get; }
        void Log(string level, string message);
        void Flush();
        void FSync();
    }

    /// <summary>
    /// Atomic persistent state journal for power-loss recovery.
    /// Written to /var/lib/console/installer/transaction.json.
    /// </summary>
    public class TransactionJournal
    {
        public const string JournalPath = "/var/lib/console/installer/transaction.json";

        public static void Write(JournalState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                string tmp = JournalPath + ".tmp";
                string json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(tmp, json);
                File.Move(tmp, JournalPath, overwrite: true);
            }
            catch { }
        }

        public static JournalState? Read()
        {
            try
            {
                if (!File.Exists(JournalPath)) return null;
                string json = File.ReadAllText(JournalPath);
                return System.Text.Json.JsonSerializer.Deserialize<JournalState>(json);
            }
            catch { return null; }
        }

        public static void Clear()
        {
            try { File.Delete(JournalPath); } catch { }
        }
    }

    public class JournalState
    {
        public string TransactionId { get; set; } = string.Empty;
        public string DeviceNode { get; set; } = string.Empty;
        public string MountPath { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public string FinalState { get; set; } = string.Empty; // "incomplete", "success", "failed"
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int GamesInstalled { get; set; }
        public int PatchesInstalled { get; set; }
        public bool FirmwareInstalled { get; set; }
        public bool KeysInstalled { get; set; }
    }
}
