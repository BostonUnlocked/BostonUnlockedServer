using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Simulation;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private PlayerCharacterSnapshot[] ResolveSelectedHenchmenForSoloMission(
            string peer,
            string mapName,
            IList<ParsedHenchmanSelection> parsedSelections,
            Guid activeIdentityGuid,
            string activeIdentityHash,
            int activeCareerIndex)
        {
            if (parsedSelections == null || parsedSelections.Count == 0)
            {
                return null;
            }

            try
            {
                int requestedCreationIndex;
                var snapshots = GetSnapshotsForSelectionCollection(parsedSelections, out requestedCreationIndex);
                if (snapshots == null || snapshots.Count == 0)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "henchman-selection-collection-miss",
                        peer = peer,
                        mapName = mapName,
                        requestedCreationIndex = requestedCreationIndex,
                        currentCreationIndex = CachedHenchmanCollectionCreationIndex,
                        selectionCount = parsedSelections.Count,
                    });
                    return null;
                }

                var ownerKarma = 0;
                var ownerSpentKarma = 0;
                var ownerNuyen = 0;
                CareerSlot slotForWallet = null;
                if (_userStore != null)
                {
                    try
                    {
                        slotForWallet = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                    }
                    catch
                    {
                        slotForWallet = null;
                    }
                }

                if (slotForWallet != null)
                {
                    ownerKarma = slotForWallet.Karma;
                    ownerSpentKarma = slotForWallet.SpentKarma;
                    ownerNuyen = slotForWallet.Nuyen;
                }

                var resolved = new List<PlayerCharacterSnapshot>();
                for (var i = 0; i < parsedSelections.Count; i++)
                {
                    var selection = parsedSelections[i];
                    if (selection.HenchmanId < 0 || selection.HenchmanId >= snapshots.Count)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "henchman-selection-invalid-index",
                            peer = peer,
                            mapName = mapName,
                            requestedCreationIndex = requestedCreationIndex,
                            henchmanId = selection.HenchmanId,
                            availableCount = snapshots.Count,
                            selectionOrdinal = i,
                        });
                        return null;
                    }

                    var src = snapshots[selection.HenchmanId];
                    if (src == null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "henchman-selection-null-snapshot",
                            peer = peer,
                            mapName = mapName,
                            requestedCreationIndex = requestedCreationIndex,
                            henchmanId = selection.HenchmanId,
                            selectionOrdinal = i,
                        });
                        return null;
                    }

                    var clone = CloneHenchSnapshotForMission(src, activeIdentityGuid, i, ownerKarma, ownerSpentKarma, ownerNuyen);
                    if (clone == null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "henchman-selection-clone-failed",
                            peer = peer,
                            mapName = mapName,
                            requestedCreationIndex = requestedCreationIndex,
                            henchmanId = selection.HenchmanId,
                            selectionOrdinal = i,
                        });
                        return null;
                    }

                    resolved.Add(clone);
                }

                var selectedHenchmen = resolved.Count > 0 ? resolved.ToArray() : null;
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-start",
                    peer = peer,
                    mapName = mapName,
                    henchSelectionCount = parsedSelections.Count,
                    henchResolvedCount = selectedHenchmen != null ? selectedHenchmen.Length : 0,
                    henchCollectionCreationIndex = parsedSelections.Count > 0 ? parsedSelections[0].CollectionCreationIndex : CachedHenchmanCollectionCreationIndex,
                    henchSelectionCreationIndex = parsedSelections.Count > 0 ? (int?)parsedSelections[0].CollectionCreationIndex : null,
                });

                return selectedHenchmen;
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-solo-start-henchman-resolution-failed",
                    peer = peer,
                    map = mapName,
                    error = ex.Message,
                });

                return null;
            }
        }

        private string BuildSoloMissionMatchConfiguration(
            string mapName,
            Guid activeIdentityGuid,
            string activeIdentityHash,
            int activeCareerIndex,
            string activeCharacterName,
            ulong gameClientEntityId,
            PlayerCharacterSnapshot[] selectedHenchmen)
        {
            var compressedMatchConfiguration = (selectedHenchmen != null && selectedHenchmen.Length > 0)
                ? _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, selectedHenchmen, gameClientEntityId)
                : _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, gameClientEntityId);

            if (_userStore == null)
            {
                return compressedMatchConfiguration;
            }

            try
            {
                CareerSlot activeSlot = !IsNullOrWhiteSpace(activeIdentityHash)
                    ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false)
                    : null;
                if (activeSlot == null)
                {
                    return compressedMatchConfiguration;
                }

                var activeSnapshot = BuildPlayerCharacterSnapshotForSlot(
                    activeIdentityGuid.ToString("D") + ":" + activeCareerIndex.ToString(),
                    activeCharacterName,
                    activeSlot);
                var activeInventory = activeSnapshot != null ? activeSnapshot.PlayerCharacterInventory : null;

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-bootstrap-active-slot",
                    mapName = mapName,
                    activeCareerIndex = activeCareerIndex,
                    activeIdentityGuid = activeIdentityGuid != Guid.Empty ? activeIdentityGuid.ToString("D") : string.Empty,
                    activeIdentityHash = activeIdentityHash ?? string.Empty,
                    activeCharacterName = activeCharacterName ?? string.Empty,
                    equippedItemsCount = activeSlot.EquippedItems != null ? activeSlot.EquippedItems.Count : 0,
                    itemPossessionsCount = activeSlot.ItemPossessions != null ? activeSlot.ItemPossessions.Count : 0,
                    persistedEquippedItems = BuildPersistedEquippedItemsDiagnostics(activeSlot),
                    persistedConsumableItemPossessions = BuildConsumableItemPossessionDiagnostics(activeSlot, 40),
                    snapshotPrimaryWeaponItemId = activeInventory != null ? GetInventoryItemId(activeInventory.PrimaryWeapon) : string.Empty,
                    snapshotSecondaryWeaponItemId = activeInventory != null ? GetInventoryItemId(activeInventory.SecondaryWeapon) : string.Empty,
                    snapshotArmorItemId = activeInventory != null ? GetInventoryItemId(activeInventory.Armor) : string.Empty,
                    snapshotEquippedItems = BuildEquippedItemsDiagnostics(activeInventory),
                    snapshotDuplicateEquippedInventoryKeys = BuildDuplicateEquippedInventoryKeyDiagnostics(activeInventory),
                });

                return selectedHenchmen != null && selectedHenchmen.Length > 0
                    ? _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, selectedHenchmen, gameClientEntityId)
                    : _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, null, gameClientEntityId);
            }
            catch
            {
                return compressedMatchConfiguration;
            }
        }

        private ServerSimulationSession CreateSoloMissionSimulation(
            string peer,
            string mapName,
            string activeIdentityHash,
            int activeCareerIndex,
            string compressedMatchConfiguration,
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3)
        {
            var storyLineForLoot = "Main Campaign";
            var chapterForLoot = 0;
            if (_userStore != null)
            {
                try
                {
                    var slotForLoot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                    if (slotForLoot != null)
                    {
                        chapterForLoot = slotForLoot.MainCampaignCurrentChapter;
                    }
                }
                catch
                {
                }
            }

            return ServerSimulationSession.Create(
                _logger,
                peer,
                _options.StaticDataDir,
                _options.StreamingAssetsDir,
                mapName,
                compressedMatchConfiguration,
                seed0,
                seed1,
                seed2,
                seed3,
                storyLineForLoot,
                chapterForLoot,
                _options != null && _options.EnableAiLogic);
        }

        private static object[] BuildPersistedEquippedItemsDiagnostics(CareerSlot slot)
        {
            if (slot == null || slot.EquippedItems == null || slot.EquippedItems.Count == 0)
            {
                return new object[0];
            }

            var keys = new List<string>(slot.EquippedItems.Keys);
            keys.Sort(StringComparer.Ordinal);
            var diagnostics = new List<object>(keys.Count);
            for (var i = 0; i < keys.Count; i++)
            {
                var slotKey = keys[i];
                if (IsNullOrWhiteSpace(slotKey))
                {
                    continue;
                }

                CareerSlot.EquippedSlotState state;
                if (!slot.EquippedItems.TryGetValue(slotKey, out state) || state == null)
                {
                    continue;
                }

                diagnostics.Add(new
                {
                    slotKey = slotKey,
                    itemId = state.ItemId ?? string.Empty,
                    inventoryKey = state.InventoryKey,
                    quality = state.Quality,
                    flavour = state.Flavour,
                });
            }

            return diagnostics.ToArray();
        }

        private static object[] BuildConsumableItemPossessionDiagnostics(CareerSlot slot, int maxEntries)
        {
            if (slot == null || slot.ItemPossessions == null || slot.ItemPossessions.Count == 0 || maxEntries <= 0)
            {
                return new object[0];
            }

            var keys = new List<string>(slot.ItemPossessions.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var diagnostics = new List<object>();
            for (var i = 0; i < keys.Count; i++)
            {
                var packedKey = keys[i];
                if (IsNullOrWhiteSpace(packedKey) || packedKey.IndexOf("Item_Consumable_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                int amount;
                if (!slot.ItemPossessions.TryGetValue(packedKey, out amount))
                {
                    continue;
                }

                diagnostics.Add(new
                {
                    possessionKey = packedKey,
                    amount = amount,
                });

                if (diagnostics.Count >= maxEntries)
                {
                    break;
                }
            }

            return diagnostics.ToArray();
        }

        private static object[] BuildDuplicateEquippedInventoryKeyDiagnostics(PlayerCharacterInventory inventory)
        {
            if (inventory == null || inventory.EquippedItems == null || inventory.EquippedItems.Count == 0)
            {
                return new object[0];
            }

            var firstByKey = new Dictionary<int, object>();
            var duplicates = new List<object>();
            for (var i = 0; i < inventory.EquippedItems.Count; i++)
            {
                var equipped = inventory.EquippedItems[i];
                if (equipped == null || equipped.Item == null)
                {
                    continue;
                }

                var key = equipped.Item.InventoryKey;
                var slotId = equipped.Definition != null ? equipped.Definition.Id : 0UL;
                var itemId = GetInventoryItemId(equipped.Item);

                object first;
                if (!firstByKey.TryGetValue(key, out first))
                {
                    firstByKey[key] = new
                    {
                        slotId = slotId,
                        itemId = itemId,
                    };
                    continue;
                }

                duplicates.Add(new
                {
                    inventoryKey = key,
                    first = first,
                    duplicate = new
                    {
                        slotId = slotId,
                        itemId = itemId,
                    },
                });
            }

            return duplicates.ToArray();
        }
    }
}
