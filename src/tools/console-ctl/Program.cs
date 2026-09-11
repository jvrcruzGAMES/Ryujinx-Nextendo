using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Ryujinx.Appliance.Client;

namespace Ryujinx.ConsoleCtl
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string socketPath = "/run/console/ryujinx.sock";
            bool jsonOutput = false;

            // Extract global flags
            System.Collections.Generic.List<string> filteredArgs = [];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--json")
                {
                    jsonOutput = true;
                }
                else if (args[i] == "--socket" && i + 1 < args.Length)
                {
                    socketPath = args[++i];
                }
                else
                {
                    filteredArgs.Add(args[i]);
                }
            }

            if (filteredArgs.Count == 0)
            {
                PrintHelp();
                return 0;
            }

            using ConsoleBackendClient client = new(socketPath);

            try
            {
                await client.ConnectAsync(timeoutMs: 3000);
            }
            catch (Exception ex)
            {
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code = ErrorCodes.BackendNotReady, message = $"Failed to connect to backend at '{socketPath}': {ex.Message}" } }));
                }
                else
                {
                    Console.Error.WriteLine($"Error: Cannot connect to Ryujinx backend at '{socketPath}' ({ex.Message}). Is ryujinx-backend running?");
                }
                return 1;
            }

            string primaryCmd = filteredArgs[0].ToLowerInvariant();
            string subCmd = filteredArgs.Count > 1 ? filteredArgs[1].ToLowerInvariant() : string.Empty;

            try
            {
                switch (primaryCmd)
                {
                    case "status":
                    {
                        BackendStatusDto status = await client.GetStatusAsync();
                        if (jsonOutput)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = status }));
                        }
                        else
                        {
                            PrintFormattedStatus(status);
                        }
                        return 0;
                    }

                    case "keys":
                    {
                        if (subCmd == "install" && filteredArgs.Count > 2)
                        {
                            string path = filteredArgs[2];
                            InstallResultDto res = await client.InstallKeysAsync(path);
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = res }));
                            }
                            else
                            {
                                Console.WriteLine($"Keys Installation: {res.Status} ({res.Version})");
                            }
                            return res.Status == "INSTALLED" ? 0 : 1;
                        }
                        else
                        {
                            KeyStatusDto status = await client.GetKeyStatusAsync();
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = status }));
                            }
                            else
                            {
                                Console.WriteLine($"Keys Status ...... {status.Status.ToUpperInvariant()} (Prod: {status.HasProdKeys}, Title: {status.HasTitleKeys}, Count: {status.KeyCount})");
                            }
                            return 0;
                        }
                    }

                    case "firmware":
                    {
                        if (subCmd == "install" && filteredArgs.Count > 2)
                        {
                            string path = filteredArgs[2];
                            InstallResultDto res = await client.InstallFirmwareAsync(path);
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = res }));
                            }
                            else
                            {
                                Console.WriteLine($"Firmware Installation: {res.Status} ({res.Version})");
                            }
                            return res.Status == "INSTALLED" ? 0 : 1;
                        }
                        else
                        {
                            FirmwareStatusDto status = await client.GetFirmwareStatusAsync();
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = status }));
                            }
                            else
                            {
                                Console.WriteLine($"Firmware Status .. {status.Status.ToUpperInvariant()} (Version: {status.Version})");
                            }
                            return 0;
                        }
                    }

                    case "library":
                    {
                        if (subCmd == "refresh")
                        {
                            var resp = await client.SendRequestAsync("library.refresh");
                            if (jsonOutput)
                            {
                                Console.WriteLine(resp.Result?.GetRawText());
                            }
                            else
                            {
                                Console.WriteLine("Library refreshed successfully.");
                            }
                            return 0;
                        }
                        else if (subCmd == "get" && filteredArgs.Count > 2)
                        {
                            string titleId = filteredArgs[2];
                            var resp = await client.SendRequestAsync("library.getGame", new { titleId });
                            if (jsonOutput)
                            {
                                Console.WriteLine(resp.Result?.GetRawText());
                            }
                            else
                            {
                                GameTitleDto game = JsonSerializer.Deserialize<GameTitleDto>(resp.Result.Value.GetRawText());
                                Console.WriteLine($"Title: {game.Name} [{game.TitleId}] (v{game.DisplayVersion}, {game.FileType})");
                            }
                            return 0;
                        }
                        else // list
                        {
                            GameTitleDto[] games = await client.ListGamesAsync();
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = games }));
                            }
                            else
                            {
                                Console.WriteLine($"Installed Titles ({games.Length}):");
                                foreach (var g in games)
                                {
                                    Console.WriteLine($" - {g.TitleId} | {g.Name} (v{g.DisplayVersion}, {g.FileType})");
                                }
                            }
                            return 0;
                        }
                    }

                    case "title":
                    {
                        if (subCmd == "install" && filteredArgs.Count > 2)
                        {
                            string path = filteredArgs[2];
                            InstallResultDto res = await client.InstallTitleAsync(path);
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = res }));
                            }
                            else
                            {
                                Console.WriteLine($"Title Installation: {res.Status} ({res.TitleName} [{res.TitleId}], v{res.Version})");
                            }
                            return res.Status == "INSTALLED" || res.Status == "ALREADY_INSTALLED" ? 0 : 1;
                        }
                        break;
                    }

                    case "update":
                    {
                        if (subCmd == "install" && filteredArgs.Count > 2)
                        {
                            string path = filteredArgs[2];
                            InstallResultDto res = await client.InstallUpdateAsync(path);
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = res }));
                            }
                            else
                            {
                                Console.WriteLine($"Update Installation: {res.Status} ({res.TitleId}, v{res.Version})");
                            }
                            return res.Status == "INSTALLED" ? 0 : 1;
                        }
                        break;
                    }

                    case "game":
                    {
                        if (subCmd == "launch" && filteredArgs.Count > 2)
                        {
                            string titleId = filteredArgs[2];
                            GameSessionDto session = await client.LaunchGameAsync(titleId);
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = session }));
                            }
                            else
                            {
                                Console.WriteLine($"Game Launched: {session.TitleName} (Session: {session.SessionId}, State: {session.State})");
                            }
                            return 0;
                        }
                        else if (subCmd == "stop")
                        {
                            GameSessionDto session = await client.StopGameAsync();
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = session }));
                            }
                            else
                            {
                                Console.WriteLine($"Game Stopped (State: {session.State}, ExitCode: {session.ExitCode})");
                            }
                            return 0;
                        }
                        else if (subCmd == "crash")
                        {
                            var resp = await client.SendRequestAsync("game.getLastCrash");
                            if (jsonOutput)
                            {
                                Console.WriteLine(resp.Result?.GetRawText());
                            }
                            else
                            {
                                if (resp.Result.HasValue)
                                {
                                    CrashReportDto crash = JsonSerializer.Deserialize<CrashReportDto>(resp.Result.Value.GetRawText());
                                    Console.WriteLine($"Last Crash: {crash.TitleName} [{crash.TitleId}] @ {crash.TimestampUtc}");
                                    Console.WriteLine($"Summary: {crash.Summary}");
                                    Console.WriteLine($"Log: {crash.LogPath}");
                                }
                                else
                                {
                                    Console.WriteLine("No recorded crash.");
                                }
                            }
                            return 0;
                        }
                        else // status
                        {
                            GameSessionDto session = await client.GetGameStatusAsync();
                            if (jsonOutput)
                            {
                                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = session }));
                            }
                            else
                            {
                                Console.WriteLine($"Active Game Session: {session.State} (Title: {session.TitleName}, Session: {session.SessionId})");
                            }
                            return 0;
                        }
                    }

                    case "storage":
                    {
                        StorageStatusDto storage = await client.GetStorageStatusAsync();
                        if (jsonOutput)
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result = storage }));
                        }
                        else
                        {
                            double freeGb = storage.FreeBytes / (1024.0 * 1024 * 1024);
                            double totalGb = storage.TotalBytes / (1024.0 * 1024 * 1024);
                            Console.WriteLine($"Storage Mount .... {storage.MountPoint}");
                            Console.WriteLine($"Free Space ....... {freeGb:F2} GB / {totalGb:F2} GB");
                        }
                        return 0;
                    }

                    case "input":
                    {
                        var resp = await client.SendRequestAsync("input.listDevices");
                        if (jsonOutput)
                        {
                            Console.WriteLine(resp.Result?.GetRawText());
                        }
                        else
                        {
                            InputDeviceDto[] devices = JsonSerializer.Deserialize<InputDeviceDto[]>(resp.Result.Value.GetRawText());
                            Console.WriteLine($"Connected Input Devices ({devices.Length}):");
                            foreach (var d in devices)
                            {
                                Console.WriteLine($" - {d.Id}: {d.Name} ({d.Type}, Connected: {d.Connected})");
                            }
                        }
                        return 0;
                    }

                    default:
                        Console.Error.WriteLine($"Unknown command: {primaryCmd}");
                        PrintHelp();
                        return 1;
                }
            }
            catch (BackendException bex)
            {
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = bex.Error }));
                }
                else
                {
                    Console.Error.WriteLine($"Backend Error [{bex.Code}]: {bex.Message}");
                }
                return 1;
            }
            catch (Exception ex)
            {
                if (jsonOutput)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { code = ErrorCodes.InternalError, message = ex.Message } }));
                }
                else
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                }
                return 1;
            }

            return 0;
        }

        private static void PrintFormattedStatus(BackendStatusDto status)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine("           SwitchPi Console Backend              ");
            Console.WriteLine("=================================================");
            Console.WriteLine($"Backend ............... {status.Status}");
            Console.WriteLine($"Ryujinx ............... {status.RyujinxVersion}");
            Console.WriteLine($"Architecture .......... {status.Architecture}");
            Console.WriteLine($"CPU Backend ........... {status.CpuBackendActive}");
            Console.WriteLine($"KVM ................... {(status.KvmAvailable ? "available" : "unavailable")}");
            Console.WriteLine($"Keys .................. {status.KeysStatus}");
            Console.WriteLine($"Firmware .............. {(string.IsNullOrEmpty(status.FirmwareVersion) ? status.FirmwareStatus : status.FirmwareVersion)}");
            Console.WriteLine($"Games ................. {status.LibraryCount}");
            Console.WriteLine($"Current Game .......... {status.CurrentGame}");
            Console.WriteLine("=================================================");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: console-ctl [options] <command> [subcommand] [arguments]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  status                              Display backend health & system overview");
            Console.WriteLine("  keys status                         Display decryption keys status");
            Console.WriteLine("  keys install <file>                 Install prod.keys or title.keys");
            Console.WriteLine("  firmware status                     Display installed firmware version");
            Console.WriteLine("  firmware install <file>             Install firmware from ZIP or XCI");
            Console.WriteLine("  library list                        List installed titles");
            Console.WriteLine("  library refresh                     Rescan persistent game library");
            Console.WriteLine("  library get <title-id>              Get metadata for specific title");
            Console.WriteLine("  title install <file>                Install game NSP or XCI");
            Console.WriteLine("  update install <file>               Install game update NSP");
            Console.WriteLine("  game launch <title-id>              Launch game session");
            Console.WriteLine("  game stop                           Stop active game session");
            Console.WriteLine("  game status                         Show active game status");
            Console.WriteLine("  game crash                          Show last crash details");
            Console.WriteLine("  storage status                      Show disk usage & partition free space");
            Console.WriteLine("  input list                          List connected controller devices");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --json                              Output response as structured JSON");
            Console.WriteLine("  --socket <path>                     Override backend Unix socket path");
        }
    }
}
