using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer.MockBackend
{
    /// <summary>
    /// Minimal Ryujinx Appliance IPC mock server for automated USB installer testing.
    /// Simulates the Prompt-5 backend without requiring real Nintendo keys, firmware, or NSP/XCI.
    /// Behaviour is configurable via MockConfig for scenario testing.
    /// </summary>
    public class MockBackendServer
    {
        private const string DefaultSocketPath = "/run/console/ryujinx.sock";
        public MockConfig Config { get; }

        private Socket? _listener;
        private bool _running;
        private string _keysStatus;
        private string _firmwareStatus;
        private string _firmwareVersion;
        private readonly List<string> _installedTitles = [];
        private readonly List<string> _calledMethods = [];
        private readonly object _lock = new();

        public MockBackendServer(MockConfig? config = null)
        {
            Config = config ?? new MockConfig();
            _keysStatus = Config.InitialKeysStatus;
            _firmwareStatus = Config.InitialFirmwareStatus;
            _firmwareVersion = Config.InitialFirmwareVersion;
        }

        public IReadOnlyList<string> CalledMethods
        {
            get { lock (_lock) { return [.. _calledMethods]; } }
        }

        public void Start(string socketPath = DefaultSocketPath)
        {
            if (File.Exists(socketPath)) File.Delete(socketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            _listener.Listen(8);
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
            Console.Error.WriteLine($"[MockBackend] Listening on {socketPath}");
        }

        private async Task AcceptLoopAsync()
        {
            while (_running)
            {
                try
                {
                    Socket client = await _listener!.AcceptAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch { break; }
            }
        }

        private async Task HandleClientAsync(Socket socket)
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch { break; }
                if (line == null) break;

                IpcRequest? request;
                try { request = JsonSerializer.Deserialize<IpcRequest>(line); }
                catch { continue; }
                if (request == null) continue;

                string response = HandleMethod(request);
                await writer.WriteLineAsync(response);
            }
        }

        private string HandleMethod(IpcRequest request)
        {
            string method = request.Method ?? string.Empty;
            lock (_lock) { _calledMethods.Add(method); }

            object result = method switch
            {
                "backend.getStatus" => new { status = "READY", keysStatus = _keysStatus, firmwareStatus = _firmwareStatus },
                "backend.ready" => new { ready = true },
                "keys.status" => new KeyStatusDto { Status = _keysStatus, HasProdKeys = _keysStatus == "installed", KeyCount = _keysStatus == "installed" ? 1000 : 0 },
                "keys.install" => HandleKeysInstall(request),
                "firmware.status" => new FirmwareStatusDto { Status = _firmwareStatus, Version = _firmwareVersion },
                "firmware.install" => HandleFirmwareInstall(request),
                "install.title" => HandleInstallTitle(request),
                "install.update" => HandleInstallUpdate(request),
                "library.refresh" => new { count = _installedTitles.Count, titles = _installedTitles },
                "library.list" => (object)_installedTitles,
                "game.getState" => new { state = "Stopped" },
                "storage.getStatus" => new StorageStatusDto { TotalBytes = 32L * 1024 * 1024 * 1024, FreeBytes = 28L * 1024 * 1024 * 1024, UsedBytes = 4L * 1024 * 1024 * 1024 },
                _ => new { error = "unknown_method", method }
            };

            bool isOk = method != "unknown_method";
            var response = new
            {
                version = 1,
                id = request.Id,
                ok = isOk,
                result = isOk ? result : (object?)null,
                error = isOk ? null : new { code = ErrorCodes.InvalidMethod, message = $"Unknown method: {method}" }
            };
            return JsonSerializer.Serialize(response);
        }

        private object HandleKeysInstall(IpcRequest request)
        {
            string? path = GetParam(request, "path");
            Console.Error.WriteLine($"[MockBackend] keys.install path={path}");

            if (Config.KeysInstallShouldFail)
            {
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.KeysInvalid, ErrorMessage = "Mock: keys validation failed" };
            }

            _keysStatus = "installed";
            return new InstallResultDto { Status = "INSTALLED", ContentType = "keys" };
        }

        private object HandleFirmwareInstall(IpcRequest request)
        {
            string? path = GetParam(request, "path");
            Console.Error.WriteLine($"[MockBackend] firmware.install path={path}");

            if (Config.FirmwareInstallShouldFail)
            {
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.FirmwareMissing, ErrorMessage = "Mock: firmware installation failed" };
            }

            _firmwareStatus = "installed";
            _firmwareVersion = "20.0.0-mock";
            return new InstallResultDto { Status = "INSTALLED", ContentType = "firmware", Version = _firmwareVersion };
        }

        private object HandleInstallTitle(IpcRequest request)
        {
            string? path = GetParam(request, "path");
            string fileName = Path.GetFileName(path ?? "unknown");
            Console.Error.WriteLine($"[MockBackend] install.title path={path}");

            if (_keysStatus != "installed")
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.KeysMissing, ErrorMessage = "Keys not installed" };

            if (Config.TitleInstallShouldFail)
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InstallFailed, ErrorMessage = "Mock: title install failed" };

            string titleId = $"01000000{Math.Abs(fileName.GetHashCode()):X8}";
            _installedTitles.Add(titleId);
            return new InstallResultDto { Status = "INSTALLED", ContentType = "base", TitleId = titleId, TitleName = $"MockGame-{fileName}" };
        }

        private object HandleInstallUpdate(IpcRequest request)
        {
            string? path = GetParam(request, "path");
            string fileName = Path.GetFileName(path ?? "unknown");
            Console.Error.WriteLine($"[MockBackend] install.update path={path}");

            if (_keysStatus != "installed")
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.KeysMissing, ErrorMessage = "Keys not installed" };

            if (Config.PatchInstallShouldFail)
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.BaseTitleMissing, ErrorMessage = "Mock: base title not found" };

            string titleId = $"01000000{Math.Abs(fileName.GetHashCode()):X8}800";
            return new InstallResultDto { Status = "INSTALLED", ContentType = "update", TitleId = titleId, Version = "2.0.0" };
        }

        private static string? GetParam(IpcRequest request, string key)
        {
            try
            {
                if (request.Params is JsonElement elem && elem.ValueKind == JsonValueKind.Object)
                    if (elem.TryGetProperty(key, out var val)) return val.GetString();
            }
            catch { }
            return null;
        }

        public void Stop()
        {
            _running = false;
            _listener?.Dispose();
        }
    }

    public class MockConfig
    {
        public string InitialKeysStatus { get; set; } = "missing";
        public string InitialFirmwareStatus { get; set; } = "missing";
        public string InitialFirmwareVersion { get; set; } = string.Empty;
        public bool KeysInstallShouldFail { get; set; } = false;
        public bool FirmwareInstallShouldFail { get; set; } = false;
        public bool TitleInstallShouldFail { get; set; } = false;
        public bool PatchInstallShouldFail { get; set; } = false;
    }

    // Minimal IpcRequest DTO for mock (mirrors Ryujinx.Appliance.Client)
    public class IpcRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonPropertyName("method")]
        public string? Method { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public object? Params { get; set; }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            string socketPath = args.Length > 0 ? args[0] : "/run/console/ryujinx.sock";
            var server = new MockBackendServer();
            server.Start(socketPath);
            Console.Error.WriteLine("[MockBackend] Running. Press Ctrl+C to stop.");
            await Task.Delay(Timeout.Infinite);
        }
    }
}
