using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Simulation;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private sealed class CoopMissionSessionState
        {
            public CoopMissionSessionState(string coopGroupName)
            {
                CoopGroupName = coopGroupName;
                SyncRoot = new object();
                CreatedUtc = DateTime.UtcNow;
                LootSnapshot = null;
                LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            public readonly string CoopGroupName;
            public readonly object SyncRoot;
            public readonly DateTime CreatedUtc;

            public string MapName;
            public uint Seed0;
            public uint Seed1;
            public uint Seed2;
            public uint Seed3;
            public string CompressedMatchConfiguration;
            public ServerSimulationSession Simulation;
            public LocalMissionLootController.LootGrant[] LootSnapshot;
            public Dictionary<string, bool> LootAppliedToParticipants;
        }

        private readonly object _coopMissionLock = new object();
        private readonly Dictionary<string, List<CoopMissionParticipant>> _coopMissionParticipants = new Dictionary<string, List<CoopMissionParticipant>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CoopMissionSessionState> _coopMissionSessions = new Dictionary<string, CoopMissionSessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>> _coopMissionHenchSelections = new Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>>(StringComparer.OrdinalIgnoreCase);

        private static List<ParsedHenchmanSelection> ParseCoopMissionSelections(bool hasPrepareMatchPayload, string prepareMatchSelectedHenchmen, out string selectedHenchmanParseSource)
        {
            selectedHenchmanParseSource = "none";
            if (!hasPrepareMatchPayload || IsNullOrWhiteSpace(prepareMatchSelectedHenchmen))
            {
                return null;
            }

            var coopParsedSelections = TryExtractCoopPayloadHenchmanSelections(prepareMatchSelectedHenchmen);
            if (coopParsedSelections != null && coopParsedSelections.Count > 0)
            {
                selectedHenchmanParseSource = "preparematch-selected";
            }

            return coopParsedSelections;
        }

        private static string ResolveCoopMissionMapName(bool hasPrepareMatchPayload, string prepareMatchMapName, List<string> payloadStrings, string coopGroupName)
        {
            string mapName = null;
            if (hasPrepareMatchPayload && !IsNullOrWhiteSpace(prepareMatchMapName))
            {
                mapName = prepareMatchMapName;
            }
            else if (payloadStrings != null && payloadStrings.Count >= 3 && !IsNullOrWhiteSpace(payloadStrings[2]))
            {
                mapName = payloadStrings[2];
            }
            else
            {
                var onIdx = !IsNullOrWhiteSpace(coopGroupName)
                    ? coopGroupName.IndexOf("_On_", StringComparison.OrdinalIgnoreCase)
                    : -1;
                if (onIdx >= 0)
                {
                    mapName = coopGroupName.Substring(onIdx + 4);
                    if (!IsNullOrWhiteSpace(mapName) && mapName.EndsWith("S", StringComparison.Ordinal))
                    {
                        mapName = mapName.Substring(0, mapName.Length - 1);
                    }
                }
            }

            return !IsNullOrWhiteSpace(mapName) ? mapName : "1_010_Prologue";
        }

        private void UpdateCoopMissionHenchSelections(string coopGroupName, Guid activeIdentityGuid, List<ParsedHenchmanSelection> coopParsedSelections)
        {
            lock (_coopMissionLock)
            {
                Dictionary<Guid, List<ParsedHenchmanSelection>> byIdentity;
                if (!_coopMissionHenchSelections.TryGetValue(coopGroupName, out byIdentity) || byIdentity == null)
                {
                    byIdentity = new Dictionary<Guid, List<ParsedHenchmanSelection>>();
                    _coopMissionHenchSelections[coopGroupName] = byIdentity;
                }

                if (coopParsedSelections != null && coopParsedSelections.Count > 0)
                {
                    byIdentity[activeIdentityGuid] = new List<ParsedHenchmanSelection>(coopParsedSelections);
                }
                else
                {
                    byIdentity.Remove(activeIdentityGuid);
                }
            }
        }

        private Dictionary<Guid, List<ParsedHenchmanSelection>> SnapshotCoopMissionHenchSelections(string coopGroupName)
        {
            lock (_coopMissionLock)
            {
                Dictionary<Guid, List<ParsedHenchmanSelection>> tmp;
                if (!_coopMissionHenchSelections.TryGetValue(coopGroupName, out tmp) || tmp == null)
                {
                    return null;
                }

                var snapshot = new Dictionary<Guid, List<ParsedHenchmanSelection>>();
                foreach (var kv in tmp)
                {
                    snapshot[kv.Key] = kv.Value != null
                        ? new List<ParsedHenchmanSelection>(kv.Value)
                        : null;
                }

                return snapshot;
            }
        }

        private Dictionary<Guid, List<ParsedHenchmanSelection>> WaitForCoopMissionHenchSelections(string coopGroupName, Guid[] identityGuids, ManualResetEvent stopEvent)
        {
            var coopSelectionsByIdentity = SnapshotCoopMissionHenchSelections(coopGroupName);
            var waitUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < waitUntilUtc)
            {
                var allHaveSelection = true;
                for (var i = 0; i < identityGuids.Length; i++)
                {
                    if (coopSelectionsByIdentity == null)
                    {
                        allHaveSelection = false;
                        break;
                    }

                    List<ParsedHenchmanSelection> parsed;
                    if (!coopSelectionsByIdentity.TryGetValue(identityGuids[i], out parsed) || parsed == null || parsed.Count == 0)
                    {
                        allHaveSelection = false;
                        break;
                    }
                }

                if (allHaveSelection)
                {
                    break;
                }

                SleepWithStop(stopEvent, 50);
                coopSelectionsByIdentity = SnapshotCoopMissionHenchSelections(coopGroupName);
            }

            return coopSelectionsByIdentity;
        }

        private string BuildCoopCompressedMatchConfiguration(
            string mapName,
            string coopGroupName,
            string memberListRaw,
            Guid activeIdentityGuid,
            string activeIdentityHash,
            int activeCareerIndex,
            string activeCharacterName,
            ulong gameClientEntityId,
            ManualResetEvent stopEvent)
        {
            var compressedMatchConfiguration = _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeCharacterName, gameClientEntityId);
            if (_userStore == null)
            {
                return compressedMatchConfiguration;
            }

            try
            {
                var memberGuids = ParseGuidsFromLooseText(memberListRaw, 8);
                if (memberGuids == null || memberGuids.Length == 0)
                {
                    memberGuids = new Guid[] { activeIdentityGuid };
                }
                if (!ContainsGuid(memberGuids, activeIdentityGuid))
                {
                    var extended = new Guid[memberGuids.Length + 1];
                    Array.Copy(memberGuids, 0, extended, 0, memberGuids.Length);
                    extended[extended.Length - 1] = activeIdentityGuid;
                    memberGuids = extended;
                }

                const int maxHumans = 4;
                if (memberGuids.Length > maxHumans)
                {
                    var truncated = new Guid[maxHumans];
                    Array.Copy(memberGuids, 0, truncated, 0, maxHumans);
                    memberGuids = truncated;
                }

                if (memberGuids.Length >= 2)
                {
                    Array.Sort(memberGuids, GuidStringOrdinalComparer.Instance);

                    Guid leaderAccountId;
                    if (CoopGroupHostRegistry.TryGetLeader(coopGroupName, out leaderAccountId))
                    {
                        memberGuids = OrderGuidsWithLeaderFirst(memberGuids, leaderAccountId);
                    }

                    var identityGuids = memberGuids;
                    var careerIndices = new int[identityGuids.Length];
                    var slots = new CareerSlot[identityGuids.Length];
                    var playerIds = new ulong[identityGuids.Length];
                    var selectedHenchmenPerPlayer = new PlayerCharacterSnapshot[identityGuids.Length][];

                    var coopSelectionsByIdentity = WaitForCoopMissionHenchSelections(coopGroupName, identityGuids, stopEvent);

                    for (var i = 0; i < identityGuids.Length; i++)
                    {
                        var guid = identityGuids[i];
                        var hash = guid.ToString();
                        var idx = guid == activeIdentityGuid ? activeCareerIndex : _userStore.GetLastCareerIndex(hash);
                        if (idx < 0)
                        {
                            idx = 0;
                        }

                        careerIndices[i] = idx;
                        slots[i] = _userStore.GetOrCreateCareer(hash, idx, false);

                        ulong mappedEntityId;
                        if (!TryGetGameClientEntityIdForIdentity(guid, out mappedEntityId) || mappedEntityId == 0UL)
                        {
                            mappedEntityId = ComputeFnv1a64(guid.ToByteArray());
                            if (mappedEntityId == 0UL)
                            {
                                mappedEntityId = (ulong)(i + 1);
                            }
                        }
                        playerIds[i] = mappedEntityId;

                        List<ParsedHenchmanSelection> parsedSelections;
                        if (coopSelectionsByIdentity != null
                            && coopSelectionsByIdentity.TryGetValue(guid, out parsedSelections)
                            && parsedSelections != null
                            && parsedSelections.Count > 0)
                        {
                            SerializeDefaultHenchmanCollection();
                            var snapshots = CachedHenchmanCollectionSnapshots;
                            if (snapshots != null && snapshots.Count > 0)
                            {
                                var ownerKarma = slots[i] != null ? slots[i].Karma : 0;
                                var ownerSpentKarma = slots[i] != null ? slots[i].SpentKarma : 0;
                                var ownerNuyen = slots[i] != null ? slots[i].Nuyen : 0;

                                var resolved = new List<PlayerCharacterSnapshot>();
                                for (var si = 0; si < parsedSelections.Count; si++)
                                {
                                    var selection = parsedSelections[si];
                                    if (selection.HenchmanId < 0 || selection.HenchmanId >= snapshots.Count)
                                    {
                                        continue;
                                    }

                                    var src = snapshots[selection.HenchmanId];
                                    var clone = CloneHenchSnapshotForMission(src, guid, si, ownerKarma, ownerSpentKarma, ownerNuyen);
                                    if (clone != null)
                                    {
                                        resolved.Add(clone);
                                    }
                                }

                                if (resolved.Count > 0)
                                {
                                    selectedHenchmenPerPlayer[i] = resolved.ToArray();
                                }
                            }
                        }
                    }

                    return _matchConfigurationGenerator.GetCompressedCoopMatchConfiguration(mapName, identityGuids, careerIndices, slots, playerIds, selectedHenchmenPerPlayer);
                }

                var activeSlot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                if (activeSlot != null)
                {
                    return _matchConfigurationGenerator.GetCompressedMatchConfiguration(mapName, activeIdentityGuid, activeCareerIndex, activeSlot, null, gameClientEntityId);
                }
            }
            catch
            {
            }

            return compressedMatchConfiguration;
        }

        private bool TryGetOrCreateCoopMissionSession(string coopGroupName, string activeIdentityHash, int activeCareerIndex, string mapName, HashSet<string> completedStoryMissions, out CoopMissionSessionState coopSession)
        {
            coopSession = null;
            lock (_coopMissionLock)
            {
                if (!_coopMissionSessions.TryGetValue(coopGroupName, out coopSession) || coopSession == null)
                {
                    if (IsMissionCompletedForCareer(activeIdentityHash, activeCareerIndex, mapName, completedStoryMissions))
                    {
                        return false;
                    }

                    coopSession = new CoopMissionSessionState(coopGroupName);
                    _coopMissionSessions[coopGroupName] = coopSession;
                }
            }

            return coopSession != null;
        }

        private ServerSimulationSession AcquireCoopMissionSimulation(
            CoopMissionSessionState coopSession,
            string peer,
            string coopGroupName,
            string mapName,
            string activeIdentityHash,
            int activeCareerIndex,
            ref uint seed0,
            ref uint seed1,
            ref uint seed2,
            ref uint seed3,
            ref string compressedMatchConfiguration)
        {
            ServerSimulationSession simulationSession;
            lock (coopSession.SyncRoot)
            {
                if (coopSession.Simulation != null
                    && !IsNullOrWhiteSpace(coopSession.CompressedMatchConfiguration)
                    && string.Equals(coopSession.MapName, mapName, StringComparison.OrdinalIgnoreCase))
                {
                    simulationSession = coopSession.Simulation;
                    seed0 = coopSession.Seed0;
                    seed1 = coopSession.Seed1;
                    seed2 = coopSession.Seed2;
                    seed3 = coopSession.Seed3;
                    compressedMatchConfiguration = coopSession.CompressedMatchConfiguration;

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = peer,
                        status = "coop-reuse",
                        mapName = mapName,
                        coopGroupName = coopGroupName,
                    });

                    return simulationSession;
                }

                try
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

                    coopSession.MapName = mapName;
                    coopSession.Seed0 = seed0;
                    coopSession.Seed1 = seed1;
                    coopSession.Seed2 = seed2;
                    coopSession.Seed3 = seed3;
                    coopSession.CompressedMatchConfiguration = compressedMatchConfiguration;
                    coopSession.LootSnapshot = null;
                    if (coopSession.LootAppliedToParticipants == null)
                    {
                        coopSession.LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        coopSession.LootAppliedToParticipants.Clear();
                    }

                    coopSession.Simulation = ServerSimulationSession.Create(
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
                    simulationSession = coopSession.Simulation;

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = peer,
                        status = "coop-created",
                        mapName = mapName,
                        coopGroupName = coopGroupName,
                    });

                    MissionRuntimeRegistry.MarkCoopMissionStarted(coopGroupName);
                    return simulationSession;
                }
                catch (Exception ex)
                {
                    coopSession.Simulation = null;
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "sim",
                        peer = peer,
                        status = "failed",
                        mapName = mapName,
                        coopGroupName = coopGroupName,
                        message = ex.Message,
                    });
                }
            }

            return null;
        }

        private void RegisterCoopMissionParticipant(string coopGroupName, string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || stream == null)
            {
                return;
            }

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null)
                {
                    list = new List<CoopMissionParticipant>();
                    _coopMissionParticipants[coopGroupName] = list;
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || string.Equals(list[i].Peer, peer, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                    }
                }

                list.Add(new CoopMissionParticipant(peer, stream));
            }
        }

        private void UnregisterCoopMissionParticipant(string coopGroupName, string peer)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null)
                {
                    return;
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || string.Equals(list[i].Peer, peer, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    _coopMissionParticipants.Remove(coopGroupName);
                    _coopMissionHenchSelections.Remove(coopGroupName);

                    CoopMissionSessionState session;
                    if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null)
                    {
                        _coopMissionSessions.Remove(coopGroupName);
                        try
                        {
                            lock (session.SyncRoot)
                            {
                                if (session.Simulation != null)
                                {
                                    session.Simulation.Stop();
                                    session.Simulation = null;
                                    MissionRuntimeRegistry.MarkCoopMissionEnded(coopGroupName);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void BroadcastToCoopMissionPeers(string coopGroupName, string senderPeer, byte[] decoded, string note)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || decoded == null || decoded.Length == 0)
            {
                return;
            }

            CoopMissionParticipant[] targets = null;
            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null || list.Count == 0)
                {
                    return;
                }
                targets = list.ToArray();
            }

            for (var i = 0; i < targets.Length; i++)
            {
                var t = targets[i];
                if (t == null || t.Stream == null)
                {
                    continue;
                }
                if (!IsNullOrWhiteSpace(senderPeer) && string.Equals(t.Peer, senderPeer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    SendRawFrame(t.Stream, t.Peer, decoded, note);
                }
                catch
                {
                }
            }
        }
    }
}
