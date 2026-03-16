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
                LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                ReadyPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                SelectionReportedIdentities = new HashSet<Guid>();
                ExpectedSelectionIdentities = new HashSet<Guid>();
                SelectionUpdatedEvent = new ManualResetEvent(false);
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
            public Dictionary<string, bool> LootAppliedToParticipants;
            public HashSet<string> ReadyPeers;
            public HashSet<Guid> SelectionReportedIdentities;
            public HashSet<Guid> ExpectedSelectionIdentities;
            public ManualResetEvent SelectionUpdatedEvent;
            public int SelectionUpdateVersion;
            public int ExpectedParticipantCount;
            public bool StartMissionForClientsSent;
        }

        private readonly object _coopMissionLock = new object();
        private readonly Dictionary<string, List<CoopMissionParticipant>> _coopMissionParticipants = new Dictionary<string, List<CoopMissionParticipant>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CoopMissionSessionState> _coopMissionSessions = new Dictionary<string, CoopMissionSessionState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>> _coopMissionHenchSelections = new Dictionary<string, Dictionary<Guid, List<ParsedHenchmanSelection>>>(StringComparer.OrdinalIgnoreCase);

        private static Guid[] NormalizeCoopMissionMemberGuids(string memberListRaw, Guid activeIdentityGuid)
        {
            var memberGuids = ParseGuidsFromLooseText(memberListRaw, 8);
            if (memberGuids == null || memberGuids.Length == 0)
            {
                memberGuids = new[] { activeIdentityGuid };
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

            return memberGuids;
        }

        private static int GetCoopMissionSelectionUpdateVersion(CoopMissionSessionState coopSession)
        {
            if (coopSession == null)
            {
                return 0;
            }

            lock (coopSession.SyncRoot)
            {
                return coopSession.SelectionUpdateVersion;
            }
        }

        private static Guid[] SnapshotExpectedCoopMissionSelectionIdentities(CoopMissionSessionState coopSession, Guid[] fallbackIdentityGuids)
        {
            if (coopSession == null)
            {
                return fallbackIdentityGuids;
            }

            lock (coopSession.SyncRoot)
            {
                if (coopSession.ExpectedSelectionIdentities == null || coopSession.ExpectedSelectionIdentities.Count == 0)
                {
                    return fallbackIdentityGuids;
                }

                var snapshot = new Guid[coopSession.ExpectedSelectionIdentities.Count];
                coopSession.ExpectedSelectionIdentities.CopyTo(snapshot);
                return snapshot;
            }
        }

        private void SignalCoopMissionSelectionUpdate(CoopMissionSessionState coopSession)
        {
            if (coopSession == null)
            {
                return;
            }

            lock (coopSession.SyncRoot)
            {
                coopSession.SelectionUpdateVersion++;
                if (coopSession.SelectionUpdatedEvent != null)
                {
                    coopSession.SelectionUpdatedEvent.Set();
                }
            }
        }

        private void UpdateCoopMissionExpectedSelectionIdentities(CoopMissionSessionState coopSession, Guid[] identityGuids)
        {
            if (coopSession == null)
            {
                return;
            }

            lock (coopSession.SyncRoot)
            {
                if (coopSession.ExpectedSelectionIdentities == null)
                {
                    coopSession.ExpectedSelectionIdentities = new HashSet<Guid>();
                }
                else
                {
                    coopSession.ExpectedSelectionIdentities.Clear();
                }

                if (identityGuids != null)
                {
                    for (var i = 0; i < identityGuids.Length; i++)
                    {
                        coopSession.ExpectedSelectionIdentities.Add(identityGuids[i]);
                    }
                }

                coopSession.SelectionUpdateVersion++;
                if (coopSession.SelectionUpdatedEvent != null)
                {
                    coopSession.SelectionUpdatedEvent.Set();
                }
            }
        }

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
                return coopParsedSelections;
            }

            selectedHenchmanParseSource = "preparematch-empty";
            return coopParsedSelections ?? new List<ParsedHenchmanSelection>();
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

                if (coopParsedSelections != null)
                {
                    byIdentity[activeIdentityGuid] = new List<ParsedHenchmanSelection>(coopParsedSelections);
                }
                else
                {
                    byIdentity.Remove(activeIdentityGuid);
                }

                CoopMissionSessionState session;
                if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null)
                {
                    lock (session.SyncRoot)
                    {
                        if (session.SelectionReportedIdentities == null)
                        {
                            session.SelectionReportedIdentities = new HashSet<Guid>();
                        }

                        if (coopParsedSelections != null)
                        {
                            session.SelectionReportedIdentities.Add(activeIdentityGuid);
                        }
                        else
                        {
                            session.SelectionReportedIdentities.Remove(activeIdentityGuid);
                        }
                    }

                    SignalCoopMissionSelectionUpdate(session);
                }
            }
        }

        private static int CountMissingCoopMissionSelections(Dictionary<Guid, List<ParsedHenchmanSelection>> coopSelectionsByIdentity, Guid[] identityGuids)
        {
            if (identityGuids == null || identityGuids.Length == 0)
            {
                return 0;
            }

            var missingCount = 0;
            for (var i = 0; i < identityGuids.Length; i++)
            {
                List<ParsedHenchmanSelection> parsed;
                if (coopSelectionsByIdentity == null
                    || !coopSelectionsByIdentity.TryGetValue(identityGuids[i], out parsed)
                    || parsed == null)
                {
                    missingCount++;
                }
            }

            return missingCount;
        }

        private void ApplyCoopHostContext(string protocol, string peer, string connectionHash, string coopGroupName, Guid activeIdentityGuid)
        {
            Guid hostAccountId;
            if (CoopGroupHostRegistry.TryGetLeader(coopGroupName, out hostAccountId) && hostAccountId != Guid.Empty)
            {
                _logger.UpdateConnectionHostAccountId(protocol, peer, connectionHash, hostAccountId);
                return;
            }

            if (activeIdentityGuid != Guid.Empty)
            {
                _logger.UpdateConnectionHostAccountId(protocol, peer, connectionHash, activeIdentityGuid);
            }
        }

        private string ResolveCoopHostAccountIdText(string coopGroupName, Guid activeIdentityGuid)
        {
            Guid hostAccountId;
            if (CoopGroupHostRegistry.TryGetLeader(coopGroupName, out hostAccountId) && hostAccountId != Guid.Empty)
            {
                return hostAccountId.ToString("D");
            }

            return activeIdentityGuid != Guid.Empty ? activeIdentityGuid.ToString("D") : null;
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

        private Dictionary<Guid, List<ParsedHenchmanSelection>> WaitForCoopMissionHenchSelections(CoopMissionSessionState coopSession, string coopGroupName, Guid[] identityGuids, ManualResetEvent stopEvent)
        {
            var coopSelectionsByIdentity = SnapshotCoopMissionHenchSelections(coopGroupName);
            var expectedIdentityGuids = SnapshotExpectedCoopMissionSelectionIdentities(coopSession, identityGuids);
            var observedUpdateVersion = GetCoopMissionSelectionUpdateVersion(coopSession);
            while (CountMissingCoopMissionSelections(coopSelectionsByIdentity, expectedIdentityGuids) > 0)
            {
                if (coopSession == null || coopSession.SelectionUpdatedEvent == null)
                {
                    break;
                }

                if (stopEvent.WaitOne(0))
                {
                    break;
                }

                var shouldWait = true;
                lock (coopSession.SyncRoot)
                {
                    if (coopSession.SelectionUpdateVersion != observedUpdateVersion)
                    {
                        observedUpdateVersion = coopSession.SelectionUpdateVersion;
                        shouldWait = false;
                    }
                    else
                    {
                        coopSession.SelectionUpdatedEvent.Reset();
                    }
                }

                if (shouldWait)
                {
                    var waitIndex = WaitHandle.WaitAny(new WaitHandle[] { stopEvent, coopSession.SelectionUpdatedEvent }, Timeout.Infinite);
                    if (waitIndex == 0)
                    {
                        break;
                    }

                    observedUpdateVersion = GetCoopMissionSelectionUpdateVersion(coopSession);
                }

                coopSelectionsByIdentity = SnapshotCoopMissionHenchSelections(coopGroupName);
                expectedIdentityGuids = SnapshotExpectedCoopMissionSelectionIdentities(coopSession, identityGuids);
            }

            return coopSelectionsByIdentity;
        }

        private string BuildCoopCompressedMatchConfiguration(
            CoopMissionSessionState coopSession,
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
                var memberGuids = NormalizeCoopMissionMemberGuids(memberListRaw, activeIdentityGuid);

                if (memberGuids.Length >= 2)
                {
                    Array.Sort(memberGuids, GuidStringOrdinalComparer.Instance);

                    Guid leaderAccountId;
                    if (CoopGroupHostRegistry.TryGetLeader(coopGroupName, out leaderAccountId))
                    {
                        memberGuids = OrderGuidsWithLeaderFirst(memberGuids, leaderAccountId);
                    }

                    var identityGuids = memberGuids;
                    UpdateCoopMissionExpectedSelectionIdentities(coopSession, identityGuids);
                    var careerIndices = new int[identityGuids.Length];
                    var slots = new CareerSlot[identityGuids.Length];
                    var playerIds = new ulong[identityGuids.Length];
                    var selectedHenchmenPerPlayer = new PlayerCharacterSnapshot[identityGuids.Length][];

                    var coopSelectionsByIdentity = WaitForCoopMissionHenchSelections(coopSession, coopGroupName, identityGuids, stopEvent);

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

        private bool TryGetOrCreateCoopMissionSession(string coopGroupName, string activeIdentityHash, Guid activeIdentityGuid, int activeCareerIndex, string mapName, HashSet<string> completedStoryMissions, out CoopMissionSessionState coopSession, out string rejectionReason)
        {
            coopSession = null;
            rejectionReason = "leader-check-not-needed";
            lock (_coopMissionLock)
            {
                if (!_coopMissionSessions.TryGetValue(coopGroupName, out coopSession) || coopSession == null)
                {
                    string authorityIdentityHash;
                    int authorityCareerIndex;
                    bool useRequesterFallbackCompletedMissions;
                    string completionGateDecision;
                    var shouldApplyCompletionGate = TryResolveCoopMissionCompletionAuthority(
                        coopGroupName,
                        activeIdentityHash,
                        activeIdentityGuid,
                        activeCareerIndex,
                        out authorityIdentityHash,
                        out authorityCareerIndex,
                        out useRequesterFallbackCompletedMissions,
                        out completionGateDecision);

                    if (shouldApplyCompletionGate
                        && IsMissionCompletedForCareer(
                            authorityIdentityHash,
                            authorityCareerIndex,
                            mapName,
                            useRequesterFallbackCompletedMissions ? completedStoryMissions : null))
                    {
                        rejectionReason = completionGateDecision;
                        return false;
                    }

                    coopSession = new CoopMissionSessionState(coopGroupName);
                    _coopMissionSessions[coopGroupName] = coopSession;
                }
            }

            return coopSession != null;
        }

        private bool TryResolveCoopMissionCompletionAuthority(
            string coopGroupName,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            int activeCareerIndex,
            out string authorityIdentityHash,
            out int authorityCareerIndex,
            out bool useRequesterFallbackCompletedMissions,
            out string decision)
        {
            authorityIdentityHash = activeIdentityHash;
            authorityCareerIndex = activeCareerIndex;
            useRequesterFallbackCompletedMissions = true;
            decision = "requester-no-leader";

            Guid leaderAccountId;
            if (!CoopGroupHostRegistry.TryGetLeader(coopGroupName, out leaderAccountId) || leaderAccountId == Guid.Empty)
            {
                return !IsNullOrWhiteSpace(authorityIdentityHash);
            }

            if (leaderAccountId == activeIdentityGuid)
            {
                decision = "leader-requester";
                return !IsNullOrWhiteSpace(authorityIdentityHash);
            }

            HubPresenceRegistry.Participant leaderPresence;
            if (_hubPresenceRegistry != null
                && _hubPresenceRegistry.TryGetParticipantForAccount(leaderAccountId, out leaderPresence)
                && leaderPresence != null
                && !IsNullOrWhiteSpace(leaderPresence.IdentityHash))
            {
                authorityIdentityHash = leaderPresence.IdentityHash;
                authorityCareerIndex = leaderPresence.CareerIndex;
                useRequesterFallbackCompletedMissions = false;
                decision = "leader-hub-presence";
                return true;
            }

            var participants = GetCoopMissionParticipantsSnapshot(coopGroupName);
            for (var i = 0; i < participants.Length; i++)
            {
                var participant = participants[i];
                if (participant == null || participant.IdentityGuid != leaderAccountId || IsNullOrWhiteSpace(participant.IdentityHash))
                {
                    continue;
                }

                authorityIdentityHash = participant.IdentityHash;
                authorityCareerIndex = participant.CareerIndex;
                useRequesterFallbackCompletedMissions = false;
                decision = "leader-coop-participant";
                return true;
            }

            authorityIdentityHash = activeIdentityHash;
            authorityCareerIndex = activeCareerIndex;
            useRequesterFallbackCompletedMissions = true;
            decision = "leader-unresolved-nonhost-allowed";
            return false;
        }

        private static int CountExpectedCoopParticipants(string memberListRaw, Guid activeIdentityGuid)
        {
            var memberGuids = NormalizeCoopMissionMemberGuids(memberListRaw, activeIdentityGuid);
            return memberGuids.Length > 0 ? memberGuids.Length : 1;
        }

        private void UpdateCoopMissionExpectedParticipantCount(CoopMissionSessionState coopSession, int expectedParticipantCount)
        {
            if (coopSession == null || expectedParticipantCount <= 0)
            {
                return;
            }

            lock (coopSession.SyncRoot)
            {
                if (expectedParticipantCount > coopSession.ExpectedParticipantCount)
                {
                    coopSession.ExpectedParticipantCount = expectedParticipantCount;
                }
            }
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
                    if (coopSession.LootAppliedToParticipants == null)
                    {
                        coopSession.LootAppliedToParticipants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        coopSession.LootAppliedToParticipants.Clear();
                    }
                    if (coopSession.ReadyPeers != null)
                    {
                        coopSession.ReadyPeers.Clear();
                    }
                    if (coopSession.SelectionReportedIdentities != null)
                    {
                        coopSession.SelectionReportedIdentities.Clear();
                    }
                    if (coopSession.ExpectedSelectionIdentities != null)
                    {
                        coopSession.ExpectedSelectionIdentities.Clear();
                    }
                    coopSession.StartMissionForClientsSent = false;

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

        private void RegisterCoopMissionParticipant(string coopGroupName, string peer, NetworkStream stream, string identityHash, Guid identityGuid, int careerIndex)
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
                        CoopMissionSessionState session;
                        if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null && session.ReadyPeers != null)
                        {
                            lock (session.SyncRoot)
                            {
                                session.ReadyPeers.Remove(peer);
                            }
                        }
                        list.RemoveAt(i);
                    }
                }

                list.Add(new CoopMissionParticipant(peer, stream, identityHash, identityGuid, careerIndex));

                CoopMissionSessionState sessionToSignal;
                if (_coopMissionSessions.TryGetValue(coopGroupName, out sessionToSignal) && sessionToSignal != null && sessionToSignal.SelectionUpdatedEvent != null)
                {
                    sessionToSignal.SelectionUpdatedEvent.Set();
                }
            }

            MissionRuntimeRegistry.MarkCoopMissionParticipantJoined(coopGroupName, peer);
        }

        private CoopMissionParticipant[] GetCoopMissionParticipantsSnapshot(string coopGroupName)
        {
            if (IsNullOrWhiteSpace(coopGroupName))
            {
                return new CoopMissionParticipant[0];
            }

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null || list.Count == 0)
                {
                    return new CoopMissionParticipant[0];
                }

                return list.ToArray();
            }
        }

        private void UnregisterCoopMissionParticipant(string coopGroupName, string peer)
        {
            if (IsNullOrWhiteSpace(coopGroupName) || IsNullOrWhiteSpace(peer))
            {
                return;
            }

            MissionRuntimeRegistry.MarkCoopMissionParticipantLeft(coopGroupName, peer);

            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (!_coopMissionParticipants.TryGetValue(coopGroupName, out list) || list == null)
                {
                    return;
                }

                CoopMissionSessionState session;
                if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null && session.ReadyPeers != null)
                {
                    lock (session.SyncRoot)
                    {
                        session.ReadyPeers.Remove(peer);
                    }
                }

                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || string.Equals(list[i].Peer, peer, StringComparison.OrdinalIgnoreCase))
                    {
                        var departingIdentityGuid = list[i] != null ? list[i].IdentityGuid : Guid.Empty;

                        Dictionary<Guid, List<ParsedHenchmanSelection>> byIdentity;
                        if (_coopMissionHenchSelections.TryGetValue(coopGroupName, out byIdentity) && byIdentity != null && departingIdentityGuid != Guid.Empty)
                        {
                            byIdentity.Remove(departingIdentityGuid);
                        }

                        if (session != null)
                        {
                            lock (session.SyncRoot)
                            {
                                if (session.SelectionReportedIdentities != null && departingIdentityGuid != Guid.Empty)
                                {
                                    session.SelectionReportedIdentities.Remove(departingIdentityGuid);
                                }
                                if (session.ExpectedSelectionIdentities != null && departingIdentityGuid != Guid.Empty)
                                {
                                    session.ExpectedSelectionIdentities.Remove(departingIdentityGuid);
                                }
                            }
                        }

                        list.RemoveAt(i);
                    }
                }

                if (session != null)
                {
                    SignalCoopMissionSelectionUpdate(session);
                }
            }

            TryStopEmptyCoopMissionGroup(coopGroupName, "participant-empty");
        }

        private bool TryMarkCoopMissionReadyAndCheckAllReady(string coopGroupName, string peer, out CoopMissionSessionState session, out int readyCount, out int expectedCount, out int participantCount)
        {
            session = null;
            readyCount = 0;
            expectedCount = 0;
            participantCount = 0;

            if (IsNullOrWhiteSpace(coopGroupName) || IsNullOrWhiteSpace(peer))
            {
                return false;
            }

            lock (_coopMissionLock)
            {
                if (!_coopMissionSessions.TryGetValue(coopGroupName, out session) || session == null)
                {
                    return false;
                }

                List<CoopMissionParticipant> list;
                if (_coopMissionParticipants.TryGetValue(coopGroupName, out list) && list != null)
                {
                    participantCount = list.Count;
                }

                lock (session.SyncRoot)
                {
                    if (session.ReadyPeers == null)
                    {
                        session.ReadyPeers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    session.ReadyPeers.Add(peer);
                    readyCount = session.ReadyPeers.Count;
                    expectedCount = session.ExpectedParticipantCount > 0 ? session.ExpectedParticipantCount : participantCount;

                    if (session.StartMissionForClientsSent)
                    {
                        return false;
                    }

                    if (expectedCount > 0 && participantCount >= expectedCount && readyCount >= expectedCount)
                    {
                        session.StartMissionForClientsSent = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private void TryStopEmptyCoopMissionGroup(string coopGroupName, string reason)
        {
            if (IsNullOrWhiteSpace(coopGroupName))
            {
                return;
            }

            CoopMissionSessionState session = null;
            lock (_coopMissionLock)
            {
                List<CoopMissionParticipant> list;
                if (_coopMissionParticipants.TryGetValue(coopGroupName, out list) && list != null && list.Count > 0)
                {
                    return;
                }

                _coopMissionParticipants.Remove(coopGroupName);
                _coopMissionHenchSelections.Remove(coopGroupName);

                if (_coopMissionSessions.TryGetValue(coopGroupName, out session) && session != null)
                {
                    _coopMissionSessions.Remove(coopGroupName);
                }
            }

            if (session == null)
            {
                MissionRuntimeRegistry.MarkCoopMissionEnded(coopGroupName);
                return;
            }

            try
            {
                lock (session.SyncRoot)
                {
                    if (session.Simulation != null)
                    {
                        session.Simulation.Stop();
                        session.Simulation = null;
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (session.SelectionUpdatedEvent != null)
                {
                    session.SelectionUpdatedEvent.Close();
                }
            }
            catch
            {
            }

            MissionRuntimeRegistry.MarkCoopMissionEnded(coopGroupName);

            try
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim-cleanup",
                    coopGroupName = coopGroupName,
                    reason = reason ?? string.Empty,
                });
            }
            catch
            {
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
