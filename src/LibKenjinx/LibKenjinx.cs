// State class for the library
using Gommon;
using LibHac.Account;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using LibKenjinx.Android;
using OpenTK.Audio.OpenAL;
using Ryujinx.Audio.Backends.Dummy;
using Ryujinx.Audio.Backends.SDL2;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Common.Utilities;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.HLE;
using Ryujinx.HLE.Kenjinx;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.Loaders.Npdm;
using Ryujinx.HLE.UI;
using Ryujinx.Input.HLE;
using Ryujinx.UI.Common.Configuration;
using Ryujinx.UI.Common.Configuration.System;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;               // <--- NEU
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;         // <--- NEU
using Path = System.IO.Path;
using System.Linq;


namespace LibKenjinx
{
    public static partial class LibKenjinx
    {
        internal static IHardwareDeviceDriver AudioDriver { get; set; } = new DummyHardwareDeviceDriver();

        private static readonly TitleUpdateMetadataJsonSerializerContext _titleSerializerContext = new(JsonHelper.GetDefaultSerializerOptions());
        public static SwitchDevice? SwitchDevice { get; set; }

        public static VirtualFileSystem? AndroidFileSystem { get; set; }

        public static bool Initialize(string? basePath)
        {
            if (SwitchDevice != null)
            {
                return false;
            }

            try
            {
                AppDataManager.Initialize(basePath);

                ConfigurationState.Initialize();
                LoggerModule.Initialize();

                string logDir = Path.Combine(AppDataManager.BaseDirPath, "Logs");
                FileStream logFile = FileLogTarget.PrepareLogFile(logDir);
                Logger.AddTarget(new AsyncLogTargetWrapper(
                    new FileLogTarget("file", logFile),
                    1000,
                    AsyncLogTargetOverflowAction.Block
                ));

                Logger.Notice.Print(LogClass.Application, "Initializing...");
                Logger.Notice.Print(LogClass.Application, $"Using base path: {AppDataManager.BaseDirPath}");
                AndroidFileSystem = VirtualFileSystem.CreateInstance();
                SwitchDevice = new SwitchDevice(AndroidFileSystem);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return false;
            }

            OpenALLibraryNameContainer.OverridePath = "libopenal.so";

            return true;
        }

        public static void InitializeAudio()
        {
            AudioDriver = new SDL2HardwareDeviceDriver();
        }

        public static GameStats GetGameStats()
        {
            if (SwitchDevice?.EmulationContext == null)
            {
                return new GameStats();
            }
            var context = SwitchDevice.EmulationContext;

            return new GameStats
            {
                Fifo = context.Statistics.GetFifoPercent(),
                GameFps = context.Statistics.GetGameFrameRate(),
                GameTime = context.Statistics.GetGameFrameTime()
            };
        }

        public static GameInfo? GetGameInfo(string? file)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return new GameInfo();
            }

            Logger.Info?.Print(LogClass.Application, $"Getting game info for file: {file}");

            using var stream = File.Open(file, FileMode.Open);

            return GetGameInfo(stream, new FileInfo(file).Extension.Remove('.'));
        }

        public static GameInfo? GetGameInfo(Stream gameStream, string extension)
        {
            if (SwitchDevice == null)
            {
                Logger.Error?.Print(LogClass.Application, "SwitchDevice is not initialized.");
                return null;
            }
            GameInfo gameInfo = GetDefaultInfo(gameStream);

            const Language TitleLanguage = Language.AmericanEnglish;

            BlitStruct<LibHac.Ns.ApplicationControlProperty> controlHolder = new(1);

            try
            {
                try
                {
                    if (extension == "nsp" || extension == "pfs0" || extension == "xci")
                    {
                        IFileSystem pfs;

                        bool isExeFs = false;

                        if (extension == "xci")
                        {
                            Xci xci = new(SwitchDevice.VirtualFileSystem.KeySet, gameStream.AsStorage());

                            pfs = xci.OpenPartition(XciPartitionType.Secure);
                        }
                        else
                        {
                            var pfsTemp = new PartitionFileSystem();
                            pfsTemp.Initialize(gameStream.AsStorage()).ThrowIfFailure();
                            pfs = pfsTemp;

                            // If the NSP doesn't have a main NCA, decrement the number of applications found and then continue to the next application.
                            bool hasMainNca = false;

                            foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*"))
                            {
                                if (Path.GetExtension(fileEntry.FullPath).ToLower() == ".nca")
                                {
                                    using UniqueRef<IFile> ncaFile = new();

                                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    Nca nca = new(SwitchDevice.VirtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                                    int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                                    // Some main NCAs don't have a data partition, so check if the partition exists before opening it
                                    if (nca.Header.ContentType == NcaContentType.Program && !(nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection()))
                                    {
                                        hasMainNca = true;

                                        break;
                                    }
                                }
                                else if (Path.GetFileNameWithoutExtension(fileEntry.FullPath) == "main")
                                {
                                    isExeFs = true;
                                }
                            }

                            if (!hasMainNca && !isExeFs)
                            {
                                return null;
                            }
                        }

                        if (isExeFs)
                        {
                            using UniqueRef<IFile> npdmFile = new();

                            LibHac.Result result = pfs.OpenFile(ref npdmFile.Ref, "/main.npdm".ToU8Span(), OpenMode.Read);

                            if (ResultFs.PathNotFound.Includes(result))
                            {
                                Npdm npdm = new(npdmFile.Get.AsStream());

                                gameInfo.TitleName = npdm.TitleName;
                                gameInfo.TitleId = npdm.Aci0.TitleId.ToString("x16");
                            }
                        }
                        else
                        {
                            GetControlFsAndTitleId(pfs, out IFileSystem? controlFs, out string? id);

                            gameInfo.TitleId = id;

                            if (controlFs == null)
                            {
                                Logger.Error?.Print(LogClass.Application, $"No control FS was returned. Unable to process game any further: {gameInfo.TitleName}");
                                return null;
                            }

                            // Check if there is an update available.
                            if (IsUpdateApplied(gameInfo.TitleId, out IFileSystem? updatedControlFs))
                            {
                                // Replace the original ControlFs by the updated one.
                                controlFs = updatedControlFs;
                            }

                            ReadControlData(controlFs, controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);

                            // Read the icon from the ControlFS and store it as a byte array
                            try
                            {
                                using UniqueRef<IFile> icon = new();

                                controlFs?.OpenFile(ref icon.Ref, $"/icon_{TitleLanguage}.dat".ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                using MemoryStream stream = new();

                                icon.Get.AsStream().CopyTo(stream);
                                gameInfo.Icon = stream.ToArray();
                            }
                            catch (HorizonResultException)
                            {
                                foreach (DirectoryEntryEx entry in controlFs.EnumerateEntries("/", "*"))
                                {
                                    if (entry.Name == "control.nacp")
                                    {
                                        continue;
                                    }

                                    using var icon = new UniqueRef<IFile>();

                                    controlFs?.OpenFile(ref icon.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                    using MemoryStream stream = new();

                                    icon.Get.AsStream().CopyTo(stream);
                                    gameInfo.Icon = stream.ToArray();

                                    if (gameInfo.Icon != null)
                                    {
                                        break;
                                    }
                                }
                            }

                            SwitchDevice.CreateSaveDir(id.ToULong(16), controlHolder);
                        }
                    }
                    else if (extension == "nro")
                    {
                        BinaryReader reader = new(gameStream);

                        byte[] Read(long position, int size)
                        {
                            gameStream.Seek(position, SeekOrigin.Begin);

                            return reader.ReadBytes(size);
                        }

                        gameStream.Seek(24, SeekOrigin.Begin);

                        int assetOffset = reader.ReadInt32();

                        if (Encoding.ASCII.GetString(Read(assetOffset, 4)) == "ASET")
                        {
                            byte[] iconSectionInfo = Read(assetOffset + 8, 0x10);

                            long iconOffset = BitConverter.ToInt64(iconSectionInfo, 0);
                            long iconSize = BitConverter.ToInt64(iconSectionInfo, 8);

                            ulong nacpOffset = reader.ReadUInt64();
                            ulong nacpSize = reader.ReadUInt64();

                            // Reads and stores game icon as byte array
                            if (iconSize > 0)
                            {
                                gameInfo.Icon = Read(assetOffset + iconOffset, (int)iconSize);
                            }

                            // Read the NACP data
                            Read(assetOffset + (int)nacpOffset, (int)nacpSize).AsSpan().CopyTo(controlHolder.ByteSpan);

                            GetGameInformation(ref controlHolder.Value, out gameInfo.TitleName, out _, out gameInfo.Developer, out gameInfo.Version);
                        }
                    }
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}");
                }
                catch (InvalidDataException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. {exception}");
                }
                catch (Exception exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The gameStream encountered was not of a valid type. Error: {exception}");

                    return null;
                }
            }
            catch (IOException exception)
            {
                Logger.Warning?.Print(LogClass.Application, exception.Message);
            }

            void ReadControlData(IFileSystem? controlFs, Span<byte> outProperty)
            {
                using UniqueRef<IFile> controlFile = new();

                controlFs?.OpenFile(ref controlFile.Ref, "/control.nacp".ToU8Span(), OpenMode.Read).ThrowIfFailure();
                controlFile.Get.Read(out _, 0, outProperty, ReadOption.None).ThrowIfFailure();
            }

            void GetGameInformation(ref LibHac.Ns.ApplicationControlProperty controlData, out string? titleName, out string titleId, out string? publisher, out string? version)
            {
                _ = Enum.TryParse(TitleLanguage.ToString(), out TitleLanguage desiredTitleLanguage);

                if (controlData.Title.Length > (int)desiredTitleLanguage)
                {
                    titleName = controlData.Title[(int)desiredTitleLanguage].NameString.ToString();
                    publisher = controlData.Title[(int)desiredTitleLanguage].PublisherString.ToString();
                }
                else
                {
                    titleName = null;
                    publisher = null;
                }

                if (string.IsNullOrWhiteSpace(titleName))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.NameString.IsEmpty())
                        {
                            titleName = controlTitle.NameString.ToString();

                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(publisher))
                {
                    foreach (ref readonly var controlTitle in controlData.Title)
                    {
                        if (!controlTitle.PublisherString.IsEmpty())
                        {
                            publisher = controlTitle.PublisherString.ToString();

                            break;
                        }
                    }
                }

                if (controlData.PresenceGroupId != 0)
                {
                    titleId = controlData.PresenceGroupId.ToString("x16");
                }
                else if (controlData.SaveDataOwnerId != 0)
                {
                    titleId = controlData.SaveDataOwnerId.ToString();
                }
                else if (controlData.AddOnContentBaseId != 0)
                {
                    titleId = (controlData.AddOnContentBaseId - 0x1000).ToString("x16");
                }
                else
                {
                    titleId = "0000000000000000";
                }

                version = controlData.DisplayVersionString.ToString();
            }

            void GetControlFsAndTitleId(IFileSystem pfs, out IFileSystem? controlFs, out string? titleId)
            {
                if (SwitchDevice == null)
                {
                    Logger.Error?.Print(LogClass.Application, "SwitchDevice is not initialized.");

                    controlFs = null;
                    titleId = null;
                    return;
                }
                (_, _, Nca? controlNca) = GetGameData(SwitchDevice.VirtualFileSystem, pfs, 0);

                if (controlNca == null)
                {
                    Logger.Warning?.Print(LogClass.Application, "Control NCA is null. Unable to load control FS.");
                }

                // Return the ControlFS
                controlFs = controlNca?.OpenFileSystem(NcaSectionType.Data, SwitchDevice.EnableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None);
                titleId = controlNca?.Header.TitleId.ToString("x16");
            }

            (Nca? mainNca, Nca? patchNca, Nca? controlNca) GetGameData(VirtualFileSystem fileSystem, IFileSystem pfs, int programIndex)
            {
                Nca? mainNca = null;
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, NcaContentType.Program);

                        if (nca.SectionExists(NcaSectionType.Data) && nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                        {
                            patchNca = nca;
                        }
                        else
                        {
                            mainNca = nca;
                        }
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (mainNca, patchNca, controlNca);
            }

            bool IsUpdateApplied(string? titleId, out IFileSystem? updatedControlFs)
            {
                updatedControlFs = null;

                string? updatePath = "(unknown)";

                if (SwitchDevice?.VirtualFileSystem == null)
                {
                    Logger.Error?.Print(LogClass.Application, "SwitchDevice was not initialized.");
                    return false;
                }

                try
                {
                    (Nca? patchNca, Nca? controlNca) = GetGameUpdateData(SwitchDevice.VirtualFileSystem, titleId, 0, out updatePath);

                    if (patchNca != null && controlNca != null)
                    {
                        updatedControlFs = controlNca.OpenFileSystem(NcaSectionType.Data, SwitchDevice.EnableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None);

                        return true;
                    }
                }
                catch (InvalidDataException)
                {
                    Logger.Warning?.Print(LogClass.Application, $"The header key is incorrect or missing and therefore the NCA header content type check has failed. Errored File: {updatePath}");
                }
                catch (MissingKeyException exception)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Your key set is missing a key with the name: {exception.Name}. Errored File: {updatePath}");
                }

                return false;
            }

            (Nca? patch, Nca? control) GetGameUpdateData(VirtualFileSystem fileSystem, string? titleId, int programIndex, out string? updatePath)
            {
                updatePath = null;

                if (ulong.TryParse(titleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdBase))
                {
                    // Clear the program index part.
                    titleIdBase &= ~0xFUL;

                    // Load update information if exists.
                    string titleUpdateMetadataPath = Path.Combine(AppDataManager.GamesDirPath, titleIdBase.ToString("x16"), "updates.json");

                    if (File.Exists(titleUpdateMetadataPath))
                    {
                        updatePath = JsonHelper.DeserializeFromFile(titleUpdateMetadataPath, _titleSerializerContext.TitleUpdateMetadata).Selected;

                        if (File.Exists(updatePath))
                        {
                            FileStream file = new(updatePath, FileMode.Open, FileAccess.Read);
                            IFileSystem pfs = null;

                            if (Path.GetExtension(updatePath).ToLower() == ".xci")
                            {
                                pfs = new Xci(fileSystem.KeySet, file.AsStorage()).OpenPartition(XciPartitionType.Secure);
                            }
                            else
                            {
                                var pfsTemp = new PartitionFileSystem();

                                pfsTemp.Initialize(file.AsStorage()).ThrowIfFailure();
                                pfs = pfsTemp;
                            }

                            return GetGameUpdateDataFromPartition(fileSystem, pfs, titleIdBase.ToString("x16"), programIndex);
                        }
                    }
                }

                return (null, null);
            }

            (Nca? patchNca, Nca? controlNca) GetGameUpdateDataFromPartition(VirtualFileSystem fileSystem, IFileSystem pfs, string titleId, int programIndex)
            {
                Nca? patchNca = null;
                Nca? controlNca = null;

                fileSystem.ImportTickets(pfs);

                foreach (DirectoryEntryEx fileEntry in pfs.EnumerateEntries("/", "*.nca"))
                {
                    using var ncaFile = new UniqueRef<IFile>();

                    pfs.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                    Nca nca = new(fileSystem.KeySet, ncaFile.Release().AsStorage());

                    int ncaProgramIndex = (int)(nca.Header.TitleId & 0xF);

                    if (ncaProgramIndex != programIndex)
                    {
                        continue;
                    }

                    if ($"{nca.Header.TitleId.ToString("x16")[..^3]}000" != titleId)
                    {
                        break;
                    }

                    if (nca.Header.ContentType == NcaContentType.Program)
                    {
                        patchNca = nca;
                    }
                    else if (nca.Header.ContentType == NcaContentType.Control)
                    {
                        controlNca = nca;
                    }
                }

                return (patchNca, controlNca);
            }

            return gameInfo;
        }

        private static GameInfo GetDefaultInfo(Stream gameStream)
        {
            return new GameInfo
            {
                FileSize = gameStream.Length * 0.000000000931,
                TitleName = "Unknown",
                TitleId = "0000000000000000",
                Developer = "Unknown",
                Version = "0",
                Icon = null
            };
        }

        public static string GetDlcTitleId(string path, string ncaPath)
        {
            if (File.Exists(path))
            {
                using FileStream containerFile = File.OpenRead(path);

                PartitionFileSystem partitionFileSystem = new();
                partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

                SwitchDevice?.VirtualFileSystem.ImportTickets(partitionFileSystem);

                using UniqueRef<IFile> ncaFile = new();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, ncaPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), ncaPath);
                if (nca != null)
                {
                    return nca.Header.TitleId.ToString("X16");
                }
            }

            return string.Empty;
        }


        private static Nca TryOpenNca(IStorage ncaStorage, string containerPath)
        {
            try
            {
                return new Nca(SwitchDevice?.VirtualFileSystem.KeySet, ncaStorage);
            }
            catch (Exception)
            {
            }

            return null;
        }

        public static List<string> GetDlcContentList(string path, ulong titleId)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using FileStream containerFile = File.OpenRead(path);

            PartitionFileSystem partitionFileSystem = new();
            partitionFileSystem.Initialize(containerFile.AsStorage()).ThrowIfFailure();

            SwitchDevice?.VirtualFileSystem.ImportTickets(partitionFileSystem);
            List<string> paths = [];

            foreach (DirectoryEntryEx fileEntry in partitionFileSystem.EnumerateEntries("/", "*.nca"))
            {
                using var ncaFile = new UniqueRef<IFile>();

                partitionFileSystem.OpenFile(ref ncaFile.Ref, fileEntry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();

                Nca nca = TryOpenNca(ncaFile.Get.AsStorage(), path);
                if (nca == null)
                {
                    continue;
                }

                if (nca.Header.ContentType == NcaContentType.PublicData)
                {
                    if ((nca.Header.TitleId & 0xFFFFFFFFFFFFE000) != titleId)
                    {
                        break;
                    }

                    paths.Add(fileEntry.FullPath);
                }
            }

            return paths;
        }

        public static void SetupUiHandler()
        {
            if (SwitchDevice is { } switchDevice)
            {
                switchDevice.HostUiHandler = new AndroidUIHandler();
            }
        }

        public static void SetUiHandlerResponse(bool isOkPressed, string input)
        {
            if (SwitchDevice?.HostUiHandler is AndroidUIHandler uiHandler)
            {
                uiHandler.SetResponse(isOkPressed, input);
            }
        }

        // ===== Amiibo Helpers (Kenjinx) =====
        public static bool AmiiboLoadFromBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                Logger.Warning?.Print(LogClass.Service, "[Amiibo] Load aborted: empty data.");
                return false;
            }

            var dev = SwitchDevice?.EmulationContext;
            if (dev == null)
            {
                Logger.Warning?.Print(LogClass.Service, "[Amiibo] Load aborted: no active EmulationContext.");
                return false;
            }

            try
            {
                var ok = AmiiboBridge.TryLoadVirtualAmiibo(dev, data, out string msg);
                if (ok)
                    Logger.Info?.Print(LogClass.Service, $"[Amiibo] Loaded {data.Length} bytes. {msg}");
                else
                    Logger.Warning?.Print(LogClass.Service, $"[Amiibo] Injection failed. {msg}");
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Service, $"[Amiibo] Exception: {ex}");
                return false;
            }
        }

        public static void AmiiboClear()
        {
            var dev = SwitchDevice?.EmulationContext;
            if (dev == null)
            {
                Logger.Warning?.Print(LogClass.Service, "[Amiibo] Clear aborted: no active EmulationContext.");
                return;
            }

            try
            {
                AmiiboBridge.ClearVirtualAmiibo(dev);
                Logger.Info?.Print(LogClass.Service, "[Amiibo] Cleared.");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Service, $"[Amiibo] Clear exception: {ex}");
            }
        }
        // ===== End Amiibo Helpers =====

    }

    public class SwitchDevice : IDisposable
    {
        private readonly SystemVersion _firmwareVersion;
        public VirtualFileSystem VirtualFileSystem { get; set; }
        public ContentManager ContentManager { get; set; }
        public AccountManager AccountManager { get; set; }
        public LibHacHorizonManager LibHacHorizonManager { get; set; }
        public UserChannelPersistence UserChannelPersistence { get; set; }
        public InputManager? InputManager { get; set; }
        public Switch? EmulationContext { get; set; }
        public IHostUIHandler? HostUiHandler { get; set; }
        public bool EnableLowPowerPtc { get; set; }
        public bool EnableJitCacheEviction { get; set; }

        public bool EnableFsIntegrityChecks { get; set; }

        internal void DisposeContext()
        {
            if (EmulationContext == null)
                return;

            Logger.Info?.Print(LogClass.Application, "Disposing EmulationContext");

            try
            {
                EmulationContext?.Dispose();
                EmulationContext?.DisposeGpu();
                EmulationContext = null;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error disposing EmulationContext: {ex.Message}");
            }
        }
        public void Dispose()
        {
            GC.SuppressFinalize(this);

            Logger.Info?.Print(LogClass.Application, "Disposing SwitchDevice");

            try
            {
                EmulationContext?.Dispose();
                EmulationContext?.DisposeGpu();
                EmulationContext = null;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error disposing EmulationContext: {ex.Message}");
            }

            try
            {
                InputManager?.Dispose();
                InputManager = null;
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Application, $"Error disposing InputManager: {ex.Message}");
            }

            LibHacHorizonManager = null;
            ContentManager = null;
            AccountManager = null;
            UserChannelPersistence = null;
            HostUiHandler = null;
        }

        public SwitchDevice(VirtualFileSystem virtualFileSystem)
        {
            VirtualFileSystem = virtualFileSystem;
            LibHacHorizonManager = new LibHacHorizonManager();

            LibHacHorizonManager.InitializeFsServer(VirtualFileSystem);
            LibHacHorizonManager.InitializeArpServer();
            LibHacHorizonManager.InitializeBcatServer();
            LibHacHorizonManager.InitializeSystemClients();

            ContentManager = new ContentManager(VirtualFileSystem);

            // Save data created before we supported extra data in directory save data will not work properly if
            // given empty extra data. Luckily some of that extra data can be created using the data from the
            // save data indexer, which should be enough to check access permissions for user saves.
            // Every single save data's extra data will be checked and fixed if needed each time the emulator is opened.
            // Consider removing this at some point in the future when we don't need to worry about old saves.
            VirtualFileSystem.FixExtraData(LibHacHorizonManager.RyujinxClient);

            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
            UserChannelPersistence = new UserChannelPersistence();

            _firmwareVersion = ContentManager.GetCurrentFirmwareVersion();

            if (_firmwareVersion != null)
            {
                Logger.Notice.Print(LogClass.Application, $"System Firmware Version: {_firmwareVersion.VersionString}");
            }
            else
            {
                Logger.Notice.Print(LogClass.Application, $"System Firmware not installed");
            }
        }

        public bool InitializeContext(MemoryManagerMode memoryManagerMode,
                                      bool useHypervisor,
                                      MemoryConfiguration memoryConfiguration,
                                      SystemLanguage systemLanguage,
                                      RegionCode regionCode,
                                      VSyncMode vSyncMode,
                                      bool enableDockedMode,
                                      bool enablePtc,
                                      bool enableLowPowerPtc,
                                      bool enableJitCacheEviction,
                                      bool enableInternetAccess,
                                      bool enableFsIntegrityChecks,
                                      int fsGlobalAccessLogMode,
                                      string? timeZone,
                                      bool ignoreMissingServices)
        {
            if (LibKenjinx.Renderer == null)
            {
                return false;
            }

            var renderer = LibKenjinx.Renderer;
            BackendThreading threadingMode = LibKenjinx.GraphicsConfiguration.BackendThreading;

            bool threadedGAL = threadingMode == BackendThreading.On || (threadingMode == BackendThreading.Auto && renderer.PreferThreading);

            if (threadedGAL)
            {
                renderer = new ThreadedRenderer(renderer);
            }

            EnableLowPowerPtc = enableLowPowerPtc;
            EnableJitCacheEviction = enableJitCacheEviction;
            EnableFsIntegrityChecks = enableFsIntegrityChecks;

            HLEConfiguration configuration = new HLEConfiguration(VirtualFileSystem,
                                                                  LibHacHorizonManager,
                                                                  ContentManager,
                                                                  AccountManager,
                                                                  UserChannelPersistence,
                                                                  renderer,
                                                                  LibKenjinx.AudioDriver,
                                                                  memoryConfiguration,
                                                                  HostUiHandler,
                                                                  systemLanguage,
                                                                  regionCode,
                                                                  vSyncMode,
                                                                  enableDockedMode,
                                                                  enablePtc,
                                                                  enableInternetAccess,
                                                                  EnableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                                                                  fsGlobalAccessLogMode,
                                                                  0,
                                                                  timeZone,
                                                                  memoryManagerMode,
                                                                  ignoreMissingServices,
                                                                  LibKenjinx.GraphicsConfiguration.AspectRatio,
                                                                  100,
                                                                  useHypervisor,
                                                                  "",
                                                                  Ryujinx.Common.Configuration.Multiplayer.MultiplayerMode.Disabled,
                                                                  false,
                                                                  "",
                                                                  "",
                                                                  60);

            EmulationContext = new Switch(configuration);

            return true;
        }

        public void CreateSaveDir(ulong titleId, BlitStruct<LibHac.Ns.ApplicationControlProperty> nacpData)
        {
            var applicationId = new LibHac.Ncm.ApplicationId(titleId);

            Logger.Info?.Print(LogClass.Application, $"Ensuring required savedata exists for title id: {titleId}.");

            ref LibHac.Ns.ApplicationControlProperty control = ref nacpData.Value;

            if (Utilities.IsZeros(nacpData.ByteSpan))
            {
                control = ref new BlitStruct<LibHac.Ns.ApplicationControlProperty>(1).Value;
                control.UserAccountSaveDataSize = 0x4000;
                control.UserAccountSaveDataJournalSize = 0x4000;
                control.SaveDataOwnerId = applicationId.Value;
            }

            // --- Pfade fürs physische Save-Verzeichnis (Android-Sandbox) vorbereiten
            string savesRoot = Path.Combine(
                AppDataManager.BaseDirPath,
                Ryujinx.HLE.FileSystem.VirtualFileSystem.UserNandPath,
                "save"
            );

            // Vorher-Liste der existierenden Save-Dirs merken (um Neu-Erstellung zu erkennen)
            string[] before = Array.Empty<string>();
            try
            {
                if (Directory.Exists(savesRoot))
                    before = Directory.GetDirectories(savesRoot);
            }
            catch { /* ignore */ }

            // Bestehende Horizon-APIs zum Erzeugen/Absichern der Saves aufrufen
            var rc = LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationCacheStorage(out _, out _, applicationId, in control);
            if (rc.IsFailure())
            {
                Logger.Error?.Print(LogClass.Application, $"Error calling EnsureApplicationCacheStorage. Result code {rc.ToStringWithName()}");
            }

            Uid userId = AccountManager.LastOpenedUser.UserId.ToLibHacUid();
            rc = LibHacHorizonManager.RyujinxClient.Fs.EnsureApplicationSaveData(out _, applicationId, in control, in userId);
            if (rc.IsFailure())
            {
                Logger.Error?.Print(LogClass.Application, $"Error calling EnsureApplicationSaveData. Result code {rc.ToStringWithName()}");
            }

            // Nachher-Liste der Save-Dirs holen und Differenz bilden
            string createdSaveDirName = null;
            try
            {
                Directory.CreateDirectory(savesRoot);

                var after = Directory.GetDirectories(savesRoot);
                var beforeSet = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);

                foreach (var d in after)
                {
                    if (!beforeSet.Contains(d))
                    {
                        // dies ist sehr wahrscheinlich der frisch angelegte Save-Ordner
                        createdSaveDirName = Path.GetFileName(d);
                        break;
                    }
                }
            }
            catch
            {
                // Falls das Listing scheitert, laufen wir einfach ohne Erkennung weiter.
            }

            // TitleId & TitleName bestimmen
            string titleIdHex = titleId.ToString("x16");
            string titleName = TryGetTitleName(ref control) ?? "Unknown";

            // Marker-Datei & Mapping schreiben (jetzt als Upsert, nicht mehr append-only)
            try
            {
                if (!string.IsNullOrEmpty(createdSaveDirName))
                {
                    string markerFile = Path.Combine(savesRoot, createdSaveDirName, "TITLEID.txt");
                    File.WriteAllText(markerFile, $"{titleIdHex}\n{titleName}");
                }

                UpsertTitleMapNdjson(savesRoot, titleIdHex, titleName, createdSaveDirName);
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"Save TitleId mapping write failed: {ex.Message}");
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

/// <summary>
/// Aktualisiert .../save/titleid_map.ndjson im NDJSON-Format:
/// - Liest bestehende Zeilen
/// - Ersetzt/fügt Eintrag für titleId
/// - Schreibt die Datei vollständig neu (keine unbegrenzte Größenzunahme)
/// - Überschreibt den Ordner NIE mit leerem Wert; versucht, ihn über Marker zu ermitteln
/// </summary>
private static void UpsertTitleMapNdjson(string savesRoot, string titleIdHex, string titleName, string createdFolder)
{
    Directory.CreateDirectory(savesRoot);
    string mapPath = Path.Combine(savesRoot, "titleid_map.ndjson");

    // titleId (lowercase) -> (Name, Folder, Timestamp)
    var byTitleId = new Dictionary<string, (string Name, string Folder, string Timestamp)>(StringComparer.OrdinalIgnoreCase);

    // Bestehende Datei einlesen
    try
    {
        if (File.Exists(mapPath))
        {
            foreach (var line in File.ReadLines(mapPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    string tid = root.TryGetProperty("titleId", out var tidEl) ? (tidEl.GetString() ?? "").Trim() : "";
                    if (string.IsNullOrEmpty(tid)) continue;

                    string name    = root.TryGetProperty("name", out var nameEl)       ? (nameEl.GetString()    ?? "") : "";
                    string folder  = root.TryGetProperty("folder", out var folderEl)   ? (folderEl.GetString()  ?? "") : "";
                    string ts      = root.TryGetProperty("timestamp", out var tsEl)    ? (tsEl.GetString()      ?? "") : "";

                    byTitleId[tid.ToLowerInvariant()] = (name, folder, ts);
                }
                catch
                {
                    // Korrupten Eintrag ignorieren
                }
            }
        }
    }
    catch
    {
        byTitleId.Clear();
    }

    var nowIso   = DateTime.UtcNow.ToString("O");
    var titleIdLc = (titleIdHex ?? string.Empty).ToLowerInvariant();

    // 1) Bestimme den "bestehenden" Ordner aus der Map (falls vorhanden)
    byTitleId.TryGetValue(titleIdLc, out var existing);
    string existingFolder = existing.Folder ?? "";

    // 2) Versuche, einen sinnvollen Ordner zu bestimmen:
    //    a) frisch erstellter Ordnername
    //    b) per Markerdatei in den Saves ermitteln
    //    c) bisherigen (nicht-leeren) Wert beibehalten
    string effectiveFolder = createdFolder;
    if (string.IsNullOrWhiteSpace(effectiveFolder))
    {
        effectiveFolder = ResolveSaveFolderByMarker(savesRoot, titleIdLc);
    }
    if (string.IsNullOrWhiteSpace(effectiveFolder) && !string.IsNullOrWhiteSpace(existingFolder))
    {
        effectiveFolder = existingFolder;
    }

    // 3) Markerdatei sicherstellen, falls Ordner ermittelt
    try
    {
        if (!string.IsNullOrWhiteSpace(effectiveFolder))
        {
            string markerPath = Path.Combine(savesRoot, effectiveFolder, "TITLEID.txt");
            if (!File.Exists(markerPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                File.WriteAllText(markerPath, $"{titleIdLc}\n{titleName ?? "Unknown"}");
            }
        }
    }
    catch
    {
        // Marker-Erstellung darf das Gameplay nicht stören
    }

    // 4) Upsert: niemals mit leerem Folder überschreiben
    string finalName   = string.IsNullOrWhiteSpace(titleName) ? (existing.Name ?? "") : titleName;
    string finalFolder = string.IsNullOrWhiteSpace(effectiveFolder) ? (existing.Folder ?? "") : effectiveFolder;
    string finalTs     = nowIso;

    byTitleId[titleIdLc] = (finalName ?? "", finalFolder ?? "", finalTs);

    // 5) Datei vollständig neu schreiben (stabil: nach titleId sortiert)
    try
    {
        var ordered = byTitleId
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
                $"{{\"titleId\":\"{EscapeJson(kv.Key)}\",\"name\":\"{EscapeJson(kv.Value.Name)}\"," +
                $"\"folder\":\"{EscapeJson(kv.Value.Folder)}\",\"timestamp\":\"{EscapeJson(kv.Value.Timestamp)}\"}}{Environment.NewLine}"
            );

        File.WriteAllText(mapPath, string.Concat(ordered), Encoding.UTF8);
    }
    catch
    {
        // Schreibfehler stillschweigend ignorieren
    }
}

/// <summary>
/// Durchsucht alle Save-Ordner nach einer TITLEID.txt, deren erste Zeile der titleId entspricht.
/// Gibt den Ordnernamen (z.B. "00000012") zurück oder null.
/// </summary>
private static string ResolveSaveFolderByMarker(string savesRoot, string titleIdLc)
{
    try
    {
        if (!Directory.Exists(savesRoot)) return null;

        foreach (var dir in Directory.GetDirectories(savesRoot))
        {
            string marker = Path.Combine(dir, "TITLEID.txt");
            if (!File.Exists(marker)) continue;

            try
            {
                using var sr = new StreamReader(marker, Encoding.UTF8, true);
                string first = sr.ReadLine()?.Trim()?.ToLowerInvariant();
                if (first == titleIdLc)
                {
                    return Path.GetFileName(dir);
                }
            }
            catch
            {
                // ignorieren und weiter
            }
        }
    }
    catch
    {
        // ignorieren
    }
    return null;
}


        /// <summary>
        /// Holt den (bevorzugt amerikanischen) Titel aus dem NACP, als Fallback den ersten nicht-leeren.
        /// </summary>
        private static string TryGetTitleName(ref LibHac.Ns.ApplicationControlProperty control)
        {
            try
            {
                // Preferred: AmericanEnglish
                int idx = (int)Language.AmericanEnglish;
                if (control.Title.Length > idx)
                {
                    var s = control.Title[idx].NameString.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }

                // Fallback: erstbeste nicht-leere Lokalisation
                foreach (ref readonly var t in control.Title)
                {
                    var s = t.NameString.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch { /* ignore */ }

            return null;
        }

        internal void ReloadFileSystem()
        {
            VirtualFileSystem.ReloadKeySet();
            ContentManager = new ContentManager(VirtualFileSystem);
            AccountManager = new AccountManager(LibHacHorizonManager.RyujinxClient);
        }
    }

    public class GameInfo
    {
        public double FileSize;
        public string? TitleName;
        public string? TitleId;
        public string? Developer;
        public string? Version;
        public byte[]? Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct GameInfoNative
    {
        public double FileSize;
        public char* TitleName;
        public char* TitleId;
        public char* Developer;
        public char* Version;
        public char* Icon;

        public GameInfoNative()
        {

        }

        public GameInfoNative(double fileSize, string? titleName, string? titleId, string? developer, string? version, byte[]? icon)
        {
            FileSize = fileSize;
            TitleId = (char*)Marshal.StringToHGlobalAnsi(titleId);
            Version = (char*)Marshal.StringToHGlobalAnsi(version);
            Developer = (char*)Marshal.StringToHGlobalAnsi(developer);
            TitleName = (char*)Marshal.StringToHGlobalAnsi(titleName);

            if (icon != null)
            {
                Icon = (char*)Marshal.StringToHGlobalAnsi(Convert.ToBase64String(icon));
            }
            else
            {
                Icon = (char*)0;
            }
        }

        public GameInfoNative(GameInfo info) : this(info.FileSize, info.TitleName, info.TitleId, info.Developer, info.Version, info.Icon){}
    }

    public class GameStats
    {
        public double Fifo;
        public double GameFps;
        public double GameTime;
    }
}
