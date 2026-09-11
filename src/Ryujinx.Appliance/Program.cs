using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Ryujinx.Common.Logging;

namespace Ryujinx.Appliance
{
    public class Program
    {
        public static string Version => "1.0.0-switchpi";

        public class CommandLineOptions
        {
            [Option('s', "socket", Required = false, Default = "/run/console/ryujinx.sock", HelpText = "Path to the Unix domain socket for IPC communication.")]
            public string SocketPath { get; set; }

            [Option('d', "data-dir", Required = false, HelpText = "Base persistent data directory for Ryujinx.")]
            public string DataDir { get; set; }

            [Option('r', "runner-path", Required = false, HelpText = "Path to ryujinx-game-runner executable.")]
            public string RunnerPath { get; set; }

            [Option('l', "log-dir", Required = false, Default = "/var/log/console", HelpText = "Path for daemon and session logs.")]
            public string LogDir { get; set; }
        }

        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine($"Ryujinx Appliance Backend Daemon v{Version}");

            string socketPath = "/run/console/ryujinx.sock";
            string dataDir = null;
            string runnerPath = null;
            string logDir = "/var/log/console";

            Parser.Default.ParseArguments<CommandLineOptions>(args)
                .WithParsed(opts =>
                {
                    socketPath = opts.SocketPath;
                    dataDir = opts.DataDir;
                    runnerPath = opts.RunnerPath;
                    logDir = opts.LogDir;
                });

            // If environment variables are set, prefer them if options not provided
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = Environment.GetEnvironmentVariable("RYUJINX_BASE_DIR");
            }

            CancellationTokenSource cts = new();
            PosixSignalRegistration sigtermReg = null;
            PosixSignalRegistration sigintReg = null;

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    cts.Cancel();
                });
                sigintReg = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    ctx.Cancel = true;
                    cts.Cancel();
                });
            }

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            using ApplianceHost host = new(socketPath, dataDir, runnerPath);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    cts.Cancel();
                    host.Dispose();
                }
                catch { }
            };

            try
            {
                await host.StartAsync(cts.Token);
                Console.WriteLine($"Backend is running. Press Ctrl+C to shut down.");

                // Wait until cancellation
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Shutting down backend daemon...");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal backend error: {ex}");
                return 1;
            }
            finally
            {
                sigtermReg?.Dispose();
                sigintReg?.Dispose();
            }

            return 0;
        }
    }
}
