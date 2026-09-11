using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer
{
    /// <summary>
    /// Publishes USB installer status/events over /run/console/installer.sock.
    /// Prompt 7 frontend subscribes here (does not need to tail install.log).
    /// Event format is JSON lines, same protocol style as Ryujinx.Appliance IPC.
    /// </summary>
    public class InstallerStatusPublisher : IDisposable
    {
        public const string InstallerSocketPath = "/run/console/installer.sock";

        private readonly ConcurrentDictionary<Guid, Socket> _subscribers = new();
        private Socket? _listener;
        private CancellationTokenSource? _cts;
        private InstallerStatusDto _currentStatus = new();
        private readonly object _statusLock = new();

        public void Start()
        {
            if (File.Exists(InstallerSocketPath))
                File.Delete(InstallerSocketPath);

            _cts = new CancellationTokenSource();
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(InstallerSocketPath));
            _listener.Listen(8);

            try
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(InstallerSocketPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite |
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
            }
            catch { }

            _ = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        private async Task AcceptLoopAsync()
        {
            while (_cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    Socket client = await _listener!.AcceptAsync(_cts.Token);
                    Guid id = Guid.NewGuid();
                    _subscribers[id] = client;
                    // Send current state immediately on connect
                    _ = Task.Run(async () =>
                    {
                        InstallerStatusDto status;
                        lock (_statusLock) { status = _currentStatus; }
                        await SendToSocketAsync(client, CreateEvent("installer.status", status));
                        // Keep client socket open for events; it will be cleaned up on error
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { /* accept errors are non-fatal */ }
            }
        }

        public void UpdateStatus(InstallerStatusDto status)
        {
            lock (_statusLock) { _currentStatus = status; }
            _ = BroadcastAsync(CreateEvent("installer.status", status));
        }

        public void BroadcastEvent(string eventName, InstallerStatusDto status)
        {
            lock (_statusLock) { _currentStatus = status; }
            _ = BroadcastAsync(CreateEvent(eventName, status));
        }

        private async Task BroadcastAsync(string line)
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            foreach (var (id, socket) in _subscribers)
            {
                try
                {
                    await socket.SendAsync(data, SocketFlags.None);
                }
                catch
                {
                    _subscribers.TryRemove(id, out _);
                    socket.Dispose();
                }
            }
        }

        private static async Task SendToSocketAsync(Socket socket, string line)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                await socket.SendAsync(data, SocketFlags.None);
            }
            catch { }
        }

        private static string CreateEvent(string eventName, InstallerStatusDto status)
        {
            var ev = new InstallerEventDto
            {
                TransactionId = status.TransactionId,
                Event = eventName,
                Status = status,
                TimestampUtc = DateTime.UtcNow
            };
            return JsonSerializer.Serialize(ev);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            foreach (var (_, socket) in _subscribers)
            {
                try { socket.Dispose(); } catch { }
            }
            _subscribers.Clear();
            _listener?.Dispose();
            try { File.Delete(InstallerSocketPath); } catch { }
        }
    }
}
