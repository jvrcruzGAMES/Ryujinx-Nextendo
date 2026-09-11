using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Appliance.Client
{
    public class ConsoleBackendClient : IDisposable
    {
        private readonly string _socketPath;
        private Socket _socket;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pendingRequests = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public event Action<IpcEvent> OnEvent;
        public event Action<string> OnDisconnected;

        public bool IsConnected => _socket != null && _socket.Connected;

        public ConsoleBackendClient(string socketPath = "/run/console/ryujinx.sock")
        {
            _socketPath = socketPath;
        }

        public async Task ConnectAsync(int timeoutMs = 5000, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            UnixDomainSocketEndPoint endpoint = new(_socketPath);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);

            await _socket.ConnectAsync(endpoint, linked.Token);

            _stream = new NetworkStream(_socket, ownsSocket: false);
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };

            _ = Task.Run(ReceiveLoopAsync, _cts.Token);
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (_cts != null && !_cts.IsCancellationRequested && _reader != null)
                {
                    string line = await _reader.ReadLineAsync(_cts.Token);
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    ProcessMessage(line);
                }
            }
            catch (Exception)
            {
                // Disconnected or cancelled
            }
            finally
            {
                OnDisconnected?.Invoke("Connection closed by server.");
                Cleanup();
            }
        }

        private void ProcessMessage(string line)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                // Check if it's an event (has "event" field)
                if (root.TryGetProperty("event", out _))
                {
                    IpcEvent ev = JsonSerializer.Deserialize<IpcEvent>(line);
                    if (ev != null)
                    {
                        OnEvent?.Invoke(ev);
                    }
                    return;
                }

                // Check if it's a response (has "id" and "ok")
                if (root.TryGetProperty("id", out JsonElement idElem) && root.TryGetProperty("ok", out _))
                {
                    string id = idElem.GetString();
                    if (!string.IsNullOrEmpty(id) && _pendingRequests.TryRemove(id, out var tcs))
                    {
                        IpcResponse resp = JsonSerializer.Deserialize<IpcResponse>(line);
                        tcs.TrySetResult(resp);
                    }
                }
            }
            catch (Exception)
            {
                // Invalid line format, ignore
            }
        }

        public async Task<IpcResponse> SendRequestAsync(string method, object parameters = null, int timeoutMs = 15000, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Client is not connected to backend.");

            string requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = tcs;

            JsonElement? paramsElem = null;
            if (parameters != null)
            {
                string paramsJson = JsonSerializer.Serialize(parameters);
                using JsonDocument paramDoc = JsonDocument.Parse(paramsJson);
                paramsElem = paramDoc.RootElement.Clone();
            }

            IpcRequest req = new()
            {
                Version = 1,
                Id = requestId,
                Method = method,
                Params = paramsElem
            };

            string reqLine = JsonSerializer.Serialize(req);

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _writer.WriteLineAsync(reqLine.AsMemory(), cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using (linked.Token.Register(() => tcs.TrySetCanceled()))
            {
                return await tcs.Task;
            }
        }

        public async Task<BackendStatusDto> GetStatusAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("backend.getStatus", null, 5000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<BackendStatusDto>(resp.Result.Value.GetRawText());
        }

        public async Task<KeyStatusDto> GetKeyStatusAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("keys.status", null, 5000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<KeyStatusDto>(resp.Result.Value.GetRawText());
        }

        public async Task<InstallResultDto> InstallKeysAsync(string path, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("keys.install", new { path }, 10000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<InstallResultDto>(resp.Result.Value.GetRawText());
        }

        public async Task<FirmwareStatusDto> GetFirmwareStatusAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("firmware.status", null, 5000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<FirmwareStatusDto>(resp.Result.Value.GetRawText());
        }

        public async Task<InstallResultDto> InstallFirmwareAsync(string path, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("firmware.install", new { path }, 60000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<InstallResultDto>(resp.Result.Value.GetRawText());
        }

        public async Task<GameTitleDto[]> ListGamesAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("library.list", null, 10000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<GameTitleDto[]>(resp.Result.Value.GetRawText());
        }

        public async Task<InstallResultDto> InstallTitleAsync(string path, bool deferRefresh = false, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("install.title", new { path, deferRefresh }, 120000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<InstallResultDto>(resp.Result.Value.GetRawText());
        }

        public async Task<InstallResultDto> InstallUpdateAsync(string path, bool deferRefresh = false, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("install.update", new { path, deferRefresh }, 120000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<InstallResultDto>(resp.Result.Value.GetRawText());
        }

        public async Task<GameSessionDto> LaunchGameAsync(string titleId, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("game.launch", new { titleId }, 15000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<GameSessionDto>(resp.Result.Value.GetRawText());
        }

        public async Task<GameSessionDto> StopGameAsync(int timeoutSeconds = 5, CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("game.stop", new { timeoutSeconds }, 15000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<GameSessionDto>(resp.Result.Value.GetRawText());
        }

        public async Task<GameSessionDto> GetGameStatusAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("game.status", null, 5000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<GameSessionDto>(resp.Result.Value.GetRawText());
        }

        public async Task<StorageStatusDto> GetStorageStatusAsync(CancellationToken ct = default)
        {
            IpcResponse resp = await SendRequestAsync("storage.status", null, 5000, ct);
            if (!resp.Ok)
                throw new BackendException(resp.Error);

            return JsonSerializer.Deserialize<StorageStatusDto>(resp.Result.Value.GetRawText());
        }

        private void Cleanup()
        {
            _cts?.Cancel();
            _reader?.Dispose();
            _writer?.Dispose();
            _stream?.Dispose();
            _socket?.Dispose();

            _reader = null;
            _writer = null;
            _stream = null;
            _socket = null;

            foreach (var kvp in _pendingRequests)
            {
                kvp.Value.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }

        public void Dispose()
        {
            Cleanup();
            _cts?.Dispose();
            _writeLock?.Dispose();
        }
    }

    public class BackendException : Exception
    {
        public string Code { get; }
        public IpcError Error { get; }

        public BackendException(IpcError error) : base($"[{error?.Code}] {error?.Message}")
        {
            Error = error;
            Code = error?.Code ?? ErrorCodes.InternalError;
        }
    }
}
