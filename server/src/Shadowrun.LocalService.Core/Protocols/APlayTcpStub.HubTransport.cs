using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private HubPlayerCharacter BuildHubPlayerCharacterFromParticipant(HubPresenceRegistry.Participant participant)
        {
            if (participant == null || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return null;
            }

            HubPlayerCharacter authoritativeCharacter;
            if (TryGetAuthoritativeHubPlayerCharacter(participant.HubId, participant.CharacterId, out authoritativeCharacter))
            {
                return authoritativeCharacter;
            }

            CareerSlot slot = null;
            if (_userStore != null && !IsNullOrWhiteSpace(participant.IdentityHash))
            {
                try
                {
                    slot = _userStore.GetOrCreateCareer(participant.IdentityHash, participant.CareerIndex, false);
                }
                catch
                {
                    slot = null;
                }
            }

            var snapshot = BuildPlayerCharacterSnapshotForSlot(participant.CharacterId, participant.CharacterName, slot);
            ulong mappedPlayerId;
            var participantAccountId = TryParseAccountIdFromCharacterIdentifier(participant.CharacterId);
            if (participantAccountId != Guid.Empty
                && TryGetGameClientEntityIdForIdentity(participantAccountId, out mappedPlayerId)
                && mappedPlayerId != 0UL)
            {
                snapshot.PlayerId = mappedPlayerId;
            }
            return new HubPlayerCharacter(participant.CharacterId, new Vector2D(participant.X, participant.Y), snapshot);
        }

        private bool TryGetAuthoritativeHubPlayerCharacter(string hubId, string characterId, out HubPlayerCharacter hubPlayerCharacter)
        {
            hubPlayerCharacter = null;
            if (_portedHubInstanceManager == null || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return false;
            }

            return portedHubInstance.HubState.PlayerCharacters.TryGetValue(characterId, out hubPlayerCharacter)
                && hubPlayerCharacter != null;
        }

        private bool TryGetHubParticipantByCharacterId(string characterId, out HubPresenceRegistry.Participant participant)
        {
            participant = null;
            if (IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            string peer;
            if (!_hubPresenceRegistry.TryGetPeerForCharacter(characterId, out peer) || IsNullOrWhiteSpace(peer))
            {
                return false;
            }

            return _hubPresenceRegistry.TryGetParticipantForPeer(peer, out participant) && participant != null;
        }

        private bool RetireDuplicateHubSessionForCharacter(string replacementPeer, string characterId, string reason)
        {
            if (IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            string existingPeer;
            if (!_hubPresenceRegistry.TryGetPeerForCharacter(characterId, out existingPeer)
                || IsNullOrWhiteSpace(existingPeer)
                || string.Equals(existingPeer, replacementPeer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var total = Interlocked.Increment(ref _hubDuplicateSessionRetiredTotal);
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-duplicate-session-retired",
                reason = reason ?? string.Empty,
                characterId = characterId,
                replacementPeer = replacementPeer ?? string.Empty,
                retiredPeer = existingPeer,
                total = total,
            });

            RemoveHubPresenceWithBroadcast(existingPeer, null, reason ?? string.Empty);
            UnregisterHubPeerStream(existingPeer, null);
            return true;
        }

        private bool TryResolveHubIdForCharacter(string characterId, out string hubId)
        {
            hubId = null;
            if (_portedHubInstanceManager == null || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var hubInstance = _portedHubInstanceManager.RequestHubInstance(characterId);
            if (hubInstance == null || IsNullOrWhiteSpace(hubInstance.HubId))
            {
                return false;
            }

            hubId = hubInstance.HubId;
            return true;
        }

        private bool TryResolveHubIdForAccount(Guid accountId, out string hubId)
        {
            hubId = null;
            if (accountId == Guid.Empty)
            {
                return false;
            }

            string characterId;
            if (!_hubPresenceRegistry.TryGetCharacterIdForAccount(accountId, out characterId))
            {
                return false;
            }

            return TryResolveHubIdForCharacter(characterId, out hubId);
        }

        private string ResolveParticipantHubId(HubPresenceRegistry.Participant participant, string fallbackHubId)
        {
            if (participant != null && !IsNullOrWhiteSpace(participant.CharacterId))
            {
                string authoritativeHubId;
                if (TryResolveHubIdForCharacter(participant.CharacterId, out authoritativeHubId))
                {
                    return authoritativeHubId;
                }
            }

            if (participant != null && !IsNullOrWhiteSpace(participant.HubId))
            {
                return participant.HubId;
            }

            return fallbackHubId;
        }

        private static HubPresenceRegistry.Participant CreateSyntheticParticipant(string hubId, string characterId, HubPlayerCharacter hubPlayerCharacter)
        {
            return new HubPresenceRegistry.Participant
            {
                Peer = string.Empty,
                AccountId = TryParseAccountIdFromCharacterIdentifier(characterId),
                IdentityHash = string.Empty,
                CareerIndex = 0,
                CharacterId = characterId ?? string.Empty,
                CharacterName = hubPlayerCharacter != null && hubPlayerCharacter.Snapshot != null ? (hubPlayerCharacter.Snapshot.CharacterName ?? string.Empty) : string.Empty,
                HubId = hubId ?? string.Empty,
                X = hubPlayerCharacter != null ? hubPlayerCharacter.CurrentPosition.X : 0f,
                Y = hubPlayerCharacter != null ? hubPlayerCharacter.CurrentPosition.Y : 0f,
            };
        }

        private byte[] BuildHubMovementPayload(string hubId, string characterId, float fallbackX, float fallbackY)
        {
            if (IsNullOrWhiteSpace(characterId))
            {
                return null;
            }

            if (_portedHubInstanceManager != null && !IsNullOrWhiteSpace(hubId))
            {
                var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
                if (portedHubInstance != null && portedHubInstance.HubState != null && portedHubInstance.HubState.PlayerCharacters != null)
                {
                    HubPlayerCharacter hubPlayerCharacter;
                    if (portedHubInstance.HubState.PlayerCharacters.TryGetValue(characterId, out hubPlayerCharacter)
                        && hubPlayerCharacter != null)
                    {
                        var authoritativeMoves = new[]
                        {
                            new KeyValuePair<string, Vector2D>(characterId, hubPlayerCharacter.CurrentPosition)
                        };

                        return BuildUtf16StringPayload(HubMovementSerializer.Serialize(authoritativeMoves));
                    }
                }
            }

            var moveRequests = new[]
            {
                new KeyValuePair<string, Vector2D>(characterId, new Vector2D(fallbackX, fallbackY))
            };

            return BuildUtf16StringPayload(HubMovementSerializer.Serialize(moveRequests));
        }

        private void RegisterHubPeerStream(string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(peer) || stream == null)
            {
                return;
            }

            lock (_hubPeerStreamsLock)
            {
                _hubPeerStreams[peer] = stream;
            }

            ConnectedPeerRegistry.MarkConnected(peer);
        }

        private void UnregisterHubPeerStream(string peer, NetworkStream stream)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_hubPeerStreamsLock)
            {
                NetworkStream existing;
                if (_hubPeerStreams.TryGetValue(peer, out existing) && (stream == null || object.ReferenceEquals(existing, stream)))
                {
                    _hubPeerStreams.Remove(peer);
                }
            }

            ConnectedPeerRegistry.MarkDisconnected(peer);

            ClearHubAnnouncementsForPeer(peer);
        }

        private static string BuildHubAnnouncementToken(string hubId, string characterId)
        {
            return (hubId ?? string.Empty) + "|" + (characterId ?? string.Empty);
        }

        private static bool IsHubAnnouncementTokenForHub(string token, string hubId)
        {
            if (IsNullOrWhiteSpace(token) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            var prefix = hubId + "|";
            return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private bool TryMarkHubCharacterAnnounced(string targetPeer, string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(targetPeer) || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (!_hubAnnouncedByPeer.TryGetValue(targetPeer, out announced) || announced == null)
                {
                    announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _hubAnnouncedByPeer[targetPeer] = announced;
                }

                if (announced.Contains(token))
                {
                    return false;
                }

                announced.Add(token);
                return true;
            }
        }

        private bool IsHubCharacterAnnounced(string targetPeer, string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(targetPeer) || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (!_hubAnnouncedByPeer.TryGetValue(targetPeer, out announced) || announced == null)
                {
                    return false;
                }

                return announced.Contains(token);
            }
        }

        private void SendHubStateAddToTarget(string hubId, HubPresenceRegistry.Participant participant, HubPeerTarget target, string mode, string source)
        {
            if (IsNullOrWhiteSpace(hubId)
                || participant == null
                || target == null
                || target.Stream == null
                || IsNullOrWhiteSpace(target.Peer)
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return;
            }

            var hubPlayerCharacter = BuildHubPlayerCharacterFromParticipant(participant);
            if (hubPlayerCharacter == null)
            {
                return;
            }

            var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            LogHubAddPayloadSummary(
                mode,
                hubId,
                target.Peer,
                participant,
                hubPlayerCharacter,
                data,
                source ?? string.Empty);

            var msgNo = ReserveMetaGameplayMsgNos(1);
            var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, 7, data), msgNo);
            SendRawFrame(target.Stream, target.Peer, PrefixLength(core), "sent HubCommunicationObject HubStateChanged add (" + (mode ?? string.Empty) + ")");

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = mode ?? string.Empty,
                source = source ?? string.Empty,
                hubId = hubId,
                senderPeer = participant.Peer ?? string.Empty,
                characterId = participant.CharacterId,
                sentCount = 1,
                targetPeers = new[] { target.Peer },
            });
        }

        private bool EnsureHubCharacterAnnouncedForTarget(string hubId, HubPresenceRegistry.Participant participant, HubPeerTarget target, string source)
        {
            if (IsNullOrWhiteSpace(hubId)
                || participant == null
                || target == null
                || IsNullOrWhiteSpace(target.Peer)
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return false;
            }

            if (IsHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
            {
                return true;
            }

            if (!TryMarkHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
            {
                return IsHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId);
            }

            SendHubStateAddToTarget(hubId, participant, target, "ensure-announced", source ?? string.Empty);
            return true;
        }

        private void ClearHubAnnouncementsForPeer(string peer)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_hubAnnouncedByPeerLock)
            {
                _hubAnnouncedByPeer.Remove(peer);
            }

            lock (_hubReadyByPeerLock)
            {
                _hubReadyByPeer.Remove(peer);
            }

        }

        private void ClearHubAnnouncementsForPeerHub(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return;
            }

            lock (_hubAnnouncedByPeerLock)
            {
                HashSet<string> announced;
                if (_hubAnnouncedByPeer.TryGetValue(peer, out announced) && announced != null)
                {
                    announced.RemoveWhere(token => IsHubAnnouncementTokenForHub(token, hubId));
                    if (announced.Count == 0)
                    {
                        _hubAnnouncedByPeer.Remove(peer);
                    }
                }
            }


            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (_hubReadyByPeer.TryGetValue(peer, out readyHubs) && readyHubs != null)
                {
                    readyHubs.Remove(hubId);
                    if (readyHubs.Count == 0)
                    {
                        _hubReadyByPeer.Remove(peer);
                    }
                }
            }

        }

        private void ClearHubAnnouncementForAllPeers(string hubId, string characterId)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            var token = BuildHubAnnouncementToken(hubId, characterId);
            lock (_hubAnnouncedByPeerLock)
            {
                foreach (var pair in _hubAnnouncedByPeer)
                {
                    if (pair.Value != null)
                    {
                        pair.Value.Remove(token);
                    }
                }
            }

        }

        private static string ResolveHubCharacterIdentifier(CareerSlot slot, Guid identityGuid, int careerIndex, string existingCharacterId)
        {
            if (!IsNullOrWhiteSpace(existingCharacterId))
            {
                return existingCharacterId;
            }

            if (slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier))
            {
                return slot.CharacterIdentifier;
            }

            return identityGuid != Guid.Empty
                ? (identityGuid.ToString() + ":" + careerIndex.ToString(CultureInfo.InvariantCulture))
                : string.Empty;
        }

        private bool TryMarkHubPeerReady(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (!_hubReadyByPeer.TryGetValue(peer, out readyHubs) || readyHubs == null)
                {
                    readyHubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _hubReadyByPeer[peer] = readyHubs;
                }

                if (readyHubs.Contains(hubId))
                {
                    return false;
                }

                readyHubs.Add(hubId);
                return true;
            }
        }

        private bool IsHubPeerReady(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            lock (_hubReadyByPeerLock)
            {
                HashSet<string> readyHubs;
                if (!_hubReadyByPeer.TryGetValue(peer, out readyHubs) || readyHubs == null)
                {
                    return false;
                }

                return readyHubs.Contains(hubId);
            }
        }

        private IList<HubPeerTarget> GetHubBroadcastTargets(string hubId, string senderPeer)
        {
            var targets = new List<HubPeerTarget>();
            if (IsNullOrWhiteSpace(hubId) || _portedHubInstanceManager == null)
            {
                return targets;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return targets;
            }

            lock (_hubPeerStreamsLock)
            {
                foreach (var kvp in portedHubInstance.HubState.PlayerCharacters)
                {
                    if (IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    string participantPeer;
                    if (!_hubPresenceRegistry.TryGetPeerForCharacter(kvp.Key, out participantPeer) || IsNullOrWhiteSpace(participantPeer))
                    {
                        continue;
                    }

                    if (!IsNullOrWhiteSpace(senderPeer) && string.Equals(participantPeer, senderPeer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    NetworkStream stream;
                    if (!_hubPeerStreams.TryGetValue(participantPeer, out stream) || stream == null)
                    {
                        continue;
                    }

                    targets.Add(new HubPeerTarget(participantPeer, stream));
                }
            }

            return targets;
        }

        private static string ComputePayloadSha1(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                using (var sha1 = new SHA1Managed())
                {
                    var hash = sha1.ComputeHash(payload);
                    var sb = new StringBuilder(hash.Length * 2);
                    for (var i = 0; i < hash.Length; i++)
                    {
                        sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                    }

                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetInventoryItemId(Item item)
        {
            if (Item.IsNullOrEmpty(item) || IsNullOrWhiteSpace(item.ItemId))
            {
                return string.Empty;
            }

            return item.ItemId;
        }

        private static object[] BuildEquippedItemsDiagnostics(PlayerCharacterInventory inventory)
        {
            if (inventory == null || inventory.EquippedItems == null || inventory.EquippedItems.Count == 0)
            {
                return new object[0];
            }

            var diagnostics = new List<object>(inventory.EquippedItems.Count);
            for (var i = 0; i < inventory.EquippedItems.Count; i++)
            {
                var equipped = inventory.EquippedItems[i];
                if (equipped == null)
                {
                    continue;
                }

                var item = equipped.Item;
                diagnostics.Add(new
                {
                    slotId = equipped.Definition != null ? equipped.Definition.Id : 0UL,
                    itemId = GetInventoryItemId(item),
                    inventoryKey = item != null ? item.InventoryKey : 0,
                    amount = item != null ? item.Amount : 0,
                    flavourIndex = item != null ? item.FlavourIndex : 0,
                    quality = item != null ? item.Quality : 0,
                });
            }

            return diagnostics.ToArray();
        }

        private void LogHubAddPayloadSummary(
            string source,
            string hubId,
            string targetPeer,
            HubPresenceRegistry.Participant participant,
            HubPlayerCharacter hubPlayerCharacter,
            byte[] payload,
            string replaySource)
        {
            if (participant == null || IsNullOrWhiteSpace(targetPeer))
            {
                return;
            }

            var snapshot = hubPlayerCharacter != null ? hubPlayerCharacter.Snapshot : null;
            var inventory = snapshot != null ? snapshot.PlayerCharacterInventory : null;
            HubPresenceRegistry.Participant targetParticipant;
            _hubPresenceRegistry.TryGetParticipantForPeer(targetPeer, out targetParticipant);

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-payload-summary",
                source = source ?? string.Empty,
                hubId = hubId ?? string.Empty,
                targetPeer = targetPeer,
                sourcePeer = participant.Peer ?? string.Empty,
                accountId = participant.AccountId != Guid.Empty ? participant.AccountId.ToString() : string.Empty,
                identityHash = participant.IdentityHash ?? string.Empty,
                targetAccountId = (targetParticipant != null && targetParticipant.AccountId != Guid.Empty)
                    ? targetParticipant.AccountId.ToString()
                    : string.Empty,
                targetIdentityHash = targetParticipant != null ? (targetParticipant.IdentityHash ?? string.Empty) : string.Empty,
                targetCharacterId = targetParticipant != null ? (targetParticipant.CharacterId ?? string.Empty) : string.Empty,
                targetHubId = targetParticipant != null ? (targetParticipant.HubId ?? string.Empty) : string.Empty,
                participantCharacterId = participant.CharacterId ?? string.Empty,
                participantCharacterName = participant.CharacterName ?? string.Empty,
                snapshotCharacterId = snapshot != null ? (snapshot.CharacterIdentifier ?? string.Empty) : string.Empty,
                snapshotCharacterName = snapshot != null ? (snapshot.CharacterName ?? string.Empty) : string.Empty,
                playerId = snapshot != null ? snapshot.PlayerId : 0UL,
                dataVersion = snapshot != null ? snapshot.DataVersion : 0,
                snapshotBodytype = snapshot != null ? snapshot.Bodytype : 0UL,
                snapshotSkinTextureIndex = snapshot != null ? snapshot.SkinTextureIndex : 0,
                snapshotBackgroundStory = snapshot != null ? snapshot.BackgroundStory : 0UL,
                snapshotVoiceSet = snapshot != null ? (snapshot.Voiceset ?? string.Empty) : string.Empty,
                snapshotPortraitPath = snapshot != null ? (snapshot.PortraitPath ?? string.Empty) : string.Empty,
                snapshotPrimaryWeaponItemId = inventory != null ? GetInventoryItemId(inventory.PrimaryWeapon) : string.Empty,
                snapshotSecondaryWeaponItemId = inventory != null ? GetInventoryItemId(inventory.SecondaryWeapon) : string.Empty,
                snapshotArmorItemId = inventory != null ? GetInventoryItemId(inventory.Armor) : string.Empty,
                snapshotEquippedItems = BuildEquippedItemsDiagnostics(inventory),
                payloadBytes = payload != null ? payload.Length : 0,
                payloadSha1 = ComputePayloadSha1(payload),
                replaySource = replaySource ?? string.Empty,
            });
        }

        private void ReplayHubRosterToPeer(string hubId, string targetPeer, string source)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(targetPeer))
            {
                return;
            }

            NetworkStream targetStream;
            lock (_hubPeerStreamsLock)
            {
                if (!_hubPeerStreams.TryGetValue(targetPeer, out targetStream) || targetStream == null)
                {
                    return;
                }
            }

            HubPresenceRegistry.Participant targetParticipant;
            _hubPresenceRegistry.TryGetParticipantForPeer(targetPeer, out targetParticipant);

            if (_portedHubInstanceManager == null)
            {
                return;
            }

            var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
            if (portedHubInstance == null || portedHubInstance.HubState == null || portedHubInstance.HubState.PlayerCharacters == null)
            {
                return;
            }

            var updates = new List<byte[]>();
            var sentPlayerIds = new List<ulong>();
            var candidateCount = 0;
            var sentCharacterIds = new List<string>();
            foreach (var kvp in portedHubInstance.HubState.PlayerCharacters)
            {
                var characterId = kvp.Key;
                var hubPlayerCharacter = kvp.Value;
                if (IsNullOrWhiteSpace(characterId)
                    || hubPlayerCharacter == null
                    || (targetParticipant != null && string.Equals(targetParticipant.CharacterId, characterId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                candidateCount++;

                if (IsHubCharacterAnnounced(targetPeer, hubId, characterId))
                {
                    continue;
                }

                if (!TryMarkHubCharacterAnnounced(targetPeer, hubId, characterId))
                {
                    continue;
                }

                var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
                var payload = BuildUtf16StringPayload(HubSerializer.SerializeHubStateUpdate(update));
                updates.Add(payload);
                sentCharacterIds.Add(characterId);
                sentPlayerIds.Add(hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL);

                HubPresenceRegistry.Participant participant;
                if (!TryGetHubParticipantByCharacterId(characterId, out participant) || participant == null)
                {
                    participant = CreateSyntheticParticipant(hubId, characterId, hubPlayerCharacter);
                }

                LogHubAddPayloadSummary(
                    "roster-replay",
                    hubId,
                    targetPeer,
                    participant,
                    hubPlayerCharacter,
                    payload,
                    source);
            }

            if (updates.Count == 0)
            {
                return;
            }

            var firstMsgNo = ReserveMetaGameplayMsgNos(updates.Count);
            for (var i = 0; i < updates.Count; i++)
            {
                try
                {
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, 7, updates[i]), firstMsgNo + (ulong)i);
                    SendRawFrame(targetStream, targetPeer, PrefixLength(core), "sent HubCommunicationObject HubStateChanged add (roster replay)");
                }
                catch
                {
                }
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-roster-replay",
                targetPeer = targetPeer,
                hubId = hubId,
                candidates = candidateCount,
                sent = updates.Count,
                sentCharacterIds = sentCharacterIds,
                sentPlayerIds = sentPlayerIds,
                source = source ?? string.Empty,
                authoritative = true,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = "roster-replay",
                targetPeer = targetPeer,
                hubId = hubId,
                source = source ?? string.Empty,
                sentCount = updates.Count,
                sentCharacterIds = sentCharacterIds,
                authoritative = true,
            });
        }

        private bool TryActivateHubReadiness(string peer, string hubId, string reason)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return false;
            }

            HubPresenceRegistry.Participant participant;
            if (!_hubPresenceRegistry.TryGetParticipantForPeer(peer, out participant)
                || participant == null
                || IsNullOrWhiteSpace(participant.CharacterId))
            {
                return false;
            }

            var participantHubId = ResolveParticipantHubId(participant, null);
            if (IsNullOrWhiteSpace(participantHubId)
                || !string.Equals(participantHubId, hubId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!TryMarkHubPeerReady(peer, hubId))
            {
                return false;
            }

            ReplayHubRosterToPeer(hubId, peer, reason ?? string.Empty);
            BroadcastHubStateAddToReadyPeers(hubId, peer, participant, reason ?? string.Empty);

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-first-move-sync",
                peer = peer,
                hubId = hubId,
                characterId = participant.CharacterId,
                reason = reason ?? string.Empty,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-ready",
                peer = peer,
                hubId = hubId,
                characterId = participant.CharacterId,
                reason = reason ?? string.Empty,
            });

            return true;
        }

        private void BroadcastHubMovement(string hubId, string senderPeer, string characterId, float x, float y)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            HubPresenceRegistry.Participant senderParticipant;
            var canEnsureAnnouncement = _hubPresenceRegistry.TryGetParticipantForPeer(senderPeer, out senderParticipant)
                && senderParticipant != null
                && !IsNullOrWhiteSpace(senderParticipant.CharacterId)
                && string.Equals(senderParticipant.CharacterId, characterId, StringComparison.OrdinalIgnoreCase);

            var filteredTargets = new List<HubPeerTarget>(targets.Count);
            var ensuredTargets = new List<string>();
            var blockedTargets = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                if (IsHubCharacterAnnounced(target.Peer, hubId, characterId))
                {
                    filteredTargets.Add(target);
                }
                else if (canEnsureAnnouncement
                    && IsHubPeerReady(target.Peer, hubId)
                    && EnsureHubCharacterAnnouncedForTarget(hubId, senderParticipant, target, "move-send"))
                {
                    filteredTargets.Add(target);
                    ensuredTargets.Add(target.Peer);
                }
                else
                {
                    blockedTargets.Add(target.Peer);
                }
            }

            if (filteredTargets.Count == 0)
            {
                return;
            }

            var targetPeers = new List<string>(filteredTargets.Count);
            for (var i = 0; i < filteredTargets.Count; i++)
            {
                if (filteredTargets[i] != null && !IsNullOrWhiteSpace(filteredTargets[i].Peer))
                {
                    targetPeers.Add(filteredTargets[i].Peer);
                }
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-move-broadcast",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = characterId,
                x = x,
                y = y,
                targets = targetPeers,
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-move-send",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = characterId,
                totalTargets = targets.Count,
                eligibleTargets = filteredTargets.Count,
                ensuredTargets = ensuredTargets,
                blockedTargets = blockedTargets,
            });

            var data = BuildHubMovementPayload(hubId, characterId, x, y);
            if (data == null)
            {
                return;
            }

            BroadcastHubFieldEvent(filteredTargets, 4, data, "sent HubCommunicationObject ExecuteMoveToPosition (broadcast)");
        }

        private void BroadcastHubStateAddToReadyPeers(string hubId, string senderPeer, HubPresenceRegistry.Participant participant, string source)
        {
            if (IsNullOrWhiteSpace(hubId) || participant == null)
            {
                return;
            }

            var hubPlayerCharacter = BuildHubPlayerCharacterFromParticipant(participant);
            if (hubPlayerCharacter == null)
            {
                return;
            }

            var update = HubStateUpdate.CreateForCharacterAddtion(hubPlayerCharacter, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            var filteredTargets = new List<HubPeerTarget>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                if (!IsHubPeerReady(target.Peer, hubId))
                {
                    continue;
                }

                if (TryMarkHubCharacterAnnounced(target.Peer, hubId, participant.CharacterId))
                {
                    filteredTargets.Add(target);
                }
            }

            if (filteredTargets.Count == 0)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-add-broadcast",
                    hubId = hubId,
                    senderPeer = senderPeer ?? string.Empty,
                    characterId = participant.CharacterId,
                    playerId = (hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL),
                    targets = targets.Count,
                    sentTargets = 0,
                });
                return;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-broadcast",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = participant.CharacterId,
                playerId = (hubPlayerCharacter.Snapshot != null ? hubPlayerCharacter.Snapshot.PlayerId : 0UL),
                targets = targets.Count,
                sentTargets = filteredTargets.Count,
                targetPeers = filteredTargets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-add-send",
                mode = "broadcast",
                source = source ?? string.Empty,
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                characterId = participant.CharacterId,
                sentCount = filteredTargets.Count,
                targetPeers = filteredTargets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            for (var i = 0; i < filteredTargets.Count; i++)
            {
                var target = filteredTargets[i];
                if (target == null || IsNullOrWhiteSpace(target.Peer))
                {
                    continue;
                }

                LogHubAddPayloadSummary(
                    "broadcast",
                    hubId,
                    target.Peer,
                    participant,
                    hubPlayerCharacter,
                    data,
                    source ?? string.Empty);
            }

            BroadcastHubFieldEvent(filteredTargets, 7, data, "sent HubCommunicationObject HubStateChanged add (broadcast)");
        }

        private void BroadcastHubStateRemove(string hubId, string senderPeer, string removedCharacterId)
        {
            if (IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(removedCharacterId))
            {
                return;
            }

            ClearHubAnnouncementForAllPeers(hubId, removedCharacterId);

            var update = HubStateUpdate.CreateForRemoveCharacter(removedCharacterId, hubId);
            var serializedUpdate = HubSerializer.SerializeHubStateUpdate(update);
            var data = BuildUtf16StringPayload(serializedUpdate);

            var targets = GetHubBroadcastTargets(hubId, senderPeer);
            if (targets.Count == 0)
            {
                return;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-remove-send",
                hubId = hubId,
                senderPeer = senderPeer ?? string.Empty,
                removedCharacterId = removedCharacterId,
                sentCount = targets.Count,
                targetPeers = targets.Select(t => t != null ? t.Peer : string.Empty).ToList(),
            });

            BroadcastHubFieldEvent(targets, 7, data, "sent HubCommunicationObject HubStateChanged remove (broadcast)");
        }

        private void BroadcastHubFieldEvent(IList<HubPeerTarget> targets, ushort fieldId, byte[] data, string note)
        {
            if (targets == null || targets.Count == 0 || data == null)
            {
                return;
            }

            var firstMsgNo = ReserveMetaGameplayMsgNos(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (target == null || target.Stream == null)
                {
                    continue;
                }

                try
                {
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, HubEntityId, fieldId, data), firstMsgNo + (ulong)i);
                    SendRawFrame(target.Stream, target.Peer, PrefixLength(core), note);
                }
                catch
                {
                }
            }
        }

        private HubStateUpdate RemoveParticipantFromAuthoritativeHub(HubPresenceRegistry.Participant participant, PortedHubInstance fallbackHubInstance, out string removedHubId)
        {
            removedHubId = null;
            if (participant == null || IsNullOrWhiteSpace(participant.CharacterId) || _portedHubInstanceManager == null)
            {
                removedHubId = ResolveParticipantHubId(participant, null);
                return null;
            }

            PortedHubInstance hubInstance = null;
            if (!IsNullOrWhiteSpace(participant.CharacterId))
            {
                hubInstance = _portedHubInstanceManager.RequestHubInstance(participant.CharacterId);
            }

            if (hubInstance == null && !IsNullOrWhiteSpace(participant.HubId))
            {
                hubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(participant.HubId);
            }

            if (hubInstance == null)
            {
                hubInstance = fallbackHubInstance;
            }

            if (hubInstance == null)
            {
                removedHubId = ResolveParticipantHubId(participant, null);
                return null;
            }

            removedHubId = hubInstance.HubId;
            return _portedHubInstanceManager.RemoveCharacterFromHub(hubInstance, participant.CharacterId);
        }

        private void RemoveHubPresenceWithBroadcast(string peer, PortedHubInstance fallbackHubInstance, string reason)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            HubPresenceRegistry.Participant existing;
            var hasExisting = _hubPresenceRegistry.TryGetParticipantForPeer(peer, out existing) && existing != null;
            HubStateUpdate leaveUpdate = null;
            string removedHubId = null;
            if (hasExisting)
            {
                leaveUpdate = RemoveParticipantFromAuthoritativeHub(existing, fallbackHubInstance, out removedHubId);
            }

            ClearHubAnnouncementsForPeer(peer);

            if (leaveUpdate != null && !IsNullOrWhiteSpace(leaveUpdate.RemovedCharacter))
            {
                BroadcastHubStateRemove(leaveUpdate.InstanceId, peer, leaveUpdate.RemovedCharacter);
            }
            else if (hasExisting && !IsNullOrWhiteSpace(removedHubId) && !IsNullOrWhiteSpace(existing.CharacterId))
            {
                BroadcastHubStateRemove(removedHubId, peer, existing.CharacterId);
            }

            if (hasExisting)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-retire",
                    reason = reason ?? string.Empty,
                    peer = peer,
                    characterId = existing.CharacterId ?? string.Empty,
                    hubId = removedHubId ?? string.Empty,
                    hadAuthoritativeLeaveUpdate = leaveUpdate != null,
                });
            }

            _hubPresenceRegistry.RemovePeer(peer);
        }

        private void RemoveHubPresenceWithBroadcast(string peer)
        {
            RemoveHubPresenceWithBroadcast(peer, null, string.Empty);
        }

        private void RegisterOrUpdateHubPresenceWithDuplicateRetire(
            string peer,
            Guid accountId,
            string identityHash,
            int careerIndex,
            string characterId,
            string characterName,
            string hubId,
            float x,
            float y,
            string reason)
        {
            if (!IsNullOrWhiteSpace(characterId))
            {
                RetireDuplicateHubSessionForCharacter(peer, characterId, reason ?? string.Empty);
            }

            _hubPresenceRegistry.RegisterOrUpdate(
                peer,
                accountId,
                identityHash,
                careerIndex,
                characterId,
                characterName,
                hubId,
                x,
                y);
        }

        private bool IsHubMoveOwnershipValid(
            string peer,
            Guid activeIdentityGuid,
            string movedCharacterId,
            HubPresenceRegistry.Participant movementParticipant)
        {
            if (IsNullOrWhiteSpace(movedCharacterId))
            {
                return true;
            }

            if (activeIdentityGuid == Guid.Empty)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = "unauthenticated-identity",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                });
                return false;
            }

            var ownerAccountId = TryParseAccountIdFromCharacterIdentifier(movedCharacterId);
            if (ownerAccountId == Guid.Empty || ownerAccountId != activeIdentityGuid)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = ownerAccountId == Guid.Empty ? "invalid-character-id" : "account-mismatch",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                    activeIdentityGuid = activeIdentityGuid.ToString(),
                    parsedOwnerAccountId = ownerAccountId != Guid.Empty ? ownerAccountId.ToString() : string.Empty,
                });
                return false;
            }

            if (movementParticipant != null
                && !IsNullOrWhiteSpace(movementParticipant.CharacterId)
                && !string.Equals(movementParticipant.CharacterId, movedCharacterId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-move-rejected-owner-mismatch",
                    reason = "registered-character-mismatch",
                    peer = peer ?? string.Empty,
                    characterId = movedCharacterId,
                    registeredCharacterId = movementParticipant.CharacterId,
                });
                return false;
            }

            return true;
        }

        private sealed class HubPeerTarget
        {
            public readonly string Peer;
            public readonly NetworkStream Stream;

            public HubPeerTarget(string peer, NetworkStream stream)
            {
                Peer = peer;
                Stream = stream;
            }
        }
    }
}
