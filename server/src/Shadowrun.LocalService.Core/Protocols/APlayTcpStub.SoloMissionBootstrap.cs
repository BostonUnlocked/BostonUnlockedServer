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
                SerializeDefaultHenchmanCollection();

                var snapshots = CachedHenchmanCollectionSnapshots;
                if (snapshots == null || snapshots.Count == 0)
                {
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
                        continue;
                    }

                    var src = snapshots[selection.HenchmanId];
                    var clone = CloneHenchSnapshotForMission(src, activeIdentityGuid, i, ownerKarma, ownerSpentKarma, ownerNuyen);
                    if (clone != null)
                    {
                        resolved.Add(clone);
                    }
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
                    henchCollectionCreationIndex = CachedHenchmanCollectionCreationIndex,
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
    }
}
