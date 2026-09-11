using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;

namespace Ryujinx.Appliance.Services
{
    public class GameSessionManager : IDisposable
    {
        private readonly string _runnerExecutablePath;
        private readonly string _socketPath;
        private readonly Func<string, object, Task> _eventBroadcaster;
        private readonly GameLibraryService _libraryService;
        private readonly string _logDir;
        private readonly object _lock = new();

        private Process _runnerProcess;
        private GameSessionDto _currentSession;
        private CrashReportDto _lastCrash;

        public bool IsGameRunning
        {
            get
            {
                lock (_lock)
                {
                    return _runnerProcess != null && !_runnerProcess.HasExited;
                }
            }
        }

        public GameSessionManager(string runnerExecutablePath, string socketPath, GameLibraryService libraryService, Func<string, object, Task> eventBroadcaster, string logDir = null)
        {
            _runnerExecutablePath = runnerExecutablePath;
            _socketPath = socketPath;
            _libraryService = libraryService;
            _eventBroadcaster = eventBroadcaster;
            _logDir = logDir ?? "/var/log/console";

            if (!Directory.Exists(_logDir))
            {
                try { Directory.CreateDirectory(_logDir); } catch { }
            }
        }

        public GameSessionDto GetCurrentSession()
        {
            lock (_lock)
            {
                if (_currentSession == null)
                {
                    return new GameSessionDto
                    {
                        State = "Stopped"
                    };
                }

                if (_runnerProcess != null && _runnerProcess.HasExited)
                {
                    if (_currentSession.State == "Running" || _currentSession.State == "Starting")
                    {
                        _currentSession.State = _runnerProcess.ExitCode == 0 ? "Stopped" : "Crashed";
                        _currentSession.ExitCode = _runnerProcess.ExitCode;
                        _currentSession.EndTimeUtc = DateTime.UtcNow;
                    }
                }

                return _currentSession;
            }
        }

        public CrashReportDto GetLastCrash()
        {
            lock (_lock)
            {
                return _lastCrash;
            }
        }

        public async Task<GameSessionDto> LaunchGameAsync(string titleId, bool fullscreen = true)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                throw new BackendException(new IpcError
                {
                    Code = ErrorCodes.InvalidParams,
                    Message = "Title ID cannot be empty."
                });
            }

            lock (_lock)
            {
                if (IsGameRunning)
                {
                    throw new BackendException(new IpcError
                    {
                        Code = ErrorCodes.GameAlreadyRunning,
                        Message = $"A game session is already active (Session: {_currentSession?.SessionId}, Title: {_currentSession?.TitleId})."
                    });
                }
            }

            GameTitleDto game = _libraryService.GetGame(titleId);
            if (game == null)
            {
                throw new BackendException(new IpcError
                {
                    Code = ErrorCodes.TitleNotFound,
                    Message = $"Title '{titleId}' was not found in the game library."
                });
            }

            string sessionId = Guid.NewGuid().ToString("N");
            string logPath = Path.Combine(_logDir, $"game-session-{sessionId}.log");

            GameSessionDto session = new()
            {
                SessionId = sessionId,
                TitleId = game.TitleId,
                TitleName = game.Name,
                State = "Starting",
                StartTimeUtc = DateTime.UtcNow,
                LogPath = logPath
            };

            lock (_lock)
            {
                _currentSession = session;
            }

            await _eventBroadcaster("game.starting", session);

            try
            {
                // Resolve runner executable path
                string runnerPath = _runnerExecutablePath;
                if (!File.Exists(runnerPath))
                {
                    // Look in same directory as current assembly
                    string localDir = AppDomain.CurrentDomain.BaseDirectory;
                    string candidate = Path.Combine(localDir, "Ryujinx.GameRunner");
                    if (!File.Exists(candidate)) candidate = Path.Combine(localDir, "ryujinx-game-runner");
                    if (!File.Exists(candidate)) candidate = Path.Combine(localDir, "Ryujinx.GameRunner.exe");

                    if (File.Exists(candidate))
                    {
                        runnerPath = candidate;
                    }
                    else
                    {
                        throw new FileNotFoundException($"Game runner executable not found at '{runnerPath}' or '{candidate}'.");
                    }
                }

                ProcessStartInfo psi = new()
                {
                    FileName = runnerPath,
                    Arguments = $"--title-id \"{game.TitleId}\" --path \"{game.FilePath}\" --session-id \"{sessionId}\" --backend-sock \"{_socketPath}\" --fullscreen {fullscreen}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Add data directory environment variable
                psi.Environment["RYUJINX_BASE_DIR"] = AppDataManager.BaseDirPath;

                Process proc = new() { StartInfo = psi, EnableRaisingEvents = true };

                FileStream logStream = new(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                StreamWriter logWriter = new(logStream) { AutoFlush = true };

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        try { logWriter.WriteLine($"[OUT] {e.Data}"); } catch { }
                    }
                };

                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        try { logWriter.WriteLine($"[ERR] {e.Data}"); } catch { }
                    }
                };

                proc.Exited += async (_, _) =>
                {
                    try { logWriter.Dispose(); } catch { }
                    try { logStream.Dispose(); } catch { }

                    await HandleRunnerExitedAsync(proc, sessionId, game.TitleId, game.Name, logPath);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                lock (_lock)
                {
                    _runnerProcess = proc;
                    session.State = "Running";
                }

                Logger.Notice.Print(LogClass.Application, $"Game runner started (PID: {proc.Id}, Session: {sessionId}, Title: {game.Name})");
                await _eventBroadcaster("game.started", session);

                return session;
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    session.State = "Crashed";
                    session.EndTimeUtc = DateTime.UtcNow;
                    _lastCrash = new CrashReportDto
                    {
                        SessionId = sessionId,
                        TitleId = game.TitleId,
                        TitleName = game.Name,
                        TimestampUtc = DateTime.UtcNow,
                        ErrorCategory = "StartupError",
                        Summary = ex.Message,
                        LogPath = logPath
                    };
                }

                await _eventBroadcaster("game.crashed", _lastCrash);
                throw new BackendException(new IpcError
                {
                    Code = ErrorCodes.InternalError,
                    Message = $"Failed to start game runner: {ex.Message}"
                });
            }
        }

        public async Task<GameSessionDto> StopGameAsync(int timeoutSeconds = 5)
        {
            Process proc;
            GameSessionDto session;

            lock (_lock)
            {
                if (!IsGameRunning)
                {
                    return _currentSession ?? new GameSessionDto { State = "Stopped" };
                }

                proc = _runnerProcess;
                session = _currentSession;
                session.State = "Stopping";
            }

            await _eventBroadcaster("game.stopping", session);

            try
            {
                // Graceful stop attempt
                if (!proc.HasExited)
                {
                    try
                    {
                        proc.CloseMainWindow();
                    }
                    catch { }

                    // Wait bounded interval
                    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Game runner did not exit within {timeoutSeconds}s. Escalating to SIGKILL.");
                        try
                        {
                            proc.Kill(entireProcessTree: true);
                            await proc.WaitForExitAsync();
                        }
                        catch { }
                    }
                }

                lock (_lock)
                {
                    session.State = "Stopped";
                    session.EndTimeUtc = DateTime.UtcNow;
                    session.ExitCode = proc.ExitCode;
                }

                await _eventBroadcaster("game.stopped", session);
                return session;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error stopping game runner: {ex.Message}");
                return session;
            }
        }

        private async Task HandleRunnerExitedAsync(Process proc, string sessionId, string titleId, string titleName, string logPath)
        {
            int exitCode = 0;
            try { exitCode = proc.ExitCode; } catch { }

            GameSessionDto session;
            bool wasStopping;

            lock (_lock)
            {
                session = _currentSession;
                wasStopping = session != null && session.State == "Stopping";

                if (session != null)
                {
                    session.EndTimeUtc = DateTime.UtcNow;
                    session.ExitCode = exitCode;
                }

                _runnerProcess = null;
            }

            if (exitCode != 0 && !wasStopping)
            {
                // Abnormal exit / crash
                CrashReportDto crash = new()
                {
                    SessionId = sessionId,
                    TitleId = titleId,
                    TitleName = titleName,
                    TimestampUtc = DateTime.UtcNow,
                    ErrorCategory = "ProcessExit",
                    Summary = $"Runner process exited abnormally with code {exitCode}",
                    LogPath = logPath
                };

                lock (_lock)
                {
                    if (session != null) session.State = "Crashed";
                    _lastCrash = crash;
                }

                Logger.Error?.Print(LogClass.Application, $"Game runner crashed with exit code {exitCode} (Session: {sessionId})");
                await _eventBroadcaster("game.crashed", crash);
            }
            else
            {
                lock (_lock)
                {
                    if (session != null) session.State = "Stopped";
                }

                Logger.Notice.Print(LogClass.Application, $"Game runner exited cleanly (Session: {sessionId})");
                await _eventBroadcaster("game.stopped", session);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_runnerProcess != null && !_runnerProcess.HasExited)
                {
                    try
                    {
                        _runnerProcess.Kill(entireProcessTree: true);
                    }
                    catch { }
                    _runnerProcess.Dispose();
                    _runnerProcess = null;
                }
            }
        }
    }
}
