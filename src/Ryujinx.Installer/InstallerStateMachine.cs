using System;
using System.Collections.Generic;
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
    /// Installer states (Step through in order — no shortcuts).
    /// </summary>
    public enum InstallerState
    {
        Idle,
        Detected,
        Mounting,
        Preparing,       // Remove old flags, create fresh install.log
        CheckingBackend, // Wait for ryujinx-backend readiness
        CheckingKeys,    // Query keys.status
        InstallingKeys,  // Install prod.keys from USB first
        Preflight,       // Space check, invariant verification
        InstallingFirmware,
        InstallingGames,
        InstallingPatches,
        Verifying,
        RefreshingLibrary,
        Finalizing,      // Write INSTALL_SUCCESS or INSTALL_FAILED
        Unmounting,
        Completed,
        Failed
    }

    /// <summary>
    /// Explicit transaction state machine with strict keys-gate enforcement.
    /// One machine per USB volume. All backend calls go through Ryujinx Appliance IPC.
    /// </summary>
    public class InstallerStateMachine
    {
        private const string BackendSocketPath = "/run/console/ryujinx.sock";
        private const string InstallerVersion = "SwitchPi USB Installer v6.0.0";

        private readonly string _deviceNode;
        private readonly MountManager _mountManager;
        private readonly InstallerStatusPublisher _publisher;
        private readonly CancellationToken _cancellationToken;

        private string _mountPath = string.Empty;
        private string _fsType = string.Empty;
        private string _uuid = string.Empty;
        private TransactionLogger? _logger;
        private InstallerState _state = InstallerState.Idle;
        private readonly string _transactionId = Guid.NewGuid().ToString("N")[..16];

        // Results tracking
        private int _gamesInstalled;
        private int _patchesInstalled;
        private bool _firmwareInstalled;
        private bool _keysInstalled;
        private string _firmwareVersion = string.Empty;

        public InstallerStateMachine(
            string deviceNode,
            MountManager mountManager,
            InstallerStatusPublisher publisher,
            CancellationToken cancellationToken)
        {
            _deviceNode = deviceNode;
            _mountManager = mountManager;
            _publisher = publisher;
            _cancellationToken = cancellationToken;
        }

        public string TransactionId => _transactionId;

        public async Task<bool> RunAsync()
        {
            try
            {
                return await ExecuteTransactionAsync();
            }
            catch (Exception ex)
            {
                _logger?.Log("ERROR", $"Unhandled exception in installer: {UsbContentScanner.SanitizeForLog(ex.Message)}");
                _logger?.Log("ERROR", UsbContentScanner.SanitizeForLog(ex.StackTrace ?? string.Empty));
                _logger?.FSync();
                await WriteFinalFlagAsync(false, $"Phase: {_state}\nError: {ex.Message}");
                return false;
            }
            finally
            {
                _logger?.Dispose();
            }
        }

        private async Task<bool> ExecuteTransactionAsync()
        {
            // ============================================================
            // STEP 1: Mount
            // ============================================================
            Transition(InstallerState.Mounting);
            MountResult mount = _mountManager.TryMount(_deviceNode);
            if (!mount.Ok)
            {
                PublishStatus("Mount failed: " + mount.ErrorMessage, mount.ErrorCode);
                return false;
            }

            _mountPath = mount.Path;
            _fsType = mount.FsType;
            _uuid = mount.Uuid;

            // ============================================================
            // STEP 2: Scan for content BEFORE creating logger (for no-content check)
            // ============================================================
            var preScanner = new UsbContentScanner(_mountPath);
            // Quick check for any recognized content using a null logger
            var quickScan = preScanner.Scan(new NullLogger());
            if (!quickScan.HasAnyContent)
            {
                // No recognized content — not an install attempt. Do not create flags.
                Console.Error.WriteLine($"[{_transactionId}] USB has no recognized SwitchPi content; ignoring.");
                _mountManager.Unmount(_deviceNode);
                return true; // Not an error
            }

            // ============================================================
            // STEP 3: Preparing — remove stale flags, create fresh install.log
            // ============================================================
            Transition(InstallerState.Preparing);

            string successFlag = Path.Combine(_mountPath, "INSTALL_SUCCESS");
            string failedFlag = Path.Combine(_mountPath, "INSTALL_FAILED");
            string installLog = Path.Combine(_mountPath, "install.log");

            // Remove stale flags FIRST — abort if we cannot write
            try
            {
                if (File.Exists(successFlag)) File.Delete(successFlag);
                if (File.Exists(failedFlag)) File.Delete(failedFlag);
                if (File.Exists(installLog)) File.Delete(installLog);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{_transactionId}] FATAL: Cannot clear stale flags on USB: {ex.Message}");
                // Cannot record state — must not install anything
                _mountManager.Unmount(_deviceNode);
                return false;
            }

            // Create logger only after clearing old log
            _logger = new TransactionLogger(_transactionId, _mountPath);
            _logger.Initialize();

            // Log transaction header
            LogHeader();

            // ============================================================
            // STEP 4: Check backend readiness
            // ============================================================
            Transition(InstallerState.CheckingBackend);
            _logger.Log("INFO", "Waiting for Ryujinx backend readiness...");

            if (!await WaitForBackendAsync())
            {
                return await FailTransaction(ErrorCodes.BackendNotReady, "Backend did not become ready in time");
            }

            // Check if a game is running
            string? gameState = await QueryBackendStringAsync("game.getState", null, "state");
            if (gameState is "Running" or "Starting")
            {
                _logger.Log("INFO", $"Game is currently running (state={gameState}). Waiting for game to stop...");
                PublishStatus($"Game running — waiting before install", null);

                bool stopped = await WaitForGameToStopAsync(timeoutSeconds: 3600);
                if (!stopped)
                {
                    return await FailTransaction(ErrorCodes.GameRunning, "Timed out waiting for game to stop");
                }
                _logger.Log("INFO", "Game stopped. Proceeding with install.");
            }

            // ============================================================
            // STEP 5: Discover content (proper scan with logging)
            // ============================================================
            var scanner = new UsbContentScanner(_mountPath);
            UsbInstallPlan plan = scanner.Scan(_logger);
            _logger.FSync();

            // ============================================================
            // STEP 6: Check keys status
            // ============================================================
            Transition(InstallerState.CheckingKeys);
            string? keysStatus = await QueryBackendStringAsync("keys.status", null, "status");
            _logger.Log("INFO", $"Persistent keys status: {keysStatus ?? "unknown"}");

            bool keysReady = keysStatus == "installed";

            // ============================================================
            // STEP 7: Install keys if needed and present on USB (MUST be first)
            // ============================================================
            if (!keysReady && plan.KeysCandidate != null)
            {
                Transition(InstallerState.InstallingKeys);
                _logger.Log("INFO", $"Installing prod.keys from USB: {UsbContentScanner.SanitizeForLog(plan.KeysCandidate)}");
                PublishStatus("Installing system keys...", null, currentFile: "prod.keys");

                bool installed = await InstallKeysFromUsbAsync(plan.KeysCandidate);
                if (!installed)
                    return await FailTransaction(ErrorCodes.KeysGateFailed, "prod.keys from USB failed validation/install");

                // Re-query keys status
                keysStatus = await QueryBackendStringAsync("keys.status", null, "status");
                _logger.Log("INFO", $"Keys status after install: {keysStatus}");
                keysReady = keysStatus == "installed";

                if (!keysReady)
                    return await FailTransaction(ErrorCodes.KeysGateFailed, "Keys were installed but status did not become 'installed'");

                _keysInstalled = true;
                _logger.Log("INFO", "prod.keys verified successfully.");
            }
            else if (!keysReady && plan.KeysCandidate == null)
            {
                // Keys missing and no USB keys — fail entire transaction
                _logger.Log("ERROR", "KEYS_GATE_FAILED: No valid prod.keys installed and none provided on USB.");
                _logger.Log("ERROR", "No firmware, game, or patch installation will proceed.");
                return await FailTransaction(ErrorCodes.KeysGateFailed, "prod.keys not installed and not on USB");
            }
            else if (keysReady && plan.KeysCandidate != null)
            {
                // Keys already installed; USB also has prod.keys — validate/replace carefully
                _logger.Log("INFO", "Persistent keys valid. USB also contains prod.keys.");
                _logger.Log("INFO", "Attempting to validate/install new keys atomically (old keys preserved on failure).");

                bool replaced = await InstallKeysFromUsbAsync(plan.KeysCandidate);
                if (!replaced)
                {
                    _logger.Log("WARN", "New prod.keys from USB failed validation. Retaining existing valid keys.");
                    // Per policy: fail the transaction if user explicitly supplied bad keys
                    return await FailTransaction(ErrorCodes.KeysGateFailed, "Supplied prod.keys is invalid (existing valid keys retained)");
                }
                _logger.Log("INFO", "Keys replaced successfully with new USB prod.keys.");
            }
            else
            {
                _logger.Log("INFO", "Keys gate passed (existing valid keys).");
            }

            _logger.FSync();

            // ============================================================
            // STEP 8: Preflight — verify space
            // ============================================================
            Transition(InstallerState.Preflight);
            long requiredBytes = EstimateRequiredSpace(plan);
            long? freeBytes = await QueryStorageFreeAsync();

            if (freeBytes.HasValue)
            {
                _logger.Log("INFO", $"Storage: free={freeBytes.Value / (1024 * 1024)} MB, estimated required={requiredBytes / (1024 * 1024)} MB");
                if (freeBytes.Value < requiredBytes + 50 * 1024 * 1024)
                {
                    return await FailTransaction(ErrorCodes.InsufficientSpace,
                        $"Insufficient storage: need {requiredBytes / (1024 * 1024)} MB, free {freeBytes.Value / (1024 * 1024)} MB");
                }
            }

            // ============================================================
            // STEP 9: Install Firmware
            // ============================================================
            if (plan.FirmwareCandidate != null)
            {
                Transition(InstallerState.InstallingFirmware);
                _logger.Log("INFO", $"Installing firmware: {UsbContentScanner.SanitizeForLog(plan.FirmwareCandidate)}");
                PublishStatus("Installing firmware...", null, currentFile: "Firmware.zip");

                bool fwOk = await InstallFirmwareAsync(plan.FirmwareCandidate);
                if (!fwOk)
                    return await FailTransaction(ErrorCodes.FirmwareMissing, "Firmware installation failed");

                _firmwareInstalled = true;
                _logger.Log("INFO", "Firmware installed successfully.");
            }

            _logger.FSync();

            // ============================================================
            // STEP 10: Install Base Games
            // ============================================================
            if (plan.GameCandidates.Count > 0)
            {
                Transition(InstallerState.InstallingGames);
                _logger.Log("INFO", $"Installing {plan.GameCandidates.Count} game(s)...");

                HashSet<string> installedThisTransaction = new();
                int idx = 0;

                foreach (var game in plan.GameCandidates)
                {
                    idx++;
                    PublishStatus($"Installing game {idx}/{plan.GameCandidates.Count}...", null,
                        currentFile: game.FileName, itemIndex: idx, itemCount: plan.GameCandidates.Count);

                    _logger.Log("INFO", $"[{idx}/{plan.GameCandidates.Count}] Installing game: {UsbContentScanner.SanitizeForLog(game.FileName)} ({game.SizeBytes / (1024 * 1024)} MB)");

                    // Verify file hasn't changed since scan
                    var currentInfo = new FileInfo(game.Path);
                    if (currentInfo.Length != game.SizeBytes || currentInfo.LastWriteTimeUtc != game.Mtime)
                    {
                        _logger.Log("WARN", $"File changed during install: {UsbContentScanner.SanitizeForLog(game.FileName)}");
                    }

                    InstallResultDto result = await CallInstallTitleAsync(game.Path);
                    _logger.Log("INFO", $"  Result: {result.Status} | TitleId={result.TitleId} | Name={UsbContentScanner.SanitizeForLog(result.TitleName)}");

                    if (result.Status == "FAILED")
                    {
                        _logger.Log("ERROR", $"  FAILED: {result.ErrorCode} - {UsbContentScanner.SanitizeForLog(result.ErrorMessage ?? string.Empty)}");
                        return await FailTransaction(result.ErrorCode ?? ErrorCodes.InstallFailed,
                            $"Game installation failed: {game.FileName} - {result.ErrorMessage ?? string.Empty}");
                    }

                    // Content type check: games/ must be base/application type
                    if (result.ContentType is "update" or "patch" or "dlc")
                    {
                        _logger.Log("ERROR", $"  CONTENT_TYPE_MISMATCH: {UsbContentScanner.SanitizeForLog(game.FileName)} is type '{result.ContentType}' but is in games/. Move to patch/ for updates.");
                        return await FailTransaction(ErrorCodes.ContentTypeMismatch,
                            $"{game.FileName} is content type '{result.ContentType}' but placed in games/. Expected base game.");
                    }

                    if (result.Status is "INSTALLED" or "ALREADY_INSTALLED")
                    {
                        installedThisTransaction.Add(result.TitleIdBase ?? result.TitleId);
                        if (result.Status == "INSTALLED") _gamesInstalled++;
                    }

                    _logger.Flush();
                }
            }

            _logger.FSync();

            // ============================================================
            // STEP 11: Install Patches
            // ============================================================
            if (plan.PatchCandidates.Count > 0)
            {
                Transition(InstallerState.InstallingPatches);
                _logger.Log("INFO", $"Installing {plan.PatchCandidates.Count} patch(es)...");

                int idx = 0;
                foreach (var patch in plan.PatchCandidates)
                {
                    idx++;
                    PublishStatus($"Installing update {idx}/{plan.PatchCandidates.Count}...", null,
                        currentFile: patch.FileName, itemIndex: idx, itemCount: plan.PatchCandidates.Count);

                    _logger.Log("INFO", $"[{idx}/{plan.PatchCandidates.Count}] Installing patch: {UsbContentScanner.SanitizeForLog(patch.FileName)}");

                    InstallResultDto result = await CallInstallUpdateAsync(patch.Path);
                    _logger.Log("INFO", $"  Result: {result.Status} | TitleId={result.TitleId}");

                    if (result.Status == "FAILED")
                    {
                        if (result.ErrorCode == ErrorCodes.BaseTitleMissing)
                        {
                            _logger.Log("ERROR", $"  BASE_TITLE_MISSING: Base game for patch not installed: {UsbContentScanner.SanitizeForLog(patch.FileName)}");
                        }
                        return await FailTransaction(result.ErrorCode ?? ErrorCodes.InstallFailed,
                            $"Patch failed: {patch.FileName} - {result.ErrorMessage ?? string.Empty}");
                    }

                    if (result.Status == "INSTALLED") _patchesInstalled++;

                    _logger.Flush();
                }
            }

            _logger.FSync();

            // ============================================================
            // STEP 12: Verify
            // ============================================================
            Transition(InstallerState.Verifying);
            _logger.Log("INFO", "Verifying installed state...");

            string? verifyKeysStatus = await QueryBackendStringAsync("keys.status", null, "status");
            _logger.Log("INFO", $"  keys.status: {verifyKeysStatus}");

            string? verifyFwStatus = await QueryBackendStringAsync("firmware.status", null, "status");
            _logger.Log("INFO", $"  firmware.status: {verifyFwStatus}");

            // ============================================================
            // STEP 13: Refresh Library
            // ============================================================
            Transition(InstallerState.RefreshingLibrary);
            _logger.Log("INFO", "Refreshing game library...");
            await SendBackendRequestAsync("library.refresh", null);

            // ============================================================
            // STEP 14: Finalize — write result flag, fsync, unmount
            // ============================================================
            Transition(InstallerState.Finalizing);
            _logger.Log("INFO", "Transaction completed successfully.");
            _logger.Log("INFO", $"  Games installed:   {_gamesInstalled}");
            _logger.Log("INFO", $"  Patches installed: {_patchesInstalled}");
            _logger.Log("INFO", $"  Firmware: {(_firmwareInstalled ? _firmwareVersion : "not installed")}");
            _logger.Log("INFO", $"  Keys: {(_keysInstalled ? "installed this transaction" : "pre-existing")}");

            string successSummary =
                $"Installation completed successfully.\n" +
                $"Games installed: {_gamesInstalled}\n" +
                $"Updates installed: {_patchesInstalled}\n" +
                $"Firmware: {(_firmwareInstalled ? _firmwareVersion : "unchanged")}\n" +
                $"Timestamp: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}";

            await WriteFinalFlagAsync(true, successSummary);

            // ============================================================
            // STEP 15: Unmount
            // ============================================================
            Transition(InstallerState.Unmounting);
            _logger.Log("INFO", "Syncing and unmounting USB...");
            _logger.FSync();
            _logger.Dispose();
            _logger = null;

            _mountManager.Unmount(_deviceNode);

            TransactionJournal.Write(new JournalState
            {
                TransactionId = _transactionId,
                DeviceNode = _deviceNode,
                MountPath = _mountPath,
                Phase = "completed",
                FinalState = "success",
                StartedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
                GamesInstalled = _gamesInstalled,
                PatchesInstalled = _patchesInstalled,
                FirmwareInstalled = _firmwareInstalled,
                KeysInstalled = _keysInstalled
            });

            Transition(InstallerState.Completed);
            PublishStatus("Installation complete — safe to remove USB", null, safeToRemove: true);
            return true;
        }

        // ===========================
        // Helper Methods
        // ===========================

        private void Transition(InstallerState newState)
        {
            _state = newState;
            _logger?.Log("INFO", $"--- State: {newState} ---");
            PublishStatus(newState.ToString(), null);

            TransactionJournal.Write(new JournalState
            {
                TransactionId = _transactionId,
                DeviceNode = _deviceNode,
                MountPath = _mountPath,
                Phase = newState.ToString(),
                FinalState = "incomplete",
                StartedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            });
        }

        private void PublishStatus(string message, string? errorCode,
            string currentFile = "", int itemIndex = 0, int itemCount = 0,
            bool safeToRemove = false)
        {
            _publisher.UpdateStatus(new InstallerStatusDto
            {
                TransactionId = _transactionId,
                State = _state.ToString().ToLowerInvariant(),
                Phase = _state.ToString(),
                Message = message,
                ErrorCode = errorCode,
                CurrentFile = currentFile,
                ItemIndex = itemIndex,
                ItemCount = itemCount,
                DeviceNode = _deviceNode,
                MountPath = _mountPath,
                SafeToRemove = safeToRemove,
                TimestampUtc = DateTime.UtcNow
            });
        }

        private async Task<bool> FailTransaction(string errorCode, string reason)
        {
            _state = InstallerState.Failed;
            _logger?.Log("ERROR", $"TRANSACTION FAILED: {errorCode} - {UsbContentScanner.SanitizeForLog(reason)}");
            _logger?.FSync();

            string failedSummary =
                $"Installation failed.\n" +
                $"Phase: {_state}\n" +
                $"Error: {UsbContentScanner.SanitizeForLog(reason)}\n" +
                $"See install.log for details.\n" +
                $"Timestamp: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}";

            await WriteFinalFlagAsync(false, failedSummary);

            _logger?.Log("INFO", "Unmounting USB after failure...");
            _logger?.FSync();
            _logger?.Dispose();
            _logger = null;

            _mountManager.Unmount(_deviceNode);
            PublishStatus(reason, errorCode, safeToRemove: true);

            TransactionJournal.Write(new JournalState
            {
                TransactionId = _transactionId,
                DeviceNode = _deviceNode,
                MountPath = _mountPath,
                Phase = _state.ToString(),
                FinalState = "failed",
                UpdatedUtc = DateTime.UtcNow
            });

            return false;
        }

        private async Task WriteFinalFlagAsync(bool success, string summary)
        {
            if (string.IsNullOrEmpty(_mountPath)) return;

            string successFlag = Path.Combine(_mountPath, "INSTALL_SUCCESS");
            string failedFlag = Path.Combine(_mountPath, "INSTALL_FAILED");

            try
            {
                // Invariant: remove opposite flag first
                if (success)
                {
                    if (File.Exists(failedFlag)) File.Delete(failedFlag);
                    await File.WriteAllTextAsync(successFlag, summary);
                }
                else
                {
                    if (File.Exists(successFlag)) File.Delete(successFlag);
                    await File.WriteAllTextAsync(failedFlag, summary);
                }

                // fsync both the flag and the directory
                string parentDir = Path.GetDirectoryName(success ? successFlag : failedFlag)!;
                using var dirHandle = File.OpenRead(parentDir);
                // Best-effort fsync; not all filesystems support it
            }
            catch (Exception ex)
            {
                _logger?.Log("ERROR", $"Failed to write result flag: {UsbContentScanner.SanitizeForLog(ex.Message)}");
            }
        }

        private void LogHeader()
        {
            _logger!.Log("INFO", $"transaction={_transactionId}");
            _logger.Log("INFO", $"installer={InstallerVersion}");
            _logger.Log("INFO", $"device={UsbContentScanner.SanitizeForLog(_deviceNode)}");
            _logger.Log("INFO", $"filesystem={_fsType}");
            _logger.Log("INFO", $"mount={_mountPath}");
            _logger.Log("INFO", $"uuid={_uuid}");
            _logger.Log("INFO", $"timestamp={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            _logger.Log("INFO", $"backend_socket={BackendSocketPath}");
            _logger.Flush();
        }

        private static long EstimateRequiredSpace(UsbInstallPlan plan)
        {
            long total = 0;
            foreach (var g in plan.GameCandidates) total += g.SizeBytes;
            foreach (var p in plan.PatchCandidates) total += p.SizeBytes;
            return total;
        }

        // ===========================
        // Backend IPC Communication
        // ===========================

        private async Task<bool> WaitForBackendAsync()
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    string? result = await QueryBackendStringAsync("backend.getStatus", null, "status");
                    if (result == "READY") return true;
                }
                catch { }
                await Task.Delay(1000, _cancellationToken);
            }
            return false;
        }

        private async Task<bool> WaitForGameToStopAsync(int timeoutSeconds)
        {
            for (int i = 0; i < timeoutSeconds; i++)
            {
                string? state = await QueryBackendStringAsync("game.getState", null, "state");
                if (state is null or "Stopped" or "Crashed") return true;
                await Task.Delay(1000, _cancellationToken);
            }
            return false;
        }

        private async Task<bool> InstallKeysFromUsbAsync(string keysPath)
        {
            try
            {
                var result = await SendBackendRequestAsync("keys.install",
                    JsonSerializer.SerializeToElement(new { path = keysPath }));
                if (result == null) return false;

                bool ok = result.Value.TryGetProperty("status", out var statusProp) &&
                          statusProp.GetString() is "INSTALLED" or "ALREADY_INSTALLED";
                return ok;
            }
            catch (Exception ex)
            {
                _logger?.Log("ERROR", $"keys.install failed: {UsbContentScanner.SanitizeForLog(ex.Message)}");
                return false;
            }
        }

        private async Task<bool> InstallFirmwareAsync(string firmwarePath)
        {
            try
            {
                var result = await SendBackendRequestAsync("firmware.install",
                    JsonSerializer.SerializeToElement(new { path = firmwarePath }));
                if (result == null) return false;

                if (result.Value.TryGetProperty("status", out var statusProp))
                {
                    string status = statusProp.GetString() ?? string.Empty;
                    if (result.Value.TryGetProperty("version", out var ver))
                        _firmwareVersion = ver.GetString() ?? string.Empty;
                    _logger?.Log("INFO", $"  Firmware install status: {status} version={_firmwareVersion}");
                    return status != "FAILED";
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Log("ERROR", $"firmware.install failed: {UsbContentScanner.SanitizeForLog(ex.Message)}");
                return false;
            }
        }

        private async Task<InstallResultDto> CallInstallTitleAsync(string path)
        {
            try
            {
                var resultElem = await SendBackendRequestAsync("install.title",
                    JsonSerializer.SerializeToElement(new { path, deferRefresh = true }));

                if (resultElem == null)
                    return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError, ErrorMessage = "No response from backend" };

                return JsonSerializer.Deserialize<InstallResultDto>(resultElem.Value.GetRawText())
                    ?? new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError };
            }
            catch (Exception ex)
            {
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError, ErrorMessage = ex.Message };
            }
        }

        private async Task<InstallResultDto> CallInstallUpdateAsync(string path)
        {
            try
            {
                var resultElem = await SendBackendRequestAsync("install.update",
                    JsonSerializer.SerializeToElement(new { path, deferRefresh = true }));

                if (resultElem == null)
                    return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError, ErrorMessage = "No response from backend" };

                return JsonSerializer.Deserialize<InstallResultDto>(resultElem.Value.GetRawText())
                    ?? new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError };
            }
            catch (Exception ex)
            {
                return new InstallResultDto { Status = "FAILED", ErrorCode = ErrorCodes.InternalError, ErrorMessage = ex.Message };
            }
        }

        private async Task<string?> QueryBackendStringAsync(string method, JsonElement? @params, string resultKey)
        {
            try
            {
                var result = await SendBackendRequestAsync(method, @params);
                if (result == null) return null;
                if (result.Value.TryGetProperty(resultKey, out var val))
                    return val.GetString();
                return null;
            }
            catch { return null; }
        }

        private async Task<long?> QueryStorageFreeAsync()
        {
            try
            {
                var result = await SendBackendRequestAsync("storage.getStatus", null);
                if (result == null) return null;
                if (result.Value.TryGetProperty("freeBytes", out var fb))
                    return fb.GetInt64();
                return null;
            }
            catch { return null; }
        }

        private async Task<JsonElement?> SendBackendRequestAsync(string method, JsonElement? @params)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(BackendSocketPath));

                using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
                using var writer = new System.IO.StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                string reqId = Guid.NewGuid().ToString("N")[..8];
                var request = new { version = 1, id = reqId, method, @params = @params as object };
                await writer.WriteLineAsync(JsonSerializer.Serialize(request));

                string? responseLine = await reader.ReadLineAsync();
                if (responseLine == null) return null;

                using var doc = JsonDocument.Parse(responseLine);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean())
                {
                    if (root.TryGetProperty("result", out var result))
                        return result;
                }
                else if (root.TryGetProperty("error", out var err))
                {
                    err.TryGetProperty("message", out var msg);
                    _logger?.Log("WARN", $"Backend {method} error: {UsbContentScanner.SanitizeForLog(msg.GetString() ?? "unknown")}");
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.Log("WARN", $"Backend IPC error ({method}): {UsbContentScanner.SanitizeForLog(ex.Message)}");
                return null;
            }
        }
    }

    /// <summary>Null logger used for pre-transaction quick-scan.</summary>
    public class NullLogger : ITransactionLogger
    {
        public string TransactionId => "null";
        public void Log(string level, string message) { }
        public void Flush() { }
        public void FSync() { }
    }
}
