﻿using eft_dma_radar.Tarkov.Player;
using eft_dma_radar.Tarkov.GameWorld.Exits;
using eft_dma_radar.Tarkov.GameWorld.Explosives;
using eft_dma_radar.Tarkov.Loot;
using eft_dma_radar.DMA.ScatterAPI;
using eft_dma_radar.Unity;
using eft_dma_radar.Tarkov.Quests;
using eft_dma_radar.Misc.Workers;
using eft_dma_radar.Tarkov.Data;

namespace eft_dma_radar.Tarkov.GameWorld
{
    /// <summary>
    /// Class containing Game (Raid) instance.
    /// IDisposable.
    /// </summary>
    public sealed class LocalGameWorld : IDisposable
    {
        #region Fields / Properties / Constructors

        public static implicit operator ulong(LocalGameWorld x) => x.Base;

        /// <summary>
        /// LocalGameWorld Address.
        /// </summary>
        private ulong Base { get; }

        private readonly RegisteredPlayers _rgtPlayers;
        private readonly ExitManager _exfilManager;
        private readonly ExplosivesManager _grenadeManager;
        private readonly WorkerThread _t1;
        private readonly WorkerThread _t2;
        private readonly WorkerThread _t3;
        private readonly WorkerThread _t4;

        /// <summary>
        /// Map ID of Current Map.
        /// </summary>
        public string MapID { get; }

        public bool InRaid => !_disposed;
        public IReadOnlyCollection<PlayerBase> Players => _rgtPlayers;
        public IReadOnlyCollection<IExplosiveItem> Explosives => _grenadeManager;
        public IReadOnlyCollection<IExitPoint> Exits => _exfilManager;
        public LocalPlayer LocalPlayer => _rgtPlayers?.LocalPlayer;
        public LootManager Loot { get; }

        public QuestManager QuestManager { get; private set; }

        public CameraManager CameraManager { get; private set; }

        /// <summary>
        /// Game Constructor.
        /// Only called internally.
        /// </summary>
        private LocalGameWorld(ulong localGameWorld, string mapID)
        {
            Base = localGameWorld;
            MapID = mapID;
            _t1 = new WorkerThread()
            {
                Name = "Realtime Worker",
                ThreadPriority = ThreadPriority.AboveNormal,
                SleepDuration = TimeSpan.FromMilliseconds(5)
            };
            _t1.PerformWork += RealtimeWorker_PerformWork;
            _t2 = new WorkerThread()
            {
                Name = "Slow Worker",
                ThreadPriority = ThreadPriority.BelowNormal,
                SleepDuration = TimeSpan.FromMilliseconds(50)
            };
            _t2.PerformWork += SlowWorker_PerformWork;
            _t3 = new WorkerThread()
            {
                Name = "Grenades Worker",
                SleepDuration = TimeSpan.FromMilliseconds(10)
            };
            _t3.PerformWork += GrenadesWorker_PerformWork;
            _t4 = new WorkerThread()
            {
                Name = "Fast Worker",
                SleepDuration = TimeSpan.FromMilliseconds(100)
            };
            _t4.PerformWork += FastWorker_PerformWork;
            // Reset static assets for a new raid/game.
            PlayerBase.Reset();
            var rgtPlayersAddr = Memory.ReadPtr(localGameWorld + Offsets.ClientLocalGameWorld.RegisteredPlayers, false);
            _rgtPlayers = new RegisteredPlayers(rgtPlayersAddr, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(_rgtPlayers.GetPlayerCount(), 1, nameof(_rgtPlayers));
            Loot = new(localGameWorld);
            _exfilManager = new(localGameWorld, _rgtPlayers.LocalPlayer.IsPmc);
            _grenadeManager = new(localGameWorld);
        }

        /// <summary>
        /// Start all Game Threads.
        /// </summary>
        public void Start()
        {
            _t1.Start();
            _t2.Start();
            _t3.Start();
            _t4.Start();
        }

        /// <summary>
        /// Blocks until a LocalGameWorld Singleton Instance can be instantiated.
        /// </summary>
        public static LocalGameWorld CreateGameInstance()
        {
            while (true)
            {
                ResourceJanitor.Run();
                Memory.ThrowIfProcessNotRunning();
                try
                {
                    var instance = GetLocalGameWorld();
                    Debug.WriteLine("Raid has started!");
                    return instance;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ERROR Instantiating Game Instance: {ex}");
                }
                finally
                {
                    Thread.Sleep(1000);
                }
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Checks if a Raid has started.
        /// Loads Local Game World resources.
        /// </summary>
        /// <returns>True if Raid has started, otherwise False.</returns>
        private static LocalGameWorld GetLocalGameWorld()
        {
            try
            {
                /// Get LocalGameWorld
                var localGameWorld = Memory.ReadPtr(MonoLib.GameWorldField, false); // Game world >> Local Game World
                /// Get Selected Map
                var mapPtr = Memory.ReadValue<ulong>(localGameWorld + Offsets.GameWorld.Location, false);
                if (mapPtr == 0x0) // Offline Mode
                {
                    var localPlayer = Memory.ReadPtr(localGameWorld + Offsets.ClientLocalGameWorld.MainPlayer, false);
                    mapPtr = Memory.ReadPtr(localPlayer + Offsets.Player.Location, false);
                }

                var map = Memory.ReadUnityString(mapPtr, 64, false);
                Debug.WriteLine("Detected Map " + map);
                if (!StaticGameData.MapNames.ContainsKey(map)) // Also makes sure we're not in the hideout
                    throw new ArgumentException("Invalid Map ID!");
                return new LocalGameWorld(localGameWorld, map);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ERROR Getting LocalGameWorld", ex);
            }
        }

        /// <summary>
        /// Main Game Loop executed by Memory Worker Thread. Refreshes/Updates Player List and performs Player Allocations.
        /// </summary>
        public void Refresh()
        {
            try
            {
                ThrowIfRaidEnded();
                if (MapID.Equals("tarkovstreets", StringComparison.OrdinalIgnoreCase) ||
                    MapID.Equals("woods", StringComparison.OrdinalIgnoreCase))
                    TryAllocateBTR();
                _rgtPlayers.Refresh(); // Check for new players, add to list, etc.
            }
            catch (OperationCanceledException ex) // Raid Ended
            {
                Debug.WriteLine(ex.Message);
                Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR - Raid ended due to unhandled exception: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Throws an exception if the current raid instance has ended.
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        private void ThrowIfRaidEnded()
        {
            for (int i = 0; i < 5; i++) // Re-attempt if read fails -- 5 times
            {
                try
                {
                    if (!IsRaidActive())
                        continue;
                    return;
                }
                catch { Thread.Sleep(10); } // short delay between read attempts
            }
            throw new OperationCanceledException("Raid has ended!"); // Still not valid? Raid must have ended.
        }

        /// <summary>
        /// Checks if the Current Raid is Active, and LocalPlayer is alive/active.
        /// </summary>
        /// <returns>True if raid is active, otherwise False.</returns>
        private bool IsRaidActive()
        {
            try
            {
                var localGameWorld = Memory.ReadPtr(MonoLib.GameWorldField, false);
                ArgumentOutOfRangeException.ThrowIfNotEqual(localGameWorld, this, nameof(localGameWorld));
                var mainPlayer = Memory.ReadPtr(localGameWorld + Offsets.ClientLocalGameWorld.MainPlayer, false);
                ArgumentOutOfRangeException.ThrowIfNotEqual(mainPlayer, _rgtPlayers.LocalPlayer, nameof(mainPlayer));
                return _rgtPlayers.GetPlayerCount() > 0;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Realtime Thread T1


        /// <summary>
        /// Managed Worker Thread that does realtime (player position/info) updates.
        /// </summary>
        private void RealtimeWorker_PerformWork(object sender, WorkerThreadArgs e)
        {
            bool espRunning = App.Config.EspWidget.Enabled; // Save resources if ESP is not running
            RealtimeLoop(espRunning); // Realtime update loop (player positions, etc.)
        }

        /// <summary>
        /// Updates all Realtime Values (View Matrix, player positions, etc.)
        /// </summary>
        private void RealtimeLoop(bool espRunning)
        {
            try
            {
                var players = _rgtPlayers.Where(x => x.IsActive && x.IsAlive);
                var localPlayer = LocalPlayer;
                if (!players.Any()) // No players - Throttle
                {
                    Thread.Sleep(1);
                    return;
                }

                using var scatterMapLease = ScatterReadMap.Lease(out var scatterMap);
                var round1 = scatterMap.AddRound(false);
                if (espRunning && CameraManager is CameraManager cm)
                {
                    cm.OnRealtimeLoop(round1[-1], localPlayer);
                }
                int i = 0;
                foreach (var player in players)
                {
                    player.OnRealtimeLoop(round1[i++], espRunning);
                }

                scatterMap.Execute(); // Execute scatter read
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR - UpdatePlayers Loop FAILED: {ex}");
            }
        }

        #endregion

        #region Slow Thread T2

        /// <summary>
        /// Managed Worker Thread that does ~Slow Local Game World Updates.
        /// *** THIS THREAD HAS A LONG RUN TIME! LOOPS ~MAY~ TAKE ~10 SECONDS OR MORE ***
        /// </summary>
        private void SlowWorker_PerformWork(object sender, WorkerThreadArgs e)
        {
            UpdateMisc(e.CancellationToken);
        }

        /// <summary>
        /// Validates Player Transforms -> Checks Exfils -> Checks Loot -> Checks Quests
        /// </summary>
        private void UpdateMisc(CancellationToken ct)
        {
            ValidatePlayerTransforms(); // Check for transform anomalies
            // Refresh exfils
            _exfilManager.Refresh();
            // Refresh Loot
            Loot.Refresh(ct);
            if (App.Config.Loot.ShowWishlist)
            {
                try
                {
                    Memory.LocalPlayer?.RefreshWishlist();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Wishlist] ERROR Refreshing: {ex}");
                }
            }
            RefreshGear(); // Update gear periodically
            if (App.Config.QuestHelper.Enabled)
                try
                {
                    if (QuestManager is null)
                    {
                        var localPlayer = LocalPlayer;
                        if (localPlayer is not null)
                            QuestManager = new QuestManager(localPlayer.Profile);
                    }
                    else
                    {
                        QuestManager.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[QuestManager] CRITICAL ERROR: {ex}");
                }
        }

        /// <summary>
        /// Refresh Gear Manager
        /// </summary>
        private void RefreshGear()
        {
            try
            {
                var players = _rgtPlayers
                    .Where(x => x.IsHostileActive);
                if (players is not null && players.Any())
                    foreach (var player in players)
                        player.RefreshGear();
            }
            catch
            {
            }
        }

        public void ValidatePlayerTransforms()
        {
            try
            {
                var players = _rgtPlayers
                    .Where(x => x.IsActive && x.IsAlive && x is not BtrOperator);
                if (players.Any()) // at least 1 player
                {
                    using var scatterMapLease = ScatterReadMap.Lease(out var scatterMap);
                    var round1 = scatterMap.AddRound();
                    var round2 = scatterMap.AddRound();
                    int i = 0;
                    foreach (var player in players)
                    {
                        player.OnValidateTransforms(round1[i], round2[i]);
                        i++;
                    }
                    scatterMap.Execute(); // execute scatter read
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR - ValidatePlayerTransforms Loop FAILED: {ex}");
            }
        }

        #endregion

        #region Grenades Thread T3

        /// <summary>
        /// Managed Worker Thread that does Grenade/Throwable updates.
        /// </summary>
        private void GrenadesWorker_PerformWork(object sender, WorkerThreadArgs e)
        {
            _grenadeManager.Refresh(e.CancellationToken);
        }

        #endregion

        #region Fast Thread T4

        /// <summary>
        /// Managed Worker Thread that does Hands Manager / DMA Toolkit updates.
        /// No long operations on this thread.
        /// </summary>
        private void FastWorker_PerformWork(object sender, WorkerThreadArgs e)
        {
            RefreshCameraManager();
            RefreshFast(e.CancellationToken);
        }

        private void RefreshCameraManager()
        {
            try
            {
                CameraManager ??= new();
            }
            catch
            {
                //Debug.WriteLine($"ERROR Refreshing Cameras! {ex}");
            }
        }

        /// <summary>
        /// Refresh various player items via Fast Worker Thread.
        /// </summary>
        private void RefreshFast(CancellationToken ct)
        {
            try
            {
                var players = _rgtPlayers
                    .Where(x => x.IsActive && x.IsAlive);
                if (players is not null && players.Any())
                    foreach (var player in players)
                    {
                        ct.ThrowIfCancellationRequested();
                        player.RefreshHands();
                    }
            }
            catch
            {
            }
        }

        #endregion

        #region BTR Vehicle

        /// <summary>
        /// Checks if there is a Bot attached to the BTR Turret and re-allocates the player instance.
        /// </summary>
        public void TryAllocateBTR()
        {
            try
            {
                var btrController = Memory.ReadPtr(this + Offsets.ClientLocalGameWorld.BtrController);
                var btrView = Memory.ReadPtr(btrController + Offsets.BtrController.BtrView);
                var btrTurretView = Memory.ReadPtr(btrView + Offsets.BTRView.turret);
                var btrOperator = Memory.ReadPtr(btrTurretView + Offsets.BTRTurretView.AttachedBot);
                _rgtPlayers.TryAllocateBTR(btrView, btrOperator);
            }
            catch
            {
                //Debug.WriteLine($"ERROR Allocating BTR: {ex}");
            }
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        public void Dispose()
        {
            bool disposed = Interlocked.Exchange(ref _disposed, true);
            if (!disposed)
            {
                _t1.Dispose();
                _t2.Dispose();
                _t3.Dispose();
                _t4.Dispose();
            }
        }

        #endregion
    }
}