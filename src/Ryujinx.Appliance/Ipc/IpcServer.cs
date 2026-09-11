using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;
using Ryujinx.Common.Logging;

namespace Ryujinx.Appliance.Ipc
{
    public class IpcServer : IDisposable
    {
        private readonly string _socketPath;
        private Socket _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
        private readonly Func<IpcRequest, ClientConnection, Task<IpcResponse>> _requestHandler;

        public IpcServer(string socketPath, Func<IpcRequest, ClientConnection, Task<IpcResponse>> requestHandler)
        {
            _socketPath = socketPath;
            _requestHandler = requestHandler;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();

            // Ensure parent directory exists
            string dir = Path.GetDirectoryName(_socketPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Clean up stale socket file if it exists
            if (File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch { }
            }

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(10);

            // Set socket file permissions (0660 / read-write for owner and group)
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(_socketPath, 
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | 
                        UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Failed to set socket file mode: {ex.Message}");
                }
            }

            Logger.Notice.Print(LogClass.Application, $"IPC Server listening on {_socketPath}");

            _ = Task.Run(AcceptLoopAsync, _cts.Token);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    Socket clientSocket = await _listener.AcceptAsync(_cts.Token);
                    ClientConnection conn = new(clientSocket, this);
                    _clients[conn.Id] = conn;

                    Logger.Info?.Print(LogClass.Application, $"Client connected: {conn.Id}");
                    _ = Task.Run(conn.RunAsync, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error?.Print(LogClass.Application, $"Error accepting IPC connection: {ex.Message}");
                }
            }
        }

        internal void RemoveClient(Guid id)
        {
            _clients.TryRemove(id, out _);
            Logger.Info?.Print(LogClass.Application, $"Client disconnected: {id}");
        }

        internal async Task<IpcResponse> HandleRequestAsync(IpcRequest request, ClientConnection conn)
        {
            if (_requestHandler != null)
            {
                return await _requestHandler(request, conn);
            }

            return new IpcResponse
            {
                Version = 1,
                Id = request.Id,
                Ok = false,
                Error = new IpcError { Code = ErrorCodes.InvalidMethod, Message = $"No handler configured" }
            };
        }

        public async Task BroadcastEventAsync(string eventName, object data)
        {
            JsonElement? dataElem = null;
            if (data != null)
            {
                string dataJson = JsonSerializer.Serialize(data);
                using JsonDocument doc = JsonDocument.Parse(dataJson);
                dataElem = doc.RootElement.Clone();
            }

            IpcEvent ev = new()
            {
                Version = 1,
                Event = eventName,
                Data = dataElem
            };

            string line = JsonSerializer.Serialize(ev);

            foreach (var client in _clients.Values)
            {
                try
                {
                    await client.WriteLineAsync(line);
                }
                catch { }
            }
        }

        public void Stop()
        {
            _cts?.Cancel();

            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }
            _clients.Clear();

            _listener?.Dispose();
            _listener = null;

            if (File.Exists(_socketPath))
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch { }
            }

            Logger.Notice.Print(LogClass.Application, "IPC Server stopped.");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }

    public class ClientConnection : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        private readonly Socket _socket;
        private readonly IpcServer _server;
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        public ClientConnection(Socket socket, IpcServer server)
        {
            _socket = socket;
            _server = server;
            _stream = new NetworkStream(socket, ownsSocket: false);
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        }

        public async Task RunAsync()
        {
            try
            {
                while (!_disposed)
                {
                    string line = await _reader.ReadLineAsync();
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    await ProcessRequestLineAsync(line);
                }
            }
            catch (Exception)
            {
                // Disconnected
            }
            finally
            {
                Dispose();
                _server.RemoveClient(Id);
            }
        }

        private async Task ProcessRequestLineAsync(string line)
        {
            IpcRequest request = null;
            try
            {
                request = JsonSerializer.Deserialize<IpcRequest>(line);
            }
            catch (Exception ex)
            {
                IpcResponse errorResp = new()
                {
                    Version = 1,
                    Id = "unknown",
                    Ok = false,
                    Error = new IpcError
                    {
                        Code = ErrorCodes.InvalidParams,
                        Message = $"Malformed JSON request: {ex.Message}"
                    }
                };
                await WriteLineAsync(JsonSerializer.Serialize(errorResp));
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.Method))
            {
                IpcResponse errorResp = new()
                {
                    Version = 1,
                    Id = request?.Id ?? "unknown",
                    Ok = false,
                    Error = new IpcError
                    {
                        Code = ErrorCodes.InvalidMethod,
                        Message = "Missing method name"
                    }
                };
                await WriteLineAsync(JsonSerializer.Serialize(errorResp));
                return;
            }

            IpcResponse response = await _server.HandleRequestAsync(request, this);
            await WriteLineAsync(JsonSerializer.Serialize(response));
        }

        public async Task WriteLineAsync(string text)
        {
            if (_disposed)
                return;

            await _writeLock.WaitAsync();
            try
            {
                if (!_disposed && _writer != null)
                {
                    await _writer.WriteLineAsync(text);
                }
            }
            catch
            {
                Dispose();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _reader?.Dispose();
            _writer?.Dispose();
            _stream?.Dispose();
            _socket?.Dispose();
            _writeLock?.Dispose();
        }
    }
}
