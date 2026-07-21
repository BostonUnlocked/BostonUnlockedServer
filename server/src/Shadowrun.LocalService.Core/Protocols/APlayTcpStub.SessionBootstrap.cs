using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private string ResolveIdentityForSessionHash(string requestedSessionHash, out Guid mappedIdentityGuid, out string rejectReason)
        {
            mappedIdentityGuid = Guid.Empty;
            rejectReason = null;

            if (IsNullOrWhiteSpace(requestedSessionHash))
            {
                rejectReason = "Missing session hash.";
                return null;
            }

            try
            {
                var _ = new Guid(requestedSessionHash);

                string mappedIdentityHash;
                if (_sessionIdentityMap != null && _sessionIdentityMap.TryGetIdentityForSession(requestedSessionHash, out mappedIdentityHash) && !IsNullOrWhiteSpace(mappedIdentityHash))
                {
                }
                else if (_userStore != null && _userStore.TryGetIdentityForSession(requestedSessionHash, out mappedIdentityHash) && !IsNullOrWhiteSpace(mappedIdentityHash))
                {
                }
                else
                {
                    rejectReason = "Unknown session hash (no mapped identity).";
                    return null;
                }

                try
                {
                    mappedIdentityGuid = new Guid(mappedIdentityHash);
                    return mappedIdentityHash;
                }
                catch
                {
                    rejectReason = "Mapped identity hash is invalid.";
                    return null;
                }
            }
            catch
            {
                rejectReason = "Invalid session hash.";
                return null;
            }
        }

        private void EnsureAccountEntityIntroduced(NetworkStream stream, string peer, ulong serverMsgNoBase, ref bool sentAccountIntro)
        {
            if (sentAccountIntro)
            {
                return;
            }

            var accountIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes(AccountEntityId), BitConverter.GetBytes((ushort)3), BitConverter.GetBytes(0));
            var accountIntroCore = BuildCoreDirectSystem(1, accountIntroRaw, serverMsgNoBase + 1);
            SendRawFrame(stream, peer, PrefixLength(accountIntroCore), "sent AP introduce shared entity (type=3 account communication object, id=" + AccountEntityId + ")");
            sentAccountIntro = true;
        }

        private void EnsureMetaGameplayAndHubEntitiesIntroduced(NetworkStream stream, string peer, ulong serverMsgNoBase, ref bool sentMetaGameplayIntro, ref bool sentHubIntro)
        {
            if (!sentMetaGameplayIntro)
            {
                var metaGameplayIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)3), BitConverter.GetBytes((ushort)8), BitConverter.GetBytes(0));
                var metaGameplayIntroCore = BuildCoreDirectSystem(1, metaGameplayIntroRaw, serverMsgNoBase);
                SendRawFrame(stream, peer, PrefixLength(metaGameplayIntroCore), "sent AP introduce shared entity (type=8 meta gameplay communication object, id=3)");

                var metaGameplayOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(3, 8), serverMsgNoBase + 1);
                SendRawFrame(stream, peer, PrefixLength(metaGameplayOwnerCore), "sent AP shared-entity set-owner (entity=3)");

                sentMetaGameplayIntro = true;
            }

            if (!sentHubIntro)
            {
                var hubIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes((ulong)4), BitConverter.GetBytes((ushort)11), BitConverter.GetBytes(0));
                var hubIntroCore = BuildCoreDirectSystem(1, hubIntroRaw, serverMsgNoBase + 2);
                SendRawFrame(stream, peer, PrefixLength(hubIntroCore), "sent AP introduce shared entity (type=11 hub communication object, id=4)");

                var hubOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(4, 11), serverMsgNoBase + 3);
                SendRawFrame(stream, peer, PrefixLength(hubOwnerCore), "sent AP shared-entity set-owner (entity=4)");

                sentHubIntro = true;
            }
        }

        private void StartKeepAliveLoop(NetworkStream stream, string peer, ManualResetEvent stopEvent, ManualResetEvent connectionClosed, ulong gameClientEntityId, ref bool keepAliveLoopStarted, long initialKeepAliveMsgNo)
        {
            if (keepAliveLoopStarted)
            {
                return;
            }

            keepAliveLoopStarted = true;
            long keepAliveMsgNo = initialKeepAliveMsgNo;
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (!stopEvent.WaitOne(0) && !connectionClosed.WaitOne(0))
                {
                    SleepWithStop(stopEvent, 2000);
                    if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                    {
                        break;
                    }

                    try
                    {
                        var msgNo = unchecked((ulong)Interlocked.Increment(ref keepAliveMsgNo));
                        var periodicKeepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 6, new byte[0]), msgNo);
                        SendRawFrame(stream, peer, PrefixLength(periodicKeepAliveCore), "sent GameClientConnection KeepAlive (periodic)");
                    }
                    catch
                    {
                        connectionClosed.Set();
                        break;
                    }
                }
            });
        }

        private bool HandleRegularConnect(
            NetworkStream stream,
            string peer,
            CoreDirectSystem direct,
            ApSharedFieldEvent shared,
            List<string> payloadStrings,
            ManualResetEvent stopEvent,
            ManualResetEvent connectionClosed,
            ref bool sentRegularConnectReply,
            ref bool sentAccountIntro,
            ref bool keepAliveLoopStarted,
            long keepAliveMsgNo,
            ref ulong gameClientEntityId,
            ref string activeIdentityHash,
            ref Guid activeIdentityGuid,
            ref Guid aplayTransportAccountId)
        {
            var serverMsgNoBase = direct.MsgNo;

            if (shared.EntityId != 0UL && shared.EntityId != gameClientEntityId)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-gameclient-entityid-adopted",
                    peer = peer,
                    previousEntityId = gameClientEntityId,
                    adoptedEntityId = shared.EntityId,
                });

                gameClientEntityId = shared.EntityId;
            }

            var requestedSessionHash = payloadStrings.Count > 0 ? payloadStrings[0] : null;
            Guid mappedIdentityGuid;
            string rejectReason;
            var mappedIdentityHash = ResolveIdentityForSessionHash(requestedSessionHash, out mappedIdentityGuid, out rejectReason);

            EnsureAccountEntityIntroduced(stream, peer, serverMsgNoBase, ref sentAccountIntro);

            var gameClientOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(gameClientEntityId, GameClientConnectionTypeId), serverMsgNoBase + 2);
            SendRawFrame(stream, peer, PrefixLength(gameClientOwnerCore), "sent AP shared-entity set-owner (entity=" + gameClientEntityId + ")");

            var accountOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(AccountEntityId, 3), serverMsgNoBase + 3);
            SendRawFrame(stream, peer, PrefixLength(accountOwnerCore), "sent AP shared-entity set-owner (entity=" + AccountEntityId + ")");

            if (rejectReason != null)
            {
                var rejectPayload = BuildUtf16StringPayload(rejectReason);
                var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 5, rejectPayload), serverMsgNoBase + 4);
                SendRawFrame(stream, peer, PrefixLength(rejectCore), "sent GameClientConnection RejectLogin in response to RegularConnect");
                connectionClosed.Set();
                return true;
            }

            activeIdentityHash = mappedIdentityHash;
            activeIdentityGuid = mappedIdentityGuid;

            if (aplayTransportAccountId != activeIdentityGuid)
            {
                if (aplayTransportAccountId != Guid.Empty)
                {
                    AccountTransportLivenessRegistry.MarkDisconnected(aplayTransportAccountId, AccountTransportLivenessRegistry.TransportAPlay);
                }

                AccountTransportLivenessRegistry.MarkConnected(activeIdentityGuid, AccountTransportLivenessRegistry.TransportAPlay);
                aplayTransportAccountId = activeIdentityGuid;

                try
                {
                    var snapshot = AccountTransportLivenessRegistry.Evaluate(activeIdentityGuid);
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "transport-connected",
                        protocol = "aplay",
                        accountId = activeIdentityGuid,
                        peer = peer,
                        connectionHash = (string)null,
                        photonConnections = snapshot.PhotonConnections,
                        aplayConnections = snapshot.APlayConnections,
                    });
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "account-liveness-evaluated",
                        protocol = "aplay",
                        accountId = activeIdentityGuid,
                        isSocialOnline = snapshot.IsSocialOnline,
                        isHardOffline = snapshot.IsHardOffline,
                        photonConnections = snapshot.PhotonConnections,
                        aplayConnections = snapshot.APlayConnections,
                        reason = "regular-connect",
                    });
                }
                catch
                {
                }
            }

            _logger.UpdateConnectionAccountId("aplay", peer, null, activeIdentityGuid);
            RegisterGameClientEntityIdForIdentity(activeIdentityGuid, gameClientEntityId, peer);

            var careerSummary = BuildCareerSummaryJsonForIdentity(activeIdentityHash);
            var welcomePayload = BuildGameClientWelcomePayload(AccountEntityId, careerSummary);
            var welcomeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 4, welcomePayload), serverMsgNoBase + 4);
            SendRawFrame(stream, peer, PrefixLength(welcomeCore), "sent GameClientConnection Welcome in response to RegularConnect");
            sentRegularConnectReply = true;

            var keepAliveCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 6, new byte[0]), serverMsgNoBase + 5);
            SendRawFrame(stream, peer, PrefixLength(keepAliveCore), "sent GameClientConnection KeepAlive after Welcome");

            StartKeepAliveLoop(stream, peer, stopEvent, connectionClosed, gameClientEntityId, ref keepAliveLoopStarted, keepAliveMsgNo);
            return false;
        }

        private bool HandleCareerBootstrapRequest(
            NetworkStream stream,
            string peer,
            CoreDirectSystem direct,
            ApSharedFieldEvent shared,
            ulong gameClientEntityId,
            string activeIdentityHash,
            Guid activeIdentityGuid,
            ref int activeCareerIndex,
            ref string activeCharacterName,
            ref int pendingCreationCareerIndex,
            ref string currentHubInstanceId,
            ref PortedHubInstance currentHubInstance,
            ref byte[] cachedHubStatePayload,
            ref byte[] cachedCreationInfoPayload,
            HashSet<string> completedStoryMissions,
            ManualResetEvent stopEvent,
            ManualResetEvent connectionClosed,
            Action<string> armCreationInfoTracking,
            Action<string, string, string> armHubReadyFallback,
            ref bool sentMetaGameplayIntro,
            ref bool sentHubIntro,
            ref bool sentEnterCareerUpdate)
        {
            int? requestedIndex = ParseInt32Payload(shared.Data);
            var careerIndex = requestedIndex.HasValue ? requestedIndex.Value : 0;
            if (careerIndex < 0)
            {
                careerIndex = 0;
            }

            var isCreateCareer = shared.FieldId == 10;
            var serverMsgNoBase = direct.MsgNo + 9;
            EnsureMetaGameplayAndHubEntitiesIntroduced(stream, peer, serverMsgNoBase, ref sentMetaGameplayIntro, ref sentHubIntro);

            if (IsNullOrWhiteSpace(activeIdentityHash) || activeIdentityGuid == Guid.Empty)
            {
                var rejectPayload = BuildUtf16StringPayload("Not logged in.");
                var rejectCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, gameClientEntityId, 5, rejectPayload), serverMsgNoBase + 4);
                SendRawFrame(stream, peer, PrefixLength(rejectCore), "sent GameClientConnection RejectLogin (EnterCareer before login)");
                connectionClosed.Set();
                return true;
            }

            var identityHash = activeIdentityHash;
            var identityGuid = activeIdentityGuid;

            CareerSlot slot = null;
            if (_userStore != null)
            {
                slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, isCreateCareer);

                if (!slot.IsOccupied && !isCreateCareer)
                {
                    slot.IsOccupied = true;
                    if (IsNullOrWhiteSpace(slot.CharacterName))
                    {
                        slot.CharacterName = "OfflineRunner";
                    }
                }

                if (!isCreateCareer && slot.PendingPersistenceCreation)
                {
                    slot.PendingPersistenceCreation = false;
                }
                else if (isCreateCareer && slot.PendingPersistenceCreation)
                {
                    pendingCreationCareerIndex = careerIndex;
                }

                if (_storyProgressionService != null)
                {
                    try
                    {
                        _storyProgressionService.NormalizeRepeatableMissionStates(slot);
                    }
                    catch
                    {
                    }
                }

                _userStore.UpsertCareer(identityHash, slot);
                _userStore.SetLastCareerIndex(identityHash, careerIndex);
            }

            var characterName = slot != null && !IsNullOrWhiteSpace(slot.CharacterName)
                ? slot.CharacterName
                : (isCreateCareer ? "NewRunner" : "OfflineRunner");

            activeCareerIndex = careerIndex;
            activeCharacterName = characterName;

            completedStoryMissions.Clear();
            if (slot != null && slot.MainCampaignMissionStates != null)
            {
                foreach (var kvp in slot.MainCampaignMissionStates)
                {
                    if (IsNullOrWhiteSpace(kvp.Key) || IsNullOrWhiteSpace(kvp.Value))
                    {
                        continue;
                    }
                    if (!IsRepeatableMission(kvp.Key) && string.Equals(kvp.Value, "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        completedStoryMissions.Add(kvp.Key);
                    }
                }
            }

            var pendingCreation = slot != null ? slot.PendingPersistenceCreation : isCreateCareer;
            var zippedCareerInfo = slot != null
                ? _careerInfoGenerator.GetZippedCareerInfo(identityGuid, careerIndex, slot)
                : _careerInfoGenerator.GetZippedCareerInfo(identityGuid, careerIndex, characterName, pendingCreation);

            var accountWelcomePayload = BuildAccountWelcomePayload(careerIndex, zippedCareerInfo, 3);
            var accountWelcomeCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)AccountEntityId, AccountFieldAccountWelcome, accountWelcomePayload), serverMsgNoBase + 4);
            SendRawFrame(stream, peer, PrefixLength(accountWelcomeCore), "sent AccountCommunicationObject Welcome after EnterCareer");

            var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJsonForIdentity(identityHash));
            var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)AccountEntityId, AccountFieldUpdateCareerSummaries, updatePayload), serverMsgNoBase + 5);
            SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after EnterCareer");

            var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)MetaGameplayEntityId, MetaGameplayFieldMetaSnapshot, metaSnapshotPayload), serverMsgNoBase + 6);
            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient");

            var henchmanCollectionPayload = BuildUtf16StringPayload(SerializeDefaultHenchmanCollection(identityHash, careerIndex));
            var henchmanCollectionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)MetaGameplayEntityId, MetaGameplayFieldHenchmanCollection, henchmanCollectionPayload), serverMsgNoBase + 7);
            SendRawFrame(stream, peer, PrefixLength(henchmanCollectionCore), "sent MetaGameplayCommunicationObject SendHenchmanCollectionToClient");

            var characterIdentifier = slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                ? slot.CharacterIdentifier
                : (identityGuid.ToString() + ":" + careerIndex.ToString());

            RetireDuplicateHubSessionForCharacter(peer, characterIdentifier, "career-enter-bootstrap-pre-transition");

            cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                slot,
                identityGuid,
                careerIndex,
                false,
                currentHubInstance,
                out currentHubInstanceId,
                out currentHubInstance);

            RegisterOrUpdateHubPresenceWithDuplicateRetire(
                peer,
                activeIdentityGuid,
                activeIdentityHash,
                activeCareerIndex,
                characterIdentifier,
                activeCharacterName,
                currentHubInstanceId,
                0f,
                0f,
                "career-enter-bootstrap");

            var creationInfoJson = "{\"PendingPersistenceCreation\":" + (pendingCreation ? "true" : "false") + ",\"DataVersionChanged\":false}";
            var creationInfoPayload = BuildUtf16StringPayload(creationInfoJson);
            cachedCreationInfoPayload = creationInfoPayload;
            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)MetaGameplayEntityId, MetaGameplayFieldCreationInfoChanged, creationInfoPayload), serverMsgNoBase + 9);
            var sentCreationInfo = false;
            if (!ShouldSuppressDuplicateHubPush(peer, true, creationInfoPayload))
            {
                SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged");
                sentCreationInfo = true;
            }

            if (sentCreationInfo && armCreationInfoTracking != null)
            {
                armCreationInfoTracking("career-enter-bootstrap");
            }

            sentEnterCareerUpdate = true;
            return false;
        }

        private void HandleLeaveCurrentCareerRequest(
            NetworkStream stream,
            string peer,
            CoreDirectSystem direct,
            string activeIdentityHash,
            Action<string> cancelHubReadyFallback,
            ref int activeCareerIndex,
            ref string activeCharacterName,
            ref int pendingCreationCareerIndex,
            ref string currentHubInstanceId,
            ref byte[] cachedCreationInfoPayload,
            ref bool sentEnterCareerUpdate)
        {
            var abortedPendingCreation = AbortPendingCareerCreationIfNeeded(
                peer,
                activeIdentityHash,
                ref activeCareerIndex,
                ref activeCharacterName,
                ref pendingCreationCareerIndex,
                "leave-current-career");

            cancelHubReadyFallback("leave-current-career");
            RemoveHubPresenceWithBroadcast(peer);
            currentHubInstanceId = null;

            var msgNoBase = direct.MsgNo + 20;
            var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJsonForIdentity(activeIdentityHash));
            var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)AccountEntityId, AccountFieldUpdateCareerSummaries, updatePayload), msgNoBase + 1);
            SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after LeaveCurrentCareer");

            if (abortedPendingCreation && cachedCreationInfoPayload != null)
            {
                var creationInfoJson = "{\"PendingPersistenceCreation\":false,\"DataVersionChanged\":false}";
                cachedCreationInfoPayload = BuildUtf16StringPayload(creationInfoJson);
                var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)MetaGameplayEntityId, MetaGameplayFieldCreationInfoChanged, cachedCreationInfoPayload), msgNoBase + 2);
                SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after LeaveCurrentCareer");
            }

            sentEnterCareerUpdate = false;
        }

        private void HandleDeactivateCareerRequest(
            NetworkStream stream,
            string peer,
            CoreDirectSystem direct,
            byte[] data,
            string activeIdentityHash,
            ref int activeCareerIndex,
            ref string activeCharacterName,
            ref int pendingCreationCareerIndex,
            ref byte[] cachedCreationInfoPayload)
        {
            var idx = ParseInt32Payload(data);
            var slotIndex = idx.HasValue ? idx.Value : 0;
            if (slotIndex < 0)
            {
                slotIndex = 0;
            }

            if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
            {
                _userStore.DeactivateCareerSlot(activeIdentityHash, slotIndex, DefaultHubId);
            }

            if (activeCareerIndex == slotIndex)
            {
                activeCareerIndex = 0;
                activeCharacterName = "OfflineRunner";
            }

            if (pendingCreationCareerIndex == slotIndex)
            {
                pendingCreationCareerIndex = -1;
            }

            var msgNoBase = direct.MsgNo + 30;
            var summaryJson = BuildCareerSummaryJsonForIdentity(activeIdentityHash);

            var careerDeactivatedPayload = BuildUtf16StringPayload(summaryJson);
            var careerDeactivatedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)AccountEntityId, AccountFieldCareerDeactivated, careerDeactivatedPayload), msgNoBase + 1);
            SendRawFrame(stream, peer, PrefixLength(careerDeactivatedCore), "sent AccountCommunicationObject CareerDeactivated after DeactivateCareer");

            var updatePayload = BuildUtf16StringPayload(summaryJson);
            var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)AccountEntityId, AccountFieldUpdateCareerSummaries, updatePayload), msgNoBase + 2);
            SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after DeactivateCareer");

            if (cachedCreationInfoPayload != null)
            {
                var creationInfoJson = "{\"PendingPersistenceCreation\":false,\"DataVersionChanged\":false}";
                cachedCreationInfoPayload = BuildUtf16StringPayload(creationInfoJson);
                var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, (int)MetaGameplayEntityId, MetaGameplayFieldCreationInfoChanged, cachedCreationInfoPayload), msgNoBase + 2);
                SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after DeactivateCareer");
            }
        }
    }
}
