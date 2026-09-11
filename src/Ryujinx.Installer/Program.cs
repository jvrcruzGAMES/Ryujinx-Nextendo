using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Installer
{
    class Program
    {
        private static UsbInstallerDaemon? _daemon;

        static async Task<int> Main(string[] args)
        {
            Console.Error.WriteLine("=============================================================");
            Console.Error.WriteLine("  SwitchPi USB Installer Daemon (console-usb-installer)");
            Console.Error.WriteLine("  Stage 6: Atomic USB Sideloading Engine");
            Console.Error.WriteLine("=============================================================");

            bool daemon = Array.IndexOf(args, "--daemon") >= 0;
            bool once = Array.IndexOf(args, "--once") >= 0;

            _daemon = new UsbInstallerDaemon();

            // Clean shutdown on SIGTERM/SIGINT
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
            PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);

            _daemon.Start();

            if (daemon || !once)
            {
                // Run indefinitely until signal
                var tcs = new TaskCompletionSource();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
                await tcs.Task;
            }
            else
            {
                // --once: scan existing devices once and exit
                await Task.Delay(5000); // Allow hotplug detection to run
            }

            _daemon.Dispose();
            return 0;
        }

        private static void OnSignal(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            Console.Error.WriteLine("[console-usb-installer] Received signal, shutting down...");
            _daemon?.Stop();
            Environment.Exit(0);
        }
    }
}
