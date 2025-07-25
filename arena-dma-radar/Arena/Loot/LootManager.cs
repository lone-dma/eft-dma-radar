﻿using arena_dma_radar.Arena.ArenaPlayer;
using eft_dma_shared.Common.DMA;
using eft_dma_shared.Common.DMA.ScatterAPI;
using eft_dma_shared.Common.Misc;
using eft_dma_shared.Common.Misc.Data;
using eft_dma_shared.Common.Unity;
using eft_dma_shared.Common.Unity.Collections;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace arena_dma_radar.Arena.Loot
{
    public sealed class LootManager
    {
        #region Fields/Properties/Constructor

        private readonly ulong _lgw;
        private readonly CancellationToken _ct;
        private readonly Lock _filterSync = new();
        public static readonly IReadOnlyDictionary<string, string> BLASTGANG_ITEMS = new Dictionary<string, string>
        {
            ["6669a73bef0f6220df0ed178"] = "Bomb",
            ["670fa9618f83118aed06c89b"] = "M18",
        };

        /// <summary>
        /// All loot (unfiltered).
        /// </summary>
        public IReadOnlyList<LootItem> UnfilteredLoot { get; private set; }

        /// <summary>
        /// All loot (with filter applied).
        /// </summary>
        public IReadOnlyList<LootItem> FilteredLoot { get; private set; }

        /// <summary>
        /// All Static Loot Containers on the map.
        /// </summary>
        public IReadOnlyList<StaticLootContainer> StaticLootContainers { get; private set; }

        public LootManager(ulong localGameWorld, CancellationToken ct)
        {
            _lgw = localGameWorld;
            _ct = ct;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Force a filter refresh.
        /// Thread Safe.
        /// </summary>
        public void RefreshFilter()
        {
            if (_filterSync.TryEnter())
            {
                try
                {
                    var filter = LootFilter.Create();
                    FilteredLoot = UnfilteredLoot?
                        .Where(x => filter(x))
                        .OrderByDescending(x => x.Important)
                        .ToList();
                }
                catch { }
                finally
                {
                    _filterSync.Exit();
                }
            }
        }

        /// <summary>
        /// Refreshes loot, only call from a memory thread (Non-GUI).
        /// </summary>
        public void Refresh()
        {
            try
            {
                GetLoot();
                RefreshFilter();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LoneLogging.WriteLine($"CRITICAL ERROR - Failed to refresh loot: {ex}");
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates referenced Loot List with fresh values.
        /// </summary>
        private void GetLoot()
        {
            var lootListAddr = Memory.ReadPtr(_lgw + Offsets.ClientLocalGameWorld.LootList);
            using var lootList = MemList<ulong>.Get(lootListAddr);
            var loot = new List<LootItem>(lootList.Count);
            var containers = new List<StaticLootContainer>(64);
            var deadPlayers = Memory.Players?
                .Where(x => x.Corpse is not null)?.ToList();
            using var map = ScatterReadMap.Get();
            var round1 = map.AddRound();
            var round2 = map.AddRound();
            var round3 = map.AddRound();
            var round4 = map.AddRound();
            for (int ix = 0; ix < lootList.Count; ix++)
            {
                var i = ix;
                _ct.ThrowIfCancellationRequested();
                var lootBase = lootList[i];
                round1[i].AddEntry<MemPointer>(0, lootBase + ObjectClass.MonoBehaviourOffset); // MonoBehaviour
                round1[i].AddEntry<MemPointer>(1, lootBase + ObjectClass.To_NamePtr[0]); // C1
                round1[i].Callbacks += x1 =>
                {
                    if (x1.TryGetResult<MemPointer>(0, out var monoBehaviour) && x1.TryGetResult<MemPointer>(1, out var c1))
                    {
                        round2[i].AddEntry<MemPointer>(2,
                            monoBehaviour + MonoBehaviour.ObjectClassOffset); // InteractiveClass
                        round2[i].AddEntry<MemPointer>(3, monoBehaviour + MonoBehaviour.GameObjectOffset); // GameObject
                        round2[i].AddEntry<MemPointer>(4, c1 + ObjectClass.To_NamePtr[1]); // C2
                        round2[i].Callbacks += x2 =>
                        {
                            if (x2.TryGetResult<MemPointer>(2, out var interactiveClass) &&
                                x2.TryGetResult<MemPointer>(3, out var gameObject) &&
                                x2.TryGetResult<MemPointer>(4, out var c2))
                            {
                                round3[i].AddEntry<MemPointer>(5, c2 + ObjectClass.To_NamePtr[2]); // ClassNamePtr
                                round3[i].AddEntry<MemPointer>(6, gameObject + GameObject.ComponentsOffset); // Components
                                round3[i].AddEntry<MemPointer>(7, gameObject + GameObject.NameOffset); // PGameObjectName
                                round3[i].Callbacks += x3 =>
                                {
                                    if (x3.TryGetResult<MemPointer>(5, out var classNamePtr) &&
                                        x3.TryGetResult<MemPointer>(6, out var components)
                                        && x3.TryGetResult<MemPointer>(7, out var pGameObjectName))
                                    {
                                        round4[i].AddEntry<UTF8String>(8, classNamePtr, 64); // ClassName
                                        round4[i].AddEntry<UTF8String>(9, pGameObjectName, 64); // ObjectName
                                        round4[i].AddEntry<MemPointer>(10,
                                            components + 0x8); // T1
                                        round4[i].Callbacks += x4 =>
                                        {
                                            if (x4.TryGetResult<UTF8String>(8, out var className) &&
                                                x4.TryGetResult<UTF8String>(9, out var objectName) &&
                                                x4.TryGetResult<MemPointer>(10, out var transformInternal))
                                            {
                                                map.CompletionCallbacks += () => // Store this as callback, let scatter reads all finish first (benchmarked faster)
                                                {
                                                    _ct.ThrowIfCancellationRequested();
                                                    try
                                                    {
                                                        ProcessLootIndex(loot, containers, deadPlayers,
                                                            interactiveClass, objectName,
                                                            transformInternal, className, gameObject);
                                                    }
                                                    catch
                                                    {
                                                    }
                                                };
                                            }
                                        };
                                    }
                                };
                            }
                        };
                    }
                };
            }

            map.Execute(); // execute scatter read
            this.UnfilteredLoot = loot;
            this.StaticLootContainers = containers;
        }

        /// <summary>
        /// Process a single loot index.
        /// </summary>
        private static void ProcessLootIndex(List<LootItem> loot, List<StaticLootContainer> containers, IReadOnlyList<Player> deadPlayers,
            ulong interactiveClass, string objectName, ulong transformInternal, string className, ulong gameObject)
        {
            var isLooseLoot = className.Equals("ObservedLootItem", StringComparison.OrdinalIgnoreCase);
            var isContainer = className.Equals("LootableContainer", StringComparison.OrdinalIgnoreCase);
            if (objectName.Contains("script", StringComparison.OrdinalIgnoreCase))
            {
                //skip these. These are scripts which I think are things like landmines but not sure
            }
            else
            {
                // Get Item Position
                var pos = new UnityTransform(transformInternal, true).UpdatePosition();
                if (isContainer)
                {
                    try
                    {
                        var itemOwner = Memory.ReadPtr(interactiveClass + Offsets.LootableContainer.ItemOwner);
                        var ownerItemBase = Memory.ReadPtr(itemOwner + Offsets.LootableContainerItemOwner.RootItem);
                        var ownerItemTemplate = Memory.ReadPtr(ownerItemBase + Offsets.LootItem.Template);
                        var ownerItemBsgIdPtr = Memory.ReadValue<Types.MongoID>(ownerItemTemplate + Offsets.ItemTemplate._id);
                        var ownerItemBsgId = Memory.ReadUnityString(ownerItemBsgIdPtr.StringID);
                        bool containerOpened = Memory.ReadValue<ulong>(interactiveClass + Offsets.LootableContainer.InteractingPlayer) != 0;
                        containers.Add(new StaticLootContainer(ownerItemBsgId, containerOpened)
                        {
                            Position = pos,
                            InteractiveClass = interactiveClass,
                            GameObject = gameObject
                        });

                        NotificationsShared.Info("Attempted to add container!");
                    }
                    catch { }
                }
                else if (isLooseLoot)
                {
                    var item = Memory.ReadPtr(interactiveClass +
                                              Offsets.InteractiveLootItem.Item); //EFT.InventoryLogic.Item
                    var itemTemplate = Memory.ReadPtr(item + Offsets.LootItem.Template); //EFT.InventoryLogic.ItemTemplate
                    var BSGIdPtr = Memory.ReadValue<Types.MongoID>(itemTemplate + Offsets.ItemTemplate._id);
                    var id = Memory.ReadUnityString(BSGIdPtr.StringID);

                    if (EftDataManager.AllItems.TryGetValue(id, out var entry))
                    {
                        loot.Add(new LootItem(entry)
                        {
                            Position = pos,
                            InteractiveClass = interactiveClass
                        });
                    }
                    else if (BLASTGANG_ITEMS.TryGetValue(id, out var manualName))
                    {
                        loot.Add(new LootItem(id, manualName)
                        {
                            Position = pos,
                            InteractiveClass = interactiveClass
                        });
                    }
                }
            }
        }

        #endregion

        #region Static Public Methods

        ///This method recursively searches grids. Grids work as follows:
        ///Take a Groundcache which holds a Blackrock which holds a pistol.
        ///The Groundcache will have 1 grid array, this method searches for whats inside that grid.
        ///Then it finds a Blackrock. This method then invokes itself recursively for the Blackrock.
        ///The Blackrock has 11 grid arrays (not to be confused with slots!! - a grid array contains slots. Look at the blackrock and you'll see it has 20 slots but 11 grids).
        ///In one of those grid arrays is a pistol. This method would recursively search through each item it finds
        ///To Do: add slot logic, so we can recursively search through the pistols slots...maybe it has a high value scope or something.
        public static void GetItemsInGrid(ulong gridsArrayPtr, List<LootItem> containerLoot,
            int recurseDepth = 0)
        {
            ArgumentOutOfRangeException.ThrowIfZero(gridsArrayPtr, nameof(gridsArrayPtr));
            if (recurseDepth++ > 3) return; // Only recurse 3 layers deep (this should be plenty)
            using var gridsArray = MemArray<ulong>.Get(gridsArrayPtr);

            try
            {
                // Check all sections of the container
                foreach (var grid in gridsArray)
                {
                    var gridEnumerableClass =
                        Memory.ReadPtr(grid +
                                       Offsets.Grids
                                           .ContainedItems); // -.GClass178A->gClass1797_0x40 // Offset: 0x0040 (Type: -.GClass1797)

                    var itemListPtr =
                        Memory.ReadPtr(gridEnumerableClass +
                                       Offsets.GridContainedItems.Items); // -.GClass1797->list_0x18 // Offset: 0x0018 (Type: System.Collections.Generic.List<Item>)
                    using var itemList = MemList<ulong>.Get(itemListPtr);

                    foreach (var childItem in itemList)
                        try
                        {
                            var childItemTemplate =
                                Memory.ReadPtr(childItem +
                                               Offsets.LootItem
                                                   .Template); // EFT.InventoryLogic.Item->_template // Offset: 0x0038 (Type: EFT.InventoryLogic.ItemTemplate)
                            var childItemIdPtr = Memory.ReadValue<Types.MongoID>(childItemTemplate + Offsets.ItemTemplate._id);
                            var childItemIdStr = Memory.ReadUnityString(childItemIdPtr.StringID);
                            if (EftDataManager.AllItems.TryGetValue(childItemIdStr, out var entry))
                                containerLoot.Add(new LootItem(entry));

                            // Check to see if the child item has children
                            // Don't throw on nullPtr since GetItemsInGrid needs to record the current item still
                            var childGridsArrayPtr = Memory.ReadValue<ulong>(childItem + Offsets.LootItemMod.Grids); // Pointer
                            GetItemsInGrid(childGridsArrayPtr, containerLoot,
                                recurseDepth); // Recursively add children to the entity
                        }
                        catch { }
                }
            }
            catch { }
        }
        #endregion
    }
}
