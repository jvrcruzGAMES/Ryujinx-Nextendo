using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;

namespace Ryujinx.Installer
{
    /// <summary>
    /// Daemon entrypoint and USB device monitor.
    /// Watches for USB block device additions (eudev netlink or polling fallback).
    /// Enforces global install lock (one active transaction system-wide).
    /// Debounces duplicate hotplug events by device node + UUID + transaction state.
    /// </summary>
    public class UsbInstallerDaemon : IDisposable
    {
        private readonly MountManager _mountManager = new();
        private readonly InstallerStatusPublisher _publisher = new();
        private readonly SemaphoreSlim _globalInstallLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> _recentlyProcessed = new();

        private CancellationTokenSource? _cts;
        private UdevWatcher? _udevWatcher;

        public void Start()
        {
            Console.Error.WriteLine("[console-usb-installer] Starting SwitchPi USB Installer Daemon");

            Directory.CreateDirectory("/run/console");
            Directory.CreateDirectory("/run/media/usb");
            Directory.CreateDirectory(TransactionLogger.PersistentLogRoot);
            Directory.CreateDirectory("/var/lib/console/installer");

            _cts = new CancellationTokenSource();
            _publisher.Start();

            // Check for interrupted transactions from previous boot
            CheckInterruptedTransactions();

            // Start udev event watcher
            _udevWatcher = new UdevWatcher(OnDeviceAdded, OnDeviceRemoved);
            _udevWatcher.Start();

            // Initial scan of already-mounted devices
            _ = Task.Run(ScanExistingDevicesAsync, _cts.Token);
        }

        private void CheckInterruptedTransactions()
        {
            JournalState? state = TransactionJournal.Read();
            if (state != null && state.FinalState == "incomplete")
            {
                string shadowLog = Path.Combine(TransactionLogger.PersistentLogRoot, $"{state.TransactionId}.log");
                Console.Error.WriteLine($"[console-usb-installer] WARNING: Interrupted transaction detected: {state.TransactionId}");
                Console.Error.WriteLine($"[console-usb-installer] Phase was: {state.Phase}");
                Console.Error.WriteLine($"[console-usb-installer] Shadow log: {shadowLog}");
                Console.Error.WriteLine("[console-usb-installer] Will start fresh when USB is reinserted.");

                // Mark as interrupted but do not resume mutation
                TransactionJournal.Write(new JournalState
                {
                    TransactionId = state.TransactionId,
                    DeviceNode = state.DeviceNode,
                    MountPath = state.MountPath,
                    Phase = state.Phase,
                    FinalState = "interrupted",
                    StartedUtc = state.StartedUtc,
                    UpdatedUtc = DateTime.UtcNow,
                    GamesInstalled = state.GamesInstalled,
                    PatchesInstalled = state.PatchesInstalled,
                    FirmwareInstalled = state.FirmwareInstalled,
                    KeysInstalled = state.KeysInstalled
                });
            }
        }

        private async Task ScanExistingDevicesAsync()
        {
            // Scan common USB block device patterns for already-present devices
            string[] patterns = ["/dev/sda1", "/dev/sdb1", "/dev/sdc1", "/dev/sdd1"];
            foreach (string dev in patterns)
            {
                if (File.Exists(dev) && MountManager.IsRemovable(dev))
                {
                    await HandleDeviceAsync(dev);
                }
            }

            // Also scan /proc/partitions
            await ScanProcPartitionsAsync();
        }

        private async Task ScanProcPartitionsAsync()
        {
            try
            {
                string[] lines = await File.ReadAllLinesAsync("/proc/partitions");
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("major")) continue;

                    string[] parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;

                    string devName = parts[3];
                    // Skip root devices (sda without number = whole disk), only process partitions
                    if (!System.Text.RegularExpressions.Regex.IsMatch(devName, @"^sd[a-z]\d+$")) continue;

                    string devPath = "/dev/" + devName;
                    if (File.Exists(devPath) && MountManager.IsRemovable(devPath))
                    {
                        await HandleDeviceAsync(devPath);
                    }
                }
            }
            catch { }
        }

        private void OnDeviceAdded(string deviceNode)
        {
            Console.Error.WriteLine($"[console-usb-installer] USB device added: {deviceNode}");
            _ = HandleDeviceAsync(deviceNode);
        }

        private void OnDeviceRemoved(string deviceNode)
        {
            Console.Error.WriteLine($"[console-usb-installer] USB device removed: {deviceNode}");
            _recentlyProcessed.TryRemove(deviceNode, out _);
        }

        private async Task HandleDeviceAsync(string deviceNode)
        {
            if (!MountManager.IsRemovable(deviceNode))
            {
                Console.Error.WriteLine($"[console-usb-installer] Skipping non-removable: {deviceNode}");
                return;
            }

            // Debounce: ignore if processed recently (within 5 seconds)
            if (_recentlyProcessed.TryGetValue(deviceNode, out DateTime last))
            {
                if ((DateTime.UtcNow - last).TotalSeconds < 5)
                {
                    Console.Error.WriteLine($"[console-usb-installer] Debounced duplicate event for: {deviceNode}");
                    return;
                }
            }
            _recentlyProcessed[deviceNode] = DateTime.UtcNow;

            // Global install lock: only one active USB install system-wide
            if (!await _globalInstallLock.WaitAsync(0))
            {
                Console.Error.WriteLine($"[console-usb-installer] Global install lock held; {deviceNode} will wait.");
                _publisher.UpdateStatus(new InstallerStatusDto
                {
                    State = "idle",
                    Message = "Another USB install is in progress. This device is queued.",
                    DeviceNode = deviceNode,
                    ErrorCode = ErrorCodes.DeviceBusy
                });

                // Wait for lock then try again
                await _globalInstallLock.WaitAsync(_cts?.Token ?? CancellationToken.None);
            }

            try
            {
                await RunTransactionAsync(deviceNode);
            }
            finally
            {
                _globalInstallLock.Release();
            }
        }

        private async Task RunTransactionAsync(string deviceNode)
        {
            var machine = new InstallerStateMachine(
                deviceNode,
                _mountManager,
                _publisher,
                _cts?.Token ?? CancellationToken.None);

            Console.Error.WriteLine($"[console-usb-installer] Starting transaction {machine.TransactionId} for {deviceNode}");
            bool result = await machine.RunAsync();
            Console.Error.WriteLine($"[console-usb-installer] Transaction {machine.TransactionId} completed: {(result ? "SUCCESS" : "FAILED")}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udevWatcher?.Stop();
        }

        public void Dispose()
        {
            Stop();
            _mountManager.Dispose();
            _publisher.Dispose();
            _cts?.Dispose();
        }
    }

    /// <summary>
    /// Watches for udev hotplug events using /proc/net/netlink or polling.
    /// Filters USB_STORAGE block devices.
    /// </summary>
    public class UdevWatcher
    {
        private readonly Action<string> _onAdded;
        private readonly Action<string> _onRemoved;
        private Thread? _thread;
        private bool _running;

        private readonly HashSet<string> _knownDevices = [];
        private readonly object _lock = new();

        public UdevWatcher(Action<string> onAdded, Action<string> onRemoved)
        {
            _onAdded = onAdded;
            _onRemoved = onRemoved;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(PollLoop) { IsBackground = true, Name = "UdevWatcher" };
            _thread.Start();
        }

        private void PollLoop()
        {
            // Initial snapshot
            UpdateDevices(initial: true);

            while (_running)
            {
                Thread.Sleep(1000);
                UpdateDevices(initial: false);
            }
        }

        private void UpdateDevices(bool initial)
        {
            // Scan /sys/block for removable block devices with partitions
            HashSet<string> current = [];

            try
            {
                foreach (string blockDir in Directory.GetDirectories("/sys/block"))
                {
                    string baseName = Path.GetFileName(blockDir);
                    string removablePath = Path.Combine(blockDir, "removable");

                    if (!File.Exists(removablePath)) continue;
                    if (File.ReadAllText(removablePath).Trim() != "1") continue;

                    // Enumerate partitions
                    foreach (string partDir in Directory.GetDirectories(blockDir, $"{baseName}[0-9]*"))
                    {
                        string partName = Path.GetFileName(partDir);
                        string devPath = $"/dev/{partName}";
                        if (File.Exists(devPath))
                            current.Add(devPath);
                    }

                    // Also check the device itself if no partitions
                    string devBase = $"/dev/{baseName}";
                    if (current.Count == 0 && File.Exists(devBase))
                        current.Add(devBase);
                }
            }
            catch { }

            lock (_lock)
            {
                // Detect additions
                foreach (string dev in current)
                {
                    if (!_knownDevices.Contains(dev))
                    {
                        _knownDevices.Add(dev);
                        if (!initial)
                            _onAdded(dev);
                    }
                }

                // Detect removals
                HashSet<string> removed = [];
                foreach (string dev in _knownDevices)
                {
                    if (!current.Contains(dev))
                        removed.Add(dev);
                }
                foreach (string dev in removed)
                {
                    _knownDevices.Remove(dev);
                    if (!initial)
                        _onRemoved(dev);
                }
            }
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }
}
