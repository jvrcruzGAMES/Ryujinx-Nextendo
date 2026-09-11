using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Ryujinx.Appliance.Client;
using Ryujinx.Audio.Backends.SDL3;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.Cpu.LinuxKvm;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Headless;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL3;
using Ryujinx.SDL3.Common;
using Silk.NET.Vulkan;

using Ryujinx.Common.Configuration.Multiplayer;

namespace Ryujinx.GameRunner
{
    public class Program
    {
        public static string Version => "1.0.0";

        public class Options
        {
            [Option('t', "title-id", Required = false, HelpText = "Nintendo Switch Title ID.")]
            public string TitleId { get; set; }

            [Option('p', "path", Required = true, HelpText = "Path to game executable / package (.nsp, .xci, .nro, .nca).")]
            public string Path { get; set; }

            [Option('s', "session-id", Required = false, HelpText = "Appliance Game Session ID.")]
            public string SessionId { get; set; }

            [Option('b', "backend-sock", Required = false, Default = "/run/console/ryujinx.sock", HelpText = "Unix domain socket of the backend daemon.")]
            public string BackendSocket { get; set; }

            [Option('f', "fullscreen", Required = false, Default = true, HelpText = "Launch in fullscreen mode.")]
            public bool Fullscreen { get; set; }

            [Option('r', "renderer", Required = false, Default = "Vulkan", HelpText = "Graphics backend (Vulkan or OpenGL).")]
            public string Renderer { get; set; }
        }

        private static VirtualFileSystem _vfs;
        private static ContentManager _contentManager;
        private static AccountManager _accountManager;
        private static LibHacHorizonManager _libHacHorizonManager;
        private static UserChannelPersistence _userChannelPersistence;
        private static InputManager _inputManager;
        private static Switch _emulationContext;
        private static WindowBase _window;

        public static int Main(string[] args)
        {
            Console.WriteLine("Ryujinx Appliance Game Runner starting...");

            Options options = null;
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(opts => options = opts);

            if (options == null || string.IsNullOrEmpty(options.Path))
            {
                Console.Error.WriteLine("Error: Missing required argument --path");
                return 1;
            }

            try
            {
                RunGame(options);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal game runner crash: {ex}");
                return 2;
            }
        }

        private static void RunGame(Options options)
        {
            string baseDir = Environment.GetEnvironmentVariable("RYUJINX_BASE_DIR");
            AppDataManager.Initialize(baseDir);

            ConfigurationState.Initialize();
            string configPath = Path.Combine(AppDataManager.BaseDirPath, ReleaseInformation.ConfigName);
            if (File.Exists(configPath) && ConfigurationFileFormat.TryLoad(configPath, out var format))
            {
                ConfigurationState.Instance.Load(format, configPath);
            }
            else
            {
                ConfigurationState.Instance.LoadDefault();
            }

            _vfs = VirtualFileSystem.CreateInstance();
            _libHacHorizonManager = new LibHacHorizonManager();
            _libHacHorizonManager.InitializeFsServer(_vfs);
            _libHacHorizonManager.InitializeArpServer();
            _libHacHorizonManager.InitializeBcatServer();
            _libHacHorizonManager.InitializeSystemClients();

            _contentManager = new ContentManager(_vfs);
            _contentManager.LoadEntries();

            _accountManager = new AccountManager(_libHacHorizonManager.RyujinxClient, null);
            _userChannelPersistence = new UserChannelPersistence();
            _inputManager = new InputManager(new SDL3KeyboardDriver(), new SDL3GamepadDriver());

            // Choose graphics backend
            GraphicsBackend backend = options.Renderer.Equals("OpenGL", StringComparison.OrdinalIgnoreCase) 
                ? GraphicsBackend.OpenGl 
                : GraphicsBackend.Vulkan;

            WindowBase window = backend == GraphicsBackend.Vulkan
                ? new VulkanWindow(_inputManager, GraphicsDebugLevel.None, AspectRatio.Fixed16x9, enableMouse: false, HideCursorMode.Always, ignoreControllerApplet: true)
                : new OpenGLWindow(_inputManager, GraphicsDebugLevel.None, AspectRatio.Fixed16x9, enableMouse: false, HideCursorMode.Always, ignoreControllerApplet: true);

            window.IsFullscreen = options.Fullscreen;
            _window = window;

            IRenderer renderer = backend == GraphicsBackend.Vulkan && window is VulkanWindow vulkanWindow
                ? new VulkanRenderer(Vk.GetApi(), (instance, vk) => new SurfaceKHR((ulong)vulkanWindow.CreateWindowSurface(instance.Handle)), VulkanWindow.GetRequiredInstanceExtensions, string.Empty)
                : (IRenderer)new OpenGLRenderer();

            _emulationContext = new Switch(
                ConfigurationState.Instance.CreateHleConfiguration().Configure(
                    _vfs,
                    _libHacHorizonManager,
                    _contentManager,
                    _accountManager,
                    _userChannelPersistence,
                    renderer.TryMakeThreaded(BackendThreading.Off),
                    new SDL3HardwareDeviceDriver(),
                    window
                )
            );

            // Load game package
            string gamePath = options.Path;
            string ext = Path.GetExtension(gamePath).ToLowerInvariant();

            bool loaded = false;
            if (ext == ".xci")
            {
                loaded = _emulationContext.LoadXci(gamePath);
            }
            else if (ext is ".nsp" or ".pfs0")
            {
                loaded = _emulationContext.LoadNsp(gamePath);
            }
            else if (ext == ".nca")
            {
                loaded = _emulationContext.LoadNca(gamePath);
            }
            else
            {
                loaded = _emulationContext.LoadProgram(gamePath);
            }

            if (!loaded)
            {
                throw new InvalidOperationException($"Failed to load game file: {gamePath}");
            }

            // Default input configuration (Gamepad Player 1)
            var inputConfig = InputConfigDefaults.CreateDefaultControllerConfiguration(
                null, null, ControllerType.JoyconPair, PlayerIndex.Player1, false);
            inputConfig.Id = "0";

            window.Initialize(_emulationContext, [inputConfig], enableKeyboard: true, enableMouse: false);
            window.Execute();

            _emulationContext.Dispose();
            window.Dispose();
            _inputManager.Dispose();
        }
    }
}
