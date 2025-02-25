global using static eft_dma_radar.Tarkov.MemoryInterface;
using eft_dma_radar.Tarkov.EFTPlayer;
using eft_dma_radar.Tarkov.Loot;
using eft_dma_radar.Tarkov.API;
using eft_dma_radar.UI.Misc;
using eft_dma_radar.UI.Radar;
using VmmFrost;
using eft_dma_radar.Tarkov.GameWorld;
using eft_dma_radar.Tarkov.GameWorld.Explosives;
using eft_dma_radar.Tarkov.GameWorld.Exits;
using System.Runtime;
using eft_dma_shared.Common.DMA;
using eft_dma_shared.Common.Unity;
using eft_dma_shared.Common.Misc.Commercial;

namespace eft_dma_radar.Tarkov
{
    internal static class MemoryInterface
    {
        private static MemDMA _memory;
        /// <summary>
        /// DMA Memory Module.
        /// </summary>
        public static MemDMA Memory
        {
            get => _memory;
            private set => _memory ??= value;
        }

        /// <summary>
        /// Initialize the Memory Interface.
        /// </summary>
        public static void ModuleInit()
        {
            Memory = new MemDMA();
        }
    }

    /// <summary>
    /// DMA Memory Module.
    /// </summary>
    public sealed class MemDMA : MemDMABase
    {
        #region Fields/Properties/Constructor/Thread Worker

        private const string _processName = "EscapeFromTarkov.exe";

        /// <summary>
        /// App Configuration.
        /// </summary>
        private static Config Config { get; } = Program.Config;

        /// <summary>
        /// Current Map ID.
        /// </summary>
        public string MapID => Game?.MapID;
        /// <summary>
        /// True if currently in a raid/match, otherwise False.
        /// </summary>
        public override bool InRaid => Game?.InRaid ?? false;
        /// <summary>
        /// True if the raid countdown has completed and the raid has started.
        /// </summary>
        public override bool RaidHasStarted => Game?.RaidHasStarted ?? false;

        private bool _ready;
        /// <summary>
        /// True if Startup was successful and waiting for raid.
        /// </summary>
        public override bool Ready => _ready;
        private bool _starting;
        /// <summary>
        /// True if in the process of starting the game.
        /// </summary>
        public override bool Starting => _starting;

        public IReadOnlyCollection<Player> Players => Game?.Players;
        public IReadOnlyCollection<IExplosiveItem> Explosives => Game?.Explosives;
        public IReadOnlyCollection<IExitPoint> Exits => Game?.Exits;
        public LocalPlayer LocalPlayer => Game?.LocalPlayer;
        public LootManager Loot => Game?.Loot;
        public QuestManager QuestManager => Game?.QuestManager;
        public LocalGameWorld Game { get; private set; }

        public MemDMA() : base(Config.FpgaAlgo, Config.MemMapEnabled)
        {
            GameStarted += MemDMA_GameStarted;
            GameStopped += MemDMA_GameStopped;
            RaidStarted += MemDMA_RaidStarted;
            RaidStopped += MemDMA_RaidStopped;
            new Thread(MemoryPrimaryWorker)
            {
                IsBackground = true
            }.Start(); // Start Memory Thread after successful startup
        }

        /// <summary>
        /// Main worker thread to perform DMA Reads on.
        /// </summary>
        private void MemoryPrimaryWorker()
        {
            LoneLogging.WriteLine("Memory thread starting...");
            while (MainForm.Window is null)
                Thread.Sleep(1);
            while (true)
            {
                try
                {
                    while (true) // Main Loop
                    {
                        RunStartupLoop();
                        OnGameStarted();
                        RunGameLoop();
                        OnGameStopped();
                    }
                }
                catch (Exception ex)
                {
                    LoneLogging.WriteLine($"FATAL ERROR on Memory Thread: {ex}");
                    OnGameStopped();
                    Thread.Sleep(1000);
                }
            }
        }

        #endregion

        #region Startup / Main Loop

        /// <summary>
        /// Starts up the Game Process and all mandatory modules.
        /// Returns to caller when the Game is ready.
        /// </summary>
        private void RunStartupLoop()
        {
            LoneLogging.WriteLine("New Game Startup");
            while (true) // Startup loop
            {
                try
                {
                    FullRefresh();
                    ResourceJanitor.Run();
                    LoadProcess();
                    LoadModules();
                    _starting = true;
                    MonoLib.InitializeEFT();
                    InputManager.Initialize(UnityBase);
                    CameraManager.Initialize();
                    _ready = true;
                    LoneLogging.WriteLine("Game Startup [OK]");
                    break;
                }
                catch (Exception ex)
                {
                    LoneLogging.WriteLine($"Game Startup [FAIL]: {ex}");
                    OnGameStopped();
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Main Game Loop Method.
        /// Returns to caller when Game is no longer running.
        /// </summary>
        private void RunGameLoop()
        {
            while (true)
            {
                try
                {
                    using (var game = Game = LocalGameWorld.CreateGameInstance())
                    {
                        OnRaidStarted();
                        game.Start();
                        while (game.InRaid)
                        {
                            if (_restartRadar)
                            {
                                LoneLogging.WriteLine("Restarting Radar per User Request.");
                                _restartRadar = false;
                                break;
                            }
                            game.Refresh();
                            Thread.Sleep(133);
                        }
                    }
                }
                catch (GameNotRunning) { break; }
                catch (Exception ex)
                {
                    LoneLogging.WriteLine($"CRITICAL ERROR in Game Loop: {ex}");
                    break;
                }
                finally
                {
                    OnRaidStopped();
                    Thread.Sleep(100);
                }
            }
            LoneLogging.WriteLine("Game is no longer running!");
        }

        /// <summary>
        /// Raised when the game is started.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MemDMA_GameStarted(object sender, EventArgs e)
        {
            _syncProcessRunning.Set();
        }

        /// <summary>
        /// Raised when the game is stopped.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MemDMA_GameStopped(object sender, EventArgs e)
        {
            _restartRadar = default;
            _starting = default;
            _ready = default;
            UnityBase = default;
            MonoBase = default;
            _pid = default;
            _syncProcessRunning.Reset();
            MonoLib.Reset();
            InputManager.Reset();
        }


        private void MemDMA_RaidStopped(object sender, EventArgs e)
        {
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            _syncInRaid.Reset();
            Game = null;
        }

        private void MemDMA_RaidStarted(object sender, EventArgs e)
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            _syncInRaid.Set();
        }

        /// <summary>
        /// Obtain the PID for the Game Process.
        /// </summary>
        private void LoadProcess()
        {
            if (_hVMM == null || !_hVMM.PidGetFromName(_processName, out uint pid))
                throw new Exception($"Unable to find '{_processName}'");
            _pid = pid;
        }

        /// <summary>
        /// Gets the Game Process Base Module Addresses.
        /// </summary>
        private void LoadModules()
        {
            var unityBase = _hVMM.ProcessGetModuleBase(_pid, "UnityPlayer.dll");
            ArgumentOutOfRangeException.ThrowIfZero(unityBase, nameof(unityBase));
            var monoBase = _hVMM.ProcessGetModuleBase(_pid, "mono-2.0-bdwgc.dll");
            ArgumentOutOfRangeException.ThrowIfZero(monoBase, nameof(monoBase));
            UnityBase = unityBase;
            MonoBase = monoBase;
        }

        #endregion

        #region R/W Methods
        /// <summary>
        /// Write value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="game">Game instance to write to.</param>
        /// <param name="addr">Address to write to.</param>
        /// <param name="value">Value to write.</param>
        public void WriteValue<T>(LocalGameWorld game, ulong addr, T value)
            where T : unmanaged
        {
            if (!game.IsSafeToWriteMem)
                throw new Exception("Not safe to write!");
            WriteValue(addr, value);
        }

        /// <summary>
        /// Write byte array buffer to Memory Address.
        /// </summary>
        /// <param name="game">Current Game Instance.</param>
        /// <param name="addr">Address to write to.</param>
        /// <param name="buffer">Buffer to write.</param>
        public void WriteBuffer<T>(LocalGameWorld game, ulong addr, Span<T> buffer)
            where T : unmanaged
        {
            if (!game.IsSafeToWriteMem)
                throw new Exception("Not safe to write!");
            WriteBuffer(addr, buffer);
        }

        #endregion

        #region Misc

        /// <summary>
        /// Throws a special exception if no longer in game.
        /// </summary>
        /// <exception cref="GameNotRunning"></exception>
        public void ThrowIfNotInGame()
        {
            FullRefresh();
            for (var i = 0; i < 5; i++)
                try
                {
                    if (!_hVMM.PidGetFromName(_processName, out uint pid))
                        throw new Exception();
                    if (pid != _pid)
                        throw new Exception();
                    return;
                }
                catch
                {
                    Thread.Sleep(150);
                }

            throw new GameNotRunning("Not in game!");
        }

        /// <summary>
        /// Get the Monitor Resolution from the Game Monitor.
        /// </summary>
        /// <returns>Monitor Resolution Result</returns>
        public Rectangle GetMonitorRes()
        {
            try
            {
                var gfx = ReadPtr(UnityBase + UnityOffsets.ModuleBase.GfxDevice, false);
                var res = ReadValue<Rectangle>(gfx + UnityOffsets.GfxDeviceClient.Viewport, false);
                if (res.Width <= 0 || res.Width > 10000 ||
                    res.Height <= 0 || res.Height > 5000)
                    throw new ArgumentOutOfRangeException(nameof(res));
                return res;
            }
            catch (Exception ex)
            {
                throw new Exception("ERROR Getting Game Monitor Res", ex);
            }
        }

        public sealed class GameNotRunning : Exception
        {
            public GameNotRunning()
            {
            }

            public GameNotRunning(string message)
                : base(message)
            {
            }

            public GameNotRunning(string message, Exception inner)
                : base(message, inner)
            {
            }
        }

        #endregion

        #region NativeHook Interop
        /// <summary>
        /// Get the Code Cave Address for NativeHook.
        /// </summary>
        /// <exception cref="Exception"></exception>
        [Obfuscation(Feature = "Virtualization", Exclude = false)]
        public override ulong GetCodeCave()
        {
            var @class = MonoLib.MonoClass.Find("Assembly-CSharp", "EFT.TarkovApplication", out _);
            return @class.FindJittedMethod("ConfigureApplication");
            // Already validated in called funcs
        }
        #endregion
    }
}