using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;
using Ryujinx.Appliance.Ipc;
using Ryujinx.Appliance.Services;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu.LinuxKvm;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;

namespace Ryujinx.Appliance
{
    public class ApplianceHost : IDisposable
    {
        private readonly string _socketPath;
        private readonly string _dataDir;
        private readonly string _runnerPath;
        private readonly DateTime _startTimeUtc = DateTime.UtcNow;

        private VirtualFileSystem _vfs;
        private ContentManager _contentManager;
        private LibHacHorizonManager _horizonManager;

        private KeysService _keysService;
        private FirmwareService _firmwareService;
        private GameLibraryService _libraryService;
        private InstallService _installService;
        private GameSessionManager _sessionManager;
        private SettingsService _settingsService;
        private InputService _inputService;
        private IpcServer _ipcServer;

        public ApplianceHost(string socketPath = "/run/console/ryujinx.sock", string dataDir = null, string runnerPath = null)
        {
            _socketPath = socketPath;
            _dataDir = dataDir;
            _runnerPath = runnerPath;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            Logger.Notice.Print(LogClass.Application, "Starting Ryujinx Console Appliance Backend...");

            // 1. Initialize persistent data directories
            AppDataManager.Initialize(_dataDir);

            // 2. Load Configuration
            ConfigurationState.Initialize();
            string configPath = Path.Combine(AppDataManager.BaseDirPath, "Config.json");
            if (File.Exists(configPath) && ConfigurationFileFormat.TryLoad(configPath, out var format))
            {
                ConfigurationState.Instance.Load(format, configPath);
            }
            else
            {
                ConfigurationState.Instance.LoadDefault();
            }

            // 3. Initialize HLE / VirtualFileSystem
            _vfs = VirtualFileSystem.CreateInstance();
            _horizonManager = new LibHacHorizonManager();
            _horizonManager.InitializeFsServer(_vfs);
            _horizonManager.InitializeArpServer();
            _horizonManager.InitializeBcatServer();
            _horizonManager.InitializeSystemClients();

            _contentManager = new ContentManager(_vfs);
            _contentManager.LoadEntries();

            // 4. Initialize Domain Services
            _keysService = new KeysService(BroadcastEventAsync);
            _firmwareService = new FirmwareService(_vfs, _contentManager, _keysService, BroadcastEventAsync);
            _libraryService = new GameLibraryService(_vfs, BroadcastEventAsync);
            _installService = new InstallService(_vfs, _keysService, _libraryService, BroadcastEventAsync);
            _sessionManager = new GameSessionManager(_runnerPath, _socketPath, _libraryService, BroadcastEventAsync);
            _settingsService = new SettingsService();
            _inputService = new InputService();

            // Initial library scan
            try
            {
                await _libraryService.RefreshLibraryAsync(ct);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Initial library scan warning: {ex.Message}");
            }

            // 5. Start IPC Server
            _ipcServer = new IpcServer(_socketPath, DispatchRequestAsync);
            _ipcServer.Start();

            // 6. Broadcast backend.ready event
            await BroadcastEventAsync("backend.ready", GetStatus());
            Logger.Notice.Print(LogClass.Application, "Ryujinx Console Appliance Backend is READY.");
        }

        private Task BroadcastEventAsync(string eventName, object data)
        {
            if (_ipcServer != null)
            {
                return _ipcServer.BroadcastEventAsync(eventName, data);
            }
            return Task.CompletedTask;
        }

        private async Task<IpcResponse> DispatchRequestAsync(IpcRequest request, ClientConnection conn)
        {
            string method = request.Method?.Trim() ?? string.Empty;

            try
            {
                switch (method)
                {
                    case "backend.getProtocolVersion":
                        return Success(request.Id, new { protocolVersion = 1 });

                    case "backend.getStatus":
                        return Success(request.Id, GetStatus());

                    case "backend.ready":
                        return Success(request.Id, new { ready = true, status = GetStatus() });

                    case "keys.status":
                        return Success(request.Id, _keysService.GetStatus());

                    case "keys.install":
                    {
                        string path = GetParamString(request.Params, "path");
                        InstallResultDto res = await _keysService.InstallKeysAsync(path);
                        return res.Status == "FAILED" ? Failure(request.Id, res.ErrorCode, res.ErrorMessage) : Success(request.Id, res);
                    }

                    case "firmware.status":
                        return Success(request.Id, _firmwareService.GetStatus());

                    case "firmware.install":
                    {
                        string path = GetParamString(request.Params, "path");
                        InstallResultDto res = await _firmwareService.InstallFirmwareAsync(path);
                        return res.Status == "FAILED" ? Failure(request.Id, res.ErrorCode, res.ErrorMessage) : Success(request.Id, res);
                    }

                    case "library.list":
                        return Success(request.Id, _libraryService.ListGames());

                    case "library.refresh":
                    {
                        int count = await _libraryService.RefreshLibraryAsync();
                        return Success(request.Id, new { count, titles = _libraryService.ListGames() });
                    }

                    case "library.getGame":
                    {
                        string titleId = GetParamString(request.Params, "titleId");
                        GameTitleDto game = _libraryService.GetGame(titleId);
                        if (game == null)
                            return Failure(request.Id, ErrorCodes.TitleNotFound, $"Title '{titleId}' not found.");
                        return Success(request.Id, game);
                    }

                    case "install.title":
                    {
                        string path = GetParamString(request.Params, "path");
                        bool defer = GetParamBool(request.Params, "deferRefresh");
                        InstallResultDto res = await _installService.InstallTitleAsync(path, defer);
                        return res.Status == "FAILED" ? Failure(request.Id, res.ErrorCode, res.ErrorMessage) : Success(request.Id, res);
                    }

                    case "install.update":
                    {
                        string path = GetParamString(request.Params, "path");
                        bool defer = GetParamBool(request.Params, "deferRefresh");
                        InstallResultDto res = await _installService.InstallUpdateAsync(path, defer);
                        return res.Status == "FAILED" ? Failure(request.Id, res.ErrorCode, res.ErrorMessage) : Success(request.Id, res);
                    }

                    case "game.launch":
                    {
                        string titleId = GetParamString(request.Params, "titleId");
                        bool fullscreen = GetParamBool(request.Params, "fullscreen", defaultValue: true);
                        GameSessionDto session = await _sessionManager.LaunchGameAsync(titleId, fullscreen);
                        return Success(request.Id, session);
                    }

                    case "game.stop":
                    {
                        int timeout = GetParamInt(request.Params, "timeoutSeconds", defaultValue: 5);
                        GameSessionDto session = await _sessionManager.StopGameAsync(timeout);
                        return Success(request.Id, session);
                    }

                    case "game.status":
                        return Success(request.Id, _sessionManager.GetCurrentSession());

                    case "game.getLastCrash":
                    {
                        CrashReportDto crash = _sessionManager.GetLastCrash();
                        return Success(request.Id, crash);
                    }

                    case "storage.status":
                        return Success(request.Id, _installService.GetStorageStatus());

                    case "input.listDevices":
                        return Success(request.Id, _inputService.ListDevices());

                    case "settings.get":
                        return Success(request.Id, _settingsService.GetSettings());

                    case "settings.set":
                    {
                        SettingsDto newSettings = request.Params.HasValue
                            ? JsonSerializer.Deserialize<SettingsDto>(request.Params.Value.GetRawText())
                            : null;
                        return Success(request.Id, _settingsService.UpdateSettings(newSettings));
                    }

                    default:
                        return Failure(request.Id, ErrorCodes.InvalidMethod, $"Unknown method '{method}'");
                }
            }
            catch (BackendException bex)
            {
                return Failure(request.Id, bex.Code, bex.Message);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Exception processing '{method}': {ex}");
                return Failure(request.Id, ErrorCodes.InternalError, ex.Message);
            }
        }

        public BackendStatusDto GetStatus()
        {
            var keyStatus = _keysService?.GetStatus();
            var fwStatus = _firmwareService?.GetStatus();
            var session = _sessionManager?.GetCurrentSession();

            bool kvm = false;
            try
            {
                kvm = KvmCapabilities.IsSupported;
            }
            catch { }

            return new BackendStatusDto
            {
                ProtocolVersion = 1,
                Status = "READY",
                UptimeSeconds = (DateTime.UtcNow - _startTimeUtc).TotalSeconds,
                RyujinxVersion = Program.Version ?? "1.0.0-switchpi",
                Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                CpuBackendRequested = ConfigurationState.Instance.System.UseHypervisor.Value ? "Auto" : "Jit",
                CpuBackendActive = kvm && ConfigurationState.Instance.System.UseHypervisor.Value ? "LinuxKvm" : "Jit",
                KvmAvailable = kvm,
                KeysStatus = keyStatus?.Status ?? "missing",
                FirmwareStatus = fwStatus?.Status ?? "missing",
                FirmwareVersion = fwStatus?.Version ?? string.Empty,
                LibraryCount = _libraryService?.ListGames().Length ?? 0,
                CurrentGame = session != null && session.State == "Running" ? session.TitleName : "none",
                ActiveSessionId = session?.SessionId
            };
        }

        private static IpcResponse Success(string id, object result)
        {
            string json = JsonSerializer.Serialize(result);
            using JsonDocument doc = JsonDocument.Parse(json);
            return new IpcResponse
            {
                Version = 1,
                Id = id,
                Ok = true,
                Result = doc.RootElement.Clone()
            };
        }

        private static IpcResponse Failure(string id, string code, string message)
        {
            return new IpcResponse
            {
                Version = 1,
                Id = id,
                Ok = false,
                Error = new IpcError
                {
                    Code = code ?? ErrorCodes.InternalError,
                    Message = message ?? "An unexpected error occurred."
                }
            };
        }

        private static string GetParamString(JsonElement? elem, string propName)
        {
            if (elem.HasValue && elem.Value.TryGetProperty(propName, out JsonElement p))
            {
                return p.GetString();
            }
            return null;
        }

        private static bool GetParamBool(JsonElement? elem, string propName, bool defaultValue = false)
        {
            if (elem.HasValue && elem.Value.TryGetProperty(propName, out JsonElement p))
            {
                return p.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => defaultValue
                };
            }
            return defaultValue;
        }

        private static int GetParamInt(JsonElement? elem, string propName, int defaultValue = 0)
        {
            if (elem.HasValue && elem.Value.TryGetProperty(propName, out JsonElement p) && p.TryGetInt32(out int val))
            {
                return val;
            }
            return defaultValue;
        }

        public void Dispose()
        {
            _ipcServer?.Dispose();
            _sessionManager?.Dispose();
            _inputService?.Dispose();
            _vfs?.Dispose();
        }
    }
}
