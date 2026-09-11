using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ns;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Appliance.Client;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Processes.Extensions;
using Ryujinx.HLE.Utilities;
using Path = System.IO.Path;
using SpanHelpers = LibHac.Common.SpanHelpers;

namespace Ryujinx.Appliance.Services
{
    public class GameLibraryService
    {
        private readonly VirtualFileSystem _vfs;
        private readonly Func<string, object, Task> _eventBroadcaster;
        private readonly string _iconCacheDir;
        private readonly string _gamesDir;
        private readonly ConcurrentDictionary<string, GameTitleDto> _gamesCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _scanLock = new(1, 1);

        public GameLibraryService(VirtualFileSystem vfs, Func<string, object, Task> eventBroadcaster, string iconCacheDir = null, string gamesDir = null)
        {
            _vfs = vfs;
            _eventBroadcaster = eventBroadcaster;
            _iconCacheDir = iconCacheDir ?? Path.Combine(AppDataManager.BaseDirPath, "..", "frontend", "cache", "icons");
            _gamesDir = gamesDir ?? Path.Combine(AppDataManager.BaseDirPath, "games");

            if (!Directory.Exists(_iconCacheDir))
            {
                try { Directory.CreateDirectory(_iconCacheDir); } catch { }
            }
            if (!Directory.Exists(_gamesDir))
            {
                try { Directory.CreateDirectory(_gamesDir); } catch { }
            }
        }

        public GameTitleDto[] ListGames()
        {
            return _gamesCache.Values.OrderBy(g => g.Name).ToArray();
        }

        public GameTitleDto GetGame(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
                return null;

            if (_gamesCache.TryGetValue(titleId, out var game))
                return game;

            return _gamesCache.Values.FirstOrDefault(g => 
                g.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase) || 
                g.TitleIdBase.Equals(titleId, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<int> RefreshLibraryAsync(CancellationToken ct = default)
        {
            await _scanLock.WaitAsync(ct);
            try
            {
                Logger.Notice.Print(LogClass.Application, "Refreshing game library...");
                _gamesCache.Clear();

                List<string> searchDirs = [_gamesDir];
                if (ConfigurationState.Instance?.UI?.GameDirs?.Value != null)
                {
                    foreach (string dir in ConfigurationState.Instance.UI.GameDirs.Value)
                    {
                        if (Directory.Exists(dir) && !searchDirs.Contains(dir))
                        {
                            searchDirs.Add(dir);
                        }
                    }
                }

                _vfs.ReloadKeySet();

                foreach (string dir in searchDirs)
                {
                    if (!Directory.Exists(dir))
                        continue;

                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Where(f =>
                            {
                                string ext = Path.GetExtension(f).ToLowerInvariant();
                                return ext is ".nsp" or ".xci" or ".nca" or ".nro";
                            }).ToArray();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning?.Print(LogClass.Application, $"Failed to enumerate '{dir}': {ex.Message}");
                        continue;
                    }

                    foreach (string file in files)
                    {
                        try
                        {
                            GameTitleDto dto = ParseTitleFile(file);
                            if (dto != null)
                            {
                                _gamesCache[dto.TitleId] = dto;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning?.Print(LogClass.Application, $"Failed to parse game file '{file}': {ex.Message}");
                        }
                    }
                }

                Logger.Notice.Print(LogClass.Application, $"Library refresh complete. Found {_gamesCache.Count} titles.");
                await _eventBroadcaster("library.changed", ListGames());
                return _gamesCache.Count;
            }
            finally
            {
                _scanLock.Release();
            }
        }

        public GameTitleDto ParseTitleFile(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            FileInfo info = new(filePath);

            try
            {
                using FileStream stream = File.OpenRead(filePath);
                using IStorage storage = stream.AsStorage();

                if (ext == ".nsp")
                {
                    PartitionFileSystem pfs = new();
                    pfs.Initialize(storage);
                    return ParsePfs(pfs, filePath, info.Length, "NSP");
                }
                else if (ext == ".xci")
                {
                    Xci xci = new(_vfs.KeySet, storage);
                    if (xci.HasPartition(XciPartitionType.Secure))
                    {
                        XciPartition secure = xci.OpenPartition(XciPartitionType.Secure);
                        return ParsePfs(secure, filePath, info.Length, "XCI");
                    }
                }
                else if (ext == ".nro")
                {
                    return ParseNro(filePath, info.Length);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Metadata extraction error for '{filePath}': {ex.Message}");
            }

            // Fallback generic representation
            string idHex = Path.GetFileNameWithoutExtension(filePath);
            return new GameTitleDto
            {
                TitleId = idHex,
                TitleIdBase = idHex,
                Name = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                FileType = ext.TrimStart('.').ToUpperInvariant(),
                FileSizeBytes = info.Length,
                DisplayVersion = "1.0.0",
                Developer = "Unknown"
            };
        }

        private GameTitleDto ParsePfs(IFileSystem pfs, string filePath, long fileSize, string fileType)
        {
            Dictionary<ulong, ApplicationControlProperty> controlDataDict = [];
            Dictionary<ulong, byte[]> iconDict = [];

            // Find all control NCAs and parse NACP
            foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.nca"))
            {
                using UniqueRef<IFile> ncaFile = new();
                if (pfs.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
                {
                    try
                    {
                        Nca nca = new(_vfs.KeySet, ncaFile.Get.AsStorage());
                        if (nca.Header.ContentType == NcaContentType.Control)
                        {
                            IFileSystem romfs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.None);
                            using UniqueRef<IFile> nacpFile = new();
                            if (romfs.OpenFile(ref nacpFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).IsSuccess())
                            {
                                ApplicationControlProperty control = new();
                                nacpFile.Get.Read(out _, 0, SpanHelpers.AsByteSpan(ref control), ReadOption.None);
                                ulong titleId = nca.Header.TitleId & ~0x1FFFUL;
                                controlDataDict[titleId] = control;
                            }

                            using UniqueRef<IFile> iconFile = new();
                            if (romfs.OpenFile(ref iconFile.Ref, "/icon_AmericanEnglish.dat".ToU8Span(), OpenMode.Read).IsSuccess() ||
                                romfs.OpenFile(ref iconFile.Ref, "/icon_Japanese.dat".ToU8Span(), OpenMode.Read).IsSuccess())
                            {
                                iconFile.Get.GetSize(out long iconSize);
                                byte[] iconBytes = new byte[iconSize];
                                iconFile.Get.Read(out _, 0, iconBytes, ReadOption.None);
                                ulong titleId = nca.Header.TitleId & ~0x1FFFUL;
                                iconDict[titleId] = iconBytes;
                            }
                        }
                    }
                    catch { }
                }
            }

            // Find program title ID
            ulong selectedTitleId = 0;
            foreach (DirectoryEntryEx entry in pfs.EnumerateEntries("/", "*.nca"))
            {
                using UniqueRef<IFile> ncaFile = new();
                if (pfs.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).IsSuccess())
                {
                    try
                    {
                        Nca nca = new(_vfs.KeySet, ncaFile.Get.AsStorage());
                        if (nca.Header.ContentType == NcaContentType.Program)
                        {
                            selectedTitleId = nca.Header.TitleId;
                            break;
                        }
                    }
                    catch { }
                }
            }

            ulong baseId = selectedTitleId != 0 ? (selectedTitleId & ~0x1FFFUL) : 0;
            string idHex = (selectedTitleId != 0 ? selectedTitleId : baseId).ToString("X16");
            string name = Path.GetFileNameWithoutExtension(filePath);
            string developer = "Unknown";
            string version = "1.0.0";
            string iconPath = string.Empty;

            if (controlDataDict.TryGetValue(baseId, out var controlProperty))
            {
                if (controlProperty.Title.Length > (int)Ryujinx.HLE.HOS.SystemState.TitleLanguage.AmericanEnglish)
                {
                    ref var titleRef = ref controlProperty.Title[(int)Ryujinx.HLE.HOS.SystemState.TitleLanguage.AmericanEnglish];
                    if (!titleRef.NameString.IsEmpty())
                    {
                        name = titleRef.NameString.ToString();
                        developer = titleRef.PublisherString.ToString();
                    }
                }

                if (string.IsNullOrWhiteSpace(name) || name == Path.GetFileNameWithoutExtension(filePath))
                {
                    foreach (var controlTitle in controlProperty.Title)
                    {
                        if (!controlTitle.NameString.IsEmpty())
                        {
                            name = controlTitle.NameString.ToString();
                            developer = controlTitle.PublisherString.ToString();
                            break;
                        }
                    }
                }

                version = controlProperty.DisplayVersionString.ToString();
            }

            if (iconDict.TryGetValue(baseId, out byte[] iconData) && iconData.Length > 0)
            {
                string iconFile = Path.Combine(_iconCacheDir, $"{idHex}.png");
                try
                {
                    File.WriteAllBytes(iconFile, iconData);
                    iconPath = iconFile;
                }
                catch { }
            }

            return new GameTitleDto
            {
                TitleId = idHex,
                TitleIdBase = baseId.ToString("X16"),
                Name = name,
                Developer = developer,
                Version = "0",
                DisplayVersion = string.IsNullOrEmpty(version) ? "1.0.0" : version,
                FileType = fileType,
                FilePath = filePath,
                FileSizeBytes = fileSize,
                IconPath = iconPath
            };
        }

        private GameTitleDto ParseNro(string filePath, long fileSize)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            string idHex = "0100000000000000";

            return new GameTitleDto
            {
                TitleId = idHex,
                TitleIdBase = idHex,
                Name = name,
                Developer = "Homebrew",
                Version = "0",
                DisplayVersion = "1.0.0",
                FileType = "NRO",
                FilePath = filePath,
                FileSizeBytes = fileSize
            };
        }
    }
}
