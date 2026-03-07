using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core.Simulation;
using Shadowrun.LocalService.Core.Career;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private const string DefaultHubId = "Act01_HUB_02";
        private const string FallbackSerializedHubState = "CwAAAEgAVQBCAF8AcwBjAGUAbgBlAF8AMQALAAAASABVAEIAXwBzAGMAZQBuAGUAXwAxAAA=";
        private static long _hubInstanceSequence;
        private static long _hubDuplicateSessionRetiredTotal;
        private static long _hubReadyFallbackTriggeredTotal;
        private static long _hubReadyFallbackSkippedTotal;
        private static readonly object HenchmanCollectionCacheLock = new object();
        private static string CachedSerializedHenchmanCollection;
        private static DateTime CachedSerializedHenchmanCollectionLastWriteUtc;
        private static int CachedHenchmanCollectionCreationIndex;
        private static List<PlayerCharacterSnapshot> CachedHenchmanCollectionSnapshots;

        private static string BuildProgressionHubInstanceId(string hubName)
        {
            var canonicalHubName = !IsNullOrWhiteSpace(hubName) ? hubName : DefaultHubId;
            var sequence = Interlocked.Increment(ref _hubInstanceSequence);
            return canonicalHubName + "#" + sequence.ToString(CultureInfo.InvariantCulture);
        }

        private PlayerCharacterSnapshot BuildMappedPlayerCharacterSnapshotForHub(Guid identityGuid, string characterIdentifier, string characterName, CareerSlot slot)
        {
            if (IsNullOrWhiteSpace(characterIdentifier))
            {
                return null;
            }

            var snapshot = BuildPlayerCharacterSnapshotForSlot(characterIdentifier, characterName, slot);
            var accountId = identityGuid != Guid.Empty ? identityGuid : TryParseAccountIdFromCharacterIdentifier(characterIdentifier);
            ulong mappedPlayerId;
            if (accountId != Guid.Empty
                && TryGetGameClientEntityIdForIdentity(accountId, out mappedPlayerId)
                && mappedPlayerId != 0UL)
            {
                snapshot.PlayerId = mappedPlayerId;
            }

            return snapshot;
        }

        private PortedHubTransitionResult TryExecutePortedHubTransition(string requestedHubId, Guid identityGuid, string characterIdentifier, string characterName, CareerSlot slot, PortedHubInstance currentHubInstance)
        {
            if (_portedHubInstanceManager == null || IsNullOrWhiteSpace(requestedHubId) || IsNullOrWhiteSpace(characterIdentifier))
            {
                return null;
            }

            var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, characterIdentifier, characterName, slot);
            if (snapshot == null)
            {
                return null;
            }

            var exactTargetHub = _portedHubInstanceManager.RequestHubInstanceByHubId(requestedHubId);
            if (exactTargetHub != null)
            {
                return _portedHubInstanceManager.ExecuteRequestHubInstance(exactTargetHub, snapshot, currentHubInstance);
            }

            return _portedHubInstanceManager.ExecuteRequestHubInstance(GetHubNameFromHubInstanceId(requestedHubId), snapshot, currentHubInstance, new GroupStatus());
        }

        private byte[] BuildPortedHubStatePayloadForSlot(CareerSlot slot, Guid identityGuid, int careerIndex, bool forceNewHubInstanceId, PortedHubInstance currentHubInstance, out string resolvedHubId, out PortedHubInstance resolvedHubInstance)
        {
            resolvedHubId = slot != null && !IsNullOrWhiteSpace(slot.HubId) ? slot.HubId : DefaultHubId;
            resolvedHubInstance = currentHubInstance;

            var characterIdentifier = slot != null && !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                ? slot.CharacterIdentifier
                : (identityGuid.ToString() + ":" + careerIndex.ToString(CultureInfo.InvariantCulture));
            var characterName = slot != null ? slot.CharacterName : null;
            var requestedHubId = forceNewHubInstanceId ? BuildProgressionHubInstanceId(resolvedHubId) : resolvedHubId;

            if (_portedHubInstanceManager != null && !IsNullOrWhiteSpace(characterIdentifier))
            {
                if (resolvedHubInstance == null)
                {
                    resolvedHubInstance = _portedHubInstanceManager.RequestHubInstance(characterIdentifier);
                }

                var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, characterIdentifier, characterName, slot);
                if (snapshot != null)
                {
                    PortedHubTransitionResult transition = forceNewHubInstanceId
                        ? _portedHubInstanceManager.ExecuteRequestExactHubInstance(requestedHubId, snapshot, resolvedHubInstance)
                        : TryExecutePortedHubTransition(requestedHubId, identityGuid, characterIdentifier, characterName, slot, resolvedHubInstance);

                    if (transition != null && transition.TargetHubInstance != null)
                    {
                        resolvedHubInstance = transition.TargetHubInstance;
                        resolvedHubId = resolvedHubInstance.HubId;
                        return BuildMetaHubPushPayload(HubEntityId, resolvedHubInstance.SerializedHubState());
                    }
                }
            }

            resolvedHubId = requestedHubId;
            return BuildMetaHubPushPayload(HubEntityId, FallbackSerializedHubState);
        }

        private string BuildSerializedSharedHubStateOrFallback(string hubId, string fallbackCharacterIdentifier, string fallbackCharacterName, CareerSlot fallbackSlot)
        {
            if (IsNullOrWhiteSpace(hubId))
            {
                return FallbackSerializedHubState;
            }

            if (_portedHubInstanceManager != null)
            {
                var portedHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(hubId);
                if (portedHubInstance == null && !IsNullOrWhiteSpace(fallbackCharacterIdentifier))
                {
                    var identityGuid = TryParseAccountIdFromCharacterIdentifier(fallbackCharacterIdentifier);
                    var snapshot = BuildMappedPlayerCharacterSnapshotForHub(identityGuid, fallbackCharacterIdentifier, fallbackCharacterName, fallbackSlot);
                    if (snapshot != null)
                    {
                        var transition = _portedHubInstanceManager.ExecuteRequestExactHubInstance(hubId, snapshot, null);
                        if (transition != null)
                        {
                            portedHubInstance = transition.TargetHubInstance;
                        }
                    }
                }

                if (portedHubInstance != null)
                {
                    var serializedHubState = portedHubInstance.SerializedHubState();
                    if (!IsNullOrWhiteSpace(serializedHubState))
                    {
                        return serializedHubState;
                    }
                }
            }

            return FallbackSerializedHubState;
        }

        private static string GetHubNameFromHubInstanceId(string hubId)
        {
            if (IsNullOrWhiteSpace(hubId))
            {
                return DefaultHubId;
            }

            var idx = hubId.IndexOf('#');
            if (idx > 0)
            {
                return hubId.Substring(0, idx);
            }

            return hubId;
        }

        private static bool TryParseRequestStoryHubForPayload(byte[] data, out string groupHostCharacterId, out Guid groupHostAccountId)
        {
            groupHostCharacterId = null;
            groupHostAccountId = Guid.Empty;

            if (data == null || data.Length < 8)
            {
                return false;
            }

            var pos = 0;
            string rawHostAccountId;
            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out groupHostCharacterId))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out rawHostAccountId))
            {
                return false;
            }

            try
            {
                if (!IsNullOrWhiteSpace(rawHostAccountId))
                {
                    groupHostAccountId = new Guid(rawHostAccountId);
                }
            }
            catch
            {
                groupHostAccountId = Guid.Empty;
            }

            return true;
        }

        private static readonly byte[] CoreHelloPayloadPrefix = HexToBytes("02310000000007000000322E302E322E3720000000433245314137464537424233463930443330413242414331333135443431463001");
        private static readonly byte[] CoreIntroduceGameClientPayload = HexToBytes("012500000003010000000000000005001600000000090000003132372E302E302E3101000000000000000100000000000000");
        private static readonly byte[] CoreInitPayload = HexToBytes("0116000000000D02EA0710280000000100000001000000000000000100000000000000");

        private const ulong DefaultIntroduceMsgNo = 1UL;
        private const ulong AccountEntityId = 2UL;
        private const ulong MetaGameplayEntityId = 3UL;
        private const ulong HubEntityId = 4UL;
        private const ushort GameClientConnectionTypeId = 5;

        private long _nextGameClientEntityId = 1000;
        private readonly object _identityEntityIdLock = new object();
        private readonly Dictionary<Guid, ulong> _gameClientEntityIdByIdentity = new Dictionary<Guid, ulong>();

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly LocalUserStore _userStore;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly CareerInfoGenerator _careerInfoGenerator;
        private readonly MatchConfigurationGenerator _matchConfigurationGenerator;
        private readonly CharacterStatePushBroker _characterStatePushBroker;
        private readonly PortedMissionRewardService _missionRewardService;
        private readonly PortedStoryProgressionService _storyProgressionService;
        private readonly PortedSkillPurchaseService _skillPurchaseService;
        private readonly PortedShopInventoryService _shopInventoryService;
        private readonly PortedHubInstanceManager _portedHubInstanceManager;

        // APlay DirectSystem messages include an 8-byte message number the client may use for ordering/dedup.
        // For MetaGameplay pushes we must keep these monotonic even if the client repeats a request with a lower MsgNo.
        private long _metaGameplayOutMsgNoHighWatermark;

        private const int HubStateDedupWindowMs = 1500;
        private const int CreationInfoDedupWindowMs = 10000;
        private readonly object _hubPushDedupLock = new object();
        private readonly Dictionary<string, HubPushDedupState> _hubPushDedupByPeer = new Dictionary<string, HubPushDedupState>(StringComparer.OrdinalIgnoreCase);
        private readonly HubPresenceRegistry _hubPresenceRegistry = new HubPresenceRegistry();
        private readonly object _hubPeerStreamsLock = new object();
        private readonly Dictionary<string, NetworkStream> _hubPeerStreams = new Dictionary<string, NetworkStream>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hubAnnouncedByPeerLock = new object();
        private readonly Dictionary<string, HashSet<string>> _hubAnnouncedByPeer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _hubReadyByPeerLock = new object();
        private readonly Dictionary<string, HashSet<string>> _hubReadyByPeer = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private sealed class HubPushDedupState
        {
            public ulong HubStateHash;
            public long HubStateSentUtcTicks;
            public ulong CreationInfoHash;
            public long CreationInfoSentUtcTicks;
        }

        private static ulong ComputeFnv1a64(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return 0UL;
            }

            unchecked
            {
                const ulong offset = 1469598103934665603UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                for (var i = 0; i < data.Length; i++)
                {
                    hash ^= (ulong)data[i];
                    hash *= prime;
                }
                return hash;
            }
        }

        private bool ShouldSuppressDuplicateHubPush(string peer, bool isCreationInfo, byte[] payload)
        {
            try
            {
                if (IsNullOrWhiteSpace(peer) || payload == null || payload.Length == 0)
                {
                    return false;
                }

                var now = DateTime.UtcNow.Ticks;
                var hash = ComputeFnv1a64(payload);
                if (hash == 0UL)
                {
                    return false;
                }

                lock (_hubPushDedupLock)
                {
                    HubPushDedupState state;
                    if (!_hubPushDedupByPeer.TryGetValue(peer, out state) || state == null)
                    {
                        state = new HubPushDedupState();
                        _hubPushDedupByPeer[peer] = state;
                    }

                    var windowTicks = (long)(TimeSpan.TicksPerMillisecond * (isCreationInfo ? CreationInfoDedupWindowMs : HubStateDedupWindowMs));
                    if (isCreationInfo)
                    {
                        if (state.CreationInfoHash == hash && state.CreationInfoSentUtcTicks > 0 && (now - state.CreationInfoSentUtcTicks) <= windowTicks)
                        {
                            return true;
                        }
                        state.CreationInfoHash = hash;
                        state.CreationInfoSentUtcTicks = now;
                        return false;
                    }

                    if (state.HubStateHash == hash && state.HubStateSentUtcTicks > 0 && (now - state.HubStateSentUtcTicks) <= windowTicks)
                    {
                        return true;
                    }
                    state.HubStateHash = hash;
                    state.HubStateSentUtcTicks = now;
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger)
            : this(options, logger, new LocalUserStore(options, logger), null, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
            : this(options, logger, userStore, null, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap)
            : this(options, logger, userStore, sessionIdentityMap, null)
        {
        }

        public APlayTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap, CharacterStatePushBroker characterStatePushBroker)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore ?? new LocalUserStore(options, logger);
            _sessionIdentityMap = sessionIdentityMap;
            _careerInfoGenerator = new CareerInfoGenerator(logger, _userStore);
            _matchConfigurationGenerator = new MatchConfigurationGenerator(logger);
            _characterStatePushBroker = characterStatePushBroker ?? CharacterStatePushBroker.Shared;
            _missionRewardService = new PortedMissionRewardService(_options);
            _storyProgressionService = new PortedStoryProgressionService(_options);
            _skillPurchaseService = new PortedSkillPurchaseService(_options);
            _shopInventoryService = new PortedShopInventoryService(_options);
            _portedHubInstanceManager = new PortedHubInstanceManager(new PortedHubRepository(new PortedHubLoader()), false);
        }

        private void TryFlushPendingCharacterStatePushes(Guid identityGuid, string identityHash, int activeCareerIndex, string peer, NetworkStream stream)
        {
            if (_characterStatePushBroker == null || _userStore == null || _careerInfoGenerator == null)
            {
                return;
            }
            if (identityGuid == Guid.Empty || IsNullOrWhiteSpace(identityHash) || stream == null)
            {
                return;
            }

            CharacterStatePushPaths paths;
            if (!_characterStatePushBroker.TryDequeue(identityGuid, out paths) || paths == CharacterStatePushPaths.None)
            {
                return;
            }

            try
            {
                var slot = _userStore.GetOrCreateCareer(identityHash, activeCareerIndex, false);
                if (slot == null)
                {
                    return;
                }

                var sendCount = 0;
                if ((paths & CharacterStatePushPaths.CareerSummaries) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.Wallet) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.Inventory) != 0) sendCount++;
                if ((paths & CharacterStatePushPaths.MetaSnapshot) != 0) sendCount++;

                if (sendCount <= 0)
                {
                    return;
                }

                var msgNo = ReserveMetaGameplayMsgNos(sendCount);

                if ((paths & CharacterStatePushPaths.CareerSummaries) != 0)
                {
                    var updatePayload = BuildUtf16StringPayload(BuildCareerSummaryJson(_userStore.GetCareers(identityHash)));
                    var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.Wallet) != 0)
                {
                    var serializedWallet = SerializeWalletForSlot(slot);
                    var walletChangedPayload = BuildUtf16StringPayload(serializedWallet);
                    var walletChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 32, walletChangedPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(walletChangedCore), "sent MetaGameplayCommunicationObject WalletChanged (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.Inventory) != 0)
                {
                    var serializedInventory = SerializeInventoryFromSlot(slot);
                    var emptyShopChanges = InventorySerializer.SerializeShopItemChanges(new ShopItemChanges
                    {
                        Failed = false,
                        TotalNuyenChange = 0,
                        AppliedChanges = new ItemChange[0],
                        NotAppliedChanges = new ItemChange[0],
                    });

                    var inventoryChangedPayload = BuildUtf16StringPayload(serializedInventory, emptyShopChanges);
                    var inventoryChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 31, inventoryChangedPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(inventoryChangedCore), "sent MetaGameplayCommunicationObject InventoryChanged (queued character-state push)");
                }

                if ((paths & CharacterStatePushPaths.MetaSnapshot) != 0)
                {
                    var zippedCareerInfo = _careerInfoGenerator.GetZippedCareerInfo(identityGuid, activeCareerIndex, slot);
                    var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNo++);
                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient (queued character-state push)");
                }

                _logger.LogAdmin(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "character-state-push",
                    action = "flushed",
                    identityGuid = identityGuid,
                    careerIndex = activeCareerIndex,
                    paths = paths.ToString(),
                });
            }
            catch (Exception ex)
            {
                _logger.LogAdmin(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "character-state-push",
                    action = "flush-failed",
                    identityGuid = identityGuid,
                    careerIndex = activeCareerIndex,
                    paths = paths.ToString(),
                    error = ex.Message,
                });
            }
        }

        private static byte[] BuildCoreIntroduceGameClientPayload(ulong gameClientEntityId, string remoteAddress, ulong aplayClientId)
        {
            if (gameClientEntityId == 0UL)
            {
                gameClientEntityId = 1UL;
            }
            if (IsNullOrWhiteSpace(remoteAddress))
            {
                remoteAddress = "127.0.0.1";
            }

            // CRITICAL: the client is very picky about the introduce packet layout.
            // The known-good hardcoded payload (`CoreIntroduceGameClientPayload`) is:
            //   0x01 + int32(len) + 0x03 + uint64(entityId) + ushort(typeId=5) + int32(payloadLen) + payload + uint64(msgNo)
            // Where:
            //   - `len` does NOT include the trailing msgNo (it matches the captured 0x25 value for addr="127.0.0.1").
            //   - `payload` is: uint8(isAdmin=0) + int32(addrLen) + ASCII(addr) + uint64(aplayClientId)

            var addrBytes = Encoding.ASCII.GetBytes(remoteAddress);
            var payload = Concat(
                new byte[] { 0 },
                BitConverter.GetBytes(addrBytes.Length),
                addrBytes,
                BitConverter.GetBytes(aplayClientId));

            var len = 1 + 8 + 2 + 4 + payload.Length;
            return Concat(
                new byte[] { 0x01 },
                BitConverter.GetBytes(len),
                new byte[] { 0x03 },
                BitConverter.GetBytes(gameClientEntityId),
                BitConverter.GetBytes(GameClientConnectionTypeId),
                BitConverter.GetBytes(payload.Length),
                payload,
                BitConverter.GetBytes(DefaultIntroduceMsgNo));
        }

        private static byte[] BuildCoreApInitializedPayload(uint connectedServerId, ulong entityId, ulong msgNo)
        {
            // APlay-level message type 0 (Initialized):
            //   uint8(type=0) + APDateTime(9 bytes) + uint32(connectedServerId) + uint64(entityId)
            var raw = Concat(
                new byte[] { 0 },
                BuildApDatePayload(DateTimeOffset.UtcNow),
                BitConverter.GetBytes(connectedServerId),
                BitConverter.GetBytes(entityId));

            return Concat(
                new byte[] { 0x01 },
                BitConverter.GetBytes(raw.Length),
                raw,
                BitConverter.GetBytes(msgNo));
        }

        private static byte[] BuildCoreWelcomePayload(ulong id, ulong secret, ulong lastClientMsgNo)
        {
            // Core message type 0 (Welcome): uint8(type=0) + uint64(id) + uint64(secret) + uint64(lastClientMsgNo)
            var raw = Concat(
                new byte[] { 0 },
                BitConverter.GetBytes(id),
                BitConverter.GetBytes(secret),
                BitConverter.GetBytes(lastClientMsgNo));
            return Concat(BitConverter.GetBytes(raw.Length), raw);
        }

        private void SendPendingLootPreviews(ServerSimulationSession simulationSession, System.Net.Sockets.NetworkStream stream, string peer, ulong msgNoBase)
        {
            if (simulationSession == null || stream == null)
            {
                return;
            }

            string[] previews;
            try
            {
                previews = simulationSession.DrainPendingLootPreviews();
            }
            catch
            {
                return;
            }

            if (previews == null || previews.Length == 0)
            {
                return;
            }

            var idx = 0;
            for (var i = 0; i < previews.Length; i++)
            {
                var itemId = previews[i];
                if (IsNullOrWhiteSpace(itemId))
                {
                    continue;
                }

                try
                {
                    // MetaGameplayCommunicationObject.onLootPreview(string item)
                    var payload = BuildUtf16StringPayload(itemId);
                    var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 30, payload), msgNoBase + (ulong)idx);
                    SendRawFrame(stream, peer, PrefixLength(core), "sent MetaGameplayCommunicationObject LootPreview (itemId=" + itemId + ")");
                    idx++;
                }
                catch
                {
                }
            }
        }

        private static StoryMissionstate ParseStoryMissionStateOrDefault(string value, StoryMissionstate fallback)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            try
            {
                return (StoryMissionstate)Enum.Parse(typeof(StoryMissionstate), value.Trim(), true);
            }
            catch
            {
                return fallback;
            }
        }

        private bool IsMissionCompletedForCareer(string identityHash, int careerIndex, string missionName, HashSet<string> fallbackCompletedMissions)
        {
            if (IsNullOrWhiteSpace(missionName))
            {
                return false;
            }

            try
            {
                if (_userStore != null && !IsNullOrWhiteSpace(identityHash))
                {
                    var slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, false);
                    if (slot != null && slot.MainCampaignMissionStates != null)
                    {
                        string raw;
                        if (slot.MainCampaignMissionStates.TryGetValue(missionName, out raw) && !IsNullOrWhiteSpace(raw))
                        {
                            var state = ParseStoryMissionStateOrDefault(raw, StoryMissionstate.Available);
                            if (state >= StoryMissionstate.ReadyToReceiveRewards)
                            {
                                return true;
                            }
                        }

                        return false;
                    }
                }
            }
            catch
            {
            }

            return fallbackCompletedMissions != null && fallbackCompletedMissions.Contains(missionName);
        }

        private void SendUnlocksChanged(NetworkStream stream, string peer, ulong msgNo, string[] activatedUnlocks, string[] deactivatedUnlocks, string reason)
        {
            if (stream == null)
            {
                return;
            }

            var changes = new UnlockChanges();
            AddUnlockChangeEntries(changes.ActivatedUnlocks, activatedUnlocks);
            AddUnlockChangeEntries(changes.DeactivatedUnlocks, deactivatedUnlocks);
            if (changes.IsEmpty())
            {
                return;
            }

            var payload = BuildUtf16StringPayload(Json.Serialize(changes));
            var core = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 28, payload), msgNo);
            SendRawFrame(stream, peer, PrefixLength(core), "sent MetaGameplayCommunicationObject UnlocksChanged" + (IsNullOrWhiteSpace(reason) ? string.Empty : " (" + reason + ")"));
        }

        private void SendMissionReward(NetworkStream stream, string peer, ulong msgNo, MissionReward missionReward, string reason)
        {
            if (stream == null || missionReward == null)
            {
                return;
            }

            var earnedCurrencies = new List<object>();
            var currencies = missionReward.EarnedCurrencies ?? new CurrencyReward[0];
            for (var i = 0; i < currencies.Length; i++)
            {
                var currency = currencies[i];
                if (currency == null || currency.EarnedValue == 0)
                {
                    continue;
                }

                earnedCurrencies.Add(new Dictionary<string, object>
                {
                    { "CurrencyId", currency.CurrencyId.ToString() },
                    { "EarnedValue", currency.EarnedValue },
                });
            }

            var itemChangesPayload = new List<object>();
            var itemChanges = missionReward.ItemChanges ?? new ItemChange[0];
            for (var i = 0; i < itemChanges.Length; i++)
            {
                var change = itemChanges[i];
                if (change == null || IsNullOrWhiteSpace(change.ItemDefintionId) || change.Delta == 0)
                {
                    continue;
                }

                itemChangesPayload.Add(new Dictionary<string, object>
                {
                    { "ItemDefintionId", change.ItemDefintionId },
                    { "Delta", change.Delta },
                    { "Quality", change.Quality },
                    { "Flavour", change.Flavour },
                });
            }

            var rewardJson = Json.Serialize(new Dictionary<string, object>
            {
                { "GrantedUnlocks", missionReward.GrantedUnlocks ?? new string[0] },
                { "EarnedCurrencies", earnedCurrencies.ToArray() },
                { "ItemChanges", itemChangesPayload.ToArray() },
            });

            var rewardPayload = BuildUtf16StringPayload(rewardJson);
            var rewardCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 35, rewardPayload), msgNo);
            SendRawFrame(stream, peer, PrefixLength(rewardCore), "sent MetaGameplayCommunicationObject GotMissionReward " + reason);
        }

        private static void AddUnlockChangeEntries(List<Unlock> target, string[] unlocks)
        {
            if (target == null || unlocks == null || unlocks.Length <= 0)
            {
                return;
            }

            for (var i = 0; i < unlocks.Length; i++)
            {
                var unlock = unlocks[i];
                if (IsNullOrWhiteSpace(unlock))
                {
                    continue;
                }

                target.Add(new Unlock
                {
                    TechnicalName = unlock,
                });
            }
        }

        private static bool TryGetUlong(IDictionary dict, string key, out ulong value)
        {
            value = 0UL;
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return false;
            }
            try
            {
                value = Convert.ToUInt64(dict[key]);
                return true;
            }
            catch
            {
                value = 0UL;
                return false;
            }
        }

        private void HandleClient(TcpClient client, ManualResetEvent stopEvent)
        {
            using (client)
            {
                var peer = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";
                using (var stream = client.GetStream())
                {
                    // The client uses `GameClientConnection.APlayEntityId` as its local PlayerID.
                    // To make coop ownership work, each connection must have a unique entity id.
                    var gameClientEntityId = AllocateGameClientEntityId();
                    var gameClientIntroducePayload = BuildCoreIntroduceGameClientPayload(gameClientEntityId, "127.0.0.1", 1UL);
                    var apInitializedPayload = BuildCoreApInitializedPayload(1U, gameClientEntityId, DefaultIntroduceMsgNo + 1UL);

                    var connectionClosed = new ManualResetEvent(false);
                    var keepAliveLoopStarted = false;
                    long keepAliveMsgNo = 500000;

                    var first = ReadChunk(stream);
                    if (first.Length == 0)
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-conn", peer = peer, note = "connected then closed" });
                        return;
                    }

                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "aplay-conn",
                        peer = peer,
                        bytes = first.Length,
                        preview = Encoding.ASCII.GetString(first, 0, Math.Min(first.Length, 180)),
                    });

                    if (LooksLikeHttp(first))
                    {
                        HandleHttpProbe(stream, peer, first);
                        return;
                    }

                    if (StartsWith(first, new byte[] { (byte)'X', (byte)'M', (byte)'L', 0x00 }))
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-proto", peer = peer, action = "received", payload = "XML\\u0000" });
                    }

                    var buffer = new List<byte>(first);
                    var sentIntro = false;
                    var sentInit = false;
                    var sentAccountIntro = false;
                    var sentMetaGameplayIntro = false;
                    var sentRegularConnectReply = false;
                    var sentEnterCareerUpdate = false;
                    var sentHubIntro = false;
                    var sentMissionEntityIntros = false;
                    byte[] cachedHubStatePayload = null;
                    byte[] cachedCreationInfoPayload = null;

                    // Extra diagnostics for the post-character-creation stall.
                    long metaSendMessageSeen = 0;
                    long metaSetStoryMissionStateSeen = 0;
                    long metaStartSingleplayerMissionSeen = 0;
                    long metaRequestHubSeen = 0;
                    long postCreateArmGeneration = 0;

                    // Per-connection identity resolved from RequestToLogin(sessionHash, deviceModel, loginMethod).
                    // Enforced: no fallback to a global/default identity.
                    string activeIdentityHash = null;
                    Guid activeIdentityGuid = Guid.Empty;
                    var activeCareerIndex = 0;
                    var activeCharacterName = "OfflineRunner";
                    string currentHubInstanceId = null;
                    PortedHubInstance currentHubInstance = null;
                    var hubReadyFallbackState = CreateHubReadyFallbackState();

                    Action<string> cancelHubReadyFallback = null;
                    Action<string, string, string> armHubReadyFallback = null;

                    cancelHubReadyFallback = delegate (string reason)
                    {
                        CancelHubReadyFallback(hubReadyFallbackState, peer, reason);
                    };

                    armHubReadyFallback = delegate (string hubId, string characterId, string reason)
                    {
                        ArmHubReadyFallback(hubReadyFallbackState, stopEvent, connectionClosed, peer, hubId, characterId, reason);
                    };

                    // Track simple story progression locally so DirectStart missions don't loop forever.
                    // Keyed by map name (e.g., "1_010_Prologue").
                    var completedStoryMissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string currentMissionMapName = null;
                    string currentCoopGroupName = null;

                    ServerSimulationSession simulationSession = null;
                    object simulationSessionSync = null;

                    const ulong gameworldEntityId = 5;
                    const ulong missionInstanceEntityId = 6;
                    const ulong missionCommandEntityId = 7;

                    const ushort gameworldCommunicationObjectTypeId = 7;
                    const ushort missionInstanceCommunicationObjectTypeId = 9;
                    const ushort missionCommandCommunicationObjectTypeId = 10;

                    // Authoritative simulation session (created per mission) used to safely skip AI turns.
                    RegisterHubPeerStream(peer, stream);

                    while (!stopEvent.WaitOne(0))
                    {
                        byte[] frame;
                        while (TryExtractNullTerminatedFrame(buffer, out frame))
                        {
                            if (frame.Length == 0)
                            {
                                continue;
                            }

                            var asciiFrame = Encoding.ASCII.GetString(frame);
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "aplay-frame",
                                peer = peer,
                                frame = asciiFrame,
                                rawHex = ToHexString(frame, 0, Math.Min(64, frame.Length)).ToLowerInvariant(),
                            });

                            if (asciiFrame == "XML")
                            {
                                continue;
                            }

                            byte[] decoded;
                            try
                            {
                                decoded = Convert.FromBase64String(asciiFrame);
                            }
                            catch
                            {
                                _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-frame-invalid-base64", peer = peer, frame = asciiFrame });
                                continue;
                            }

                            var decodedLog = new Dictionary<string, object>();
                            decodedLog["ts"] = RequestLogger.UtcNowIso();
                            decodedLog["type"] = "aplay-frame-decoded";
                            decodedLog["peer"] = peer;
                            decodedLog["bytes"] = decoded.Length;
                            decodedLog["decodedHex"] = ToHexString(decoded, 0, decoded.Length).ToLowerInvariant();
                            if (decoded.Length >= 5)
                            {
                                decodedLog["coreEnvelopeLen"] = ReadInt32LE(decoded, 0);
                                decodedLog["coreMessageType"] = decoded[4];
                            }
                            var utf16Preview = Encoding.Unicode.GetString(decoded).Trim('\0');
                            if (!IsNullOrWhiteSpace(utf16Preview))
                            {
                                decodedLog["utf16Preview"] = utf16Preview.Length > 200 ? utf16Preview.Substring(0, 200) : utf16Preview;
                            }
                            _logger.Log(decodedLog);

                            // Client "hello" is a Core.Client->Server message: RawData(len=1, payload={0x00}).
                            // That decodes to: int32(1) + uint8(0). Some clients may base64-encode it as "AQAAAAA=".
                            if (decoded.Length == 5 && ReadInt32LE(decoded, 0) == 1 && decoded[4] == 0)
                            {
                                var welcome = BuildCoreWelcomePayload(1UL, 1UL, 0UL);
                                SendRawFrame(stream, peer, welcome, "sent core welcome");
                            }

                            if (decoded.Length < 8)
                            {
                                continue;
                            }

                            var msgLen = ReadInt32LE(decoded, 0);
                            var corePayload = new byte[decoded.Length - 4];
                            Buffer.BlockCopy(decoded, 4, corePayload, 0, corePayload.Length);
                            if (msgLen != corePayload.Length)
                            {
                                continue;
                            }

                            if (StartsWith(corePayload, CoreHelloPayloadPrefix))
                            {
                                if (!sentIntro)
                                {
                                    SendRawFrame(stream, peer, PrefixLength(gameClientIntroducePayload), "sent AP introduce shared entity (type=5 game client connection)");
                                    sentIntro = true;
                                }

                                if (!sentInit)
                                {
                                    // IMPORTANT: entityId in Initialized must match the player's entity id.
                                    SendRawFrame(stream, peer, PrefixLength(apInitializedPayload), "sent AP initialized in response to AP hello (entityId=" + gameClientEntityId + ")");
                                    sentInit = true;
                                }
                            }

                            if (corePayload.Length == 0 || corePayload[0] != 3)
                            {
                                continue;
                            }

                            var direct = ParseCoreDirectSystem(corePayload);
                            if (!direct.HasValue)
                            {
                                continue;
                            }

                            var shared = ParseApSharedFieldEvent(direct.Value.Raw);
                            if (!shared.HasValue)
                            {
                                continue;
                            }

                            List<string> payloadStrings;
                            try
                            {
                                payloadStrings = ParseUtf16StringPayload(shared.Value.Data);
                            }
                            catch (Exception ex)
                            {
                                payloadStrings = new List<string>();
                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "aplay-utf16-payload-parse-failed",
                                    peer = peer,
                                    message = ex.Message,
                                });
                            }

                            // Some APlay calls (notably MetaGameplayCommunicationObject.ChangeCharacter / callField(6))
                            // use a WString encoding that isn't our simple int32-length-prefixed list. If we couldn't
                            // parse any strings, fall back to scanning for a UTF-16 JSON object within the payload.
                            if (payloadStrings.Count == 0)
                            {
                                var extracted = TryExtractUtf16JsonObject(shared.Value.Data);
                                if (!IsNullOrWhiteSpace(extracted))
                                {
                                    payloadStrings.Add(extracted);
                                }
                            }

                            string prepareMatchIdentifier = null;
                            string prepareMatchPlayers = null;
                            bool prepareMatchCoop = false;
                            string prepareMatchMapName = null;
                            string prepareMatchSelectedHenchmen = null;
                            var hasPrepareMatchPayload = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 4
                                && TryParsePrepareMatchPayload(
                                    shared.Value.Data,
                                    out prepareMatchIdentifier,
                                    out prepareMatchPlayers,
                                    out prepareMatchCoop,
                                    out prepareMatchMapName,
                                    out prepareMatchSelectedHenchmen);

                            if (hasPrepareMatchPayload)
                            {
                                payloadStrings = new List<string>(4)
                                {
                                    prepareMatchIdentifier ?? string.Empty,
                                    prepareMatchPlayers ?? string.Empty,
                                    prepareMatchMapName ?? string.Empty,
                                    prepareMatchSelectedHenchmen ?? string.Empty,
                                };

                                var selectedPreview = prepareMatchSelectedHenchmen ?? string.Empty;
                                if (selectedPreview.Length > 320)
                                {
                                    selectedPreview = selectedPreview.Substring(0, 320);
                                }

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "aplay-preparematch-decoded",
                                    peer = peer,
                                    matchIdentifier = prepareMatchIdentifier,
                                    coop = prepareMatchCoop,
                                    mapName = prepareMatchMapName,
                                    selectedHenchmenLength = prepareMatchSelectedHenchmen != null ? prepareMatchSelectedHenchmen.Length : 0,
                                    selectedHenchmenPreview = selectedPreview,
                                });
                            }

                            var isHubEntityCall = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == HubEntityId;
                            if (isHubEntityCall)
                            {
                                var payloadPreview = payloadStrings.Count > 0 && payloadStrings[0] != null
                                    ? payloadStrings[0]
                                    : string.Empty;
                                if (payloadPreview.Length > 320)
                                {
                                    payloadPreview = payloadPreview.Substring(0, 320);
                                }

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "hub-entity-call",
                                    peer = peer,
                                    apMsgId = shared.Value.ApMsgId,
                                    entityId = shared.Value.EntityId,
                                    fieldId = shared.Value.FieldId,
                                    dataBytes = shared.Value.Data != null ? shared.Value.Data.Length : 0,
                                    payloadCount = payloadStrings.Count,
                                    payloadPreview = payloadPreview,
                                });

                                if (shared.Value.FieldId == 1 || shared.Value.FieldId == 2)
                                {
                                    var hubPos = 0;
                                    string movedCharacterId;
                                    if (TryReadUtf16LengthPrefixedString(shared.Value.Data, ref hubPos, out movedCharacterId))
                                    {
                                        int rawX;
                                        int rawY;
                                        if (TryReadInt32LE(shared.Value.Data, ref hubPos, out rawX)
                                            && TryReadInt32LE(shared.Value.Data, ref hubPos, out rawY))
                                        {
                                            var movedX = BitConverter.ToSingle(BitConverter.GetBytes(rawX), 0);
                                            var movedY = BitConverter.ToSingle(BitConverter.GetBytes(rawY), 0);

                                            if (shared.Value.FieldId == 1)
                                            {
                                                _hubPresenceRegistry.UpdatePosition(peer, movedX, movedY);

                                                HubPresenceRegistry.Participant movementParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out movementParticipant);
                                                var movementHubId = ResolveParticipantHubId(movementParticipant, currentHubInstanceId);

                                                if (!IsHubMoveOwnershipValid(peer, activeIdentityGuid, movedCharacterId, movementParticipant))
                                                {
                                                    continue;
                                                }

                                                if (movementParticipant != null
                                                    && !IsNullOrWhiteSpace(movedCharacterId)
                                                    && !string.Equals(movementParticipant.CharacterId, movedCharacterId, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    var previousCharacterId = movementParticipant.CharacterId;
                                                    var updatedHubId = ResolveParticipantHubId(movementParticipant, movementHubId);

                                                    RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                                        peer,
                                                        movementParticipant.AccountId,
                                                        movementParticipant.IdentityHash,
                                                        movementParticipant.CareerIndex,
                                                        movedCharacterId,
                                                        movementParticipant.CharacterName,
                                                        updatedHubId,
                                                        movedX,
                                                        movedY,
                                                        "hub-move-character-shift");

                                                    if (!IsNullOrWhiteSpace(updatedHubId) && !IsNullOrWhiteSpace(movementParticipant.CharacterId))
                                                    {
                                                        BroadcastHubStateRemove(updatedHubId, peer, movementParticipant.CharacterId);
                                                    }

                                                    HubPresenceRegistry.Participant shiftedParticipant;
                                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out shiftedParticipant);
                                                    var shiftedHubId = ResolveParticipantHubId(shiftedParticipant, updatedHubId);
                                                    if (shiftedParticipant != null && !IsNullOrWhiteSpace(shiftedHubId))
                                                    {
                                                        ClearHubAnnouncementForAllPeers(shiftedHubId, shiftedParticipant.CharacterId);
                                                        BroadcastHubStateAddToReadyPeers(shiftedHubId, peer, shiftedParticipant, "character-shift");
                                                    }

                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "hub-characterid-shift",
                                                        peer = peer,
                                                        oldCharacterId = movementParticipant.CharacterId ?? string.Empty,
                                                        newCharacterId = movedCharacterId,
                                                        hubId = updatedHubId ?? string.Empty,
                                                    });

                                                    movementHubId = updatedHubId;
                                                    movementParticipant = shiftedParticipant;

                                                    if (currentHubInstance != null && !IsNullOrWhiteSpace(movementParticipant != null ? movementParticipant.CharacterId : null))
                                                    {
                                                        CareerSlot shiftedSlot = null;
                                                        if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                                        {
                                                            try
                                                            {
                                                                shiftedSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                                            }
                                                            catch
                                                            {
                                                                shiftedSlot = null;
                                                            }
                                                        }

                                                        var shiftedSnapshot = BuildMappedPlayerCharacterSnapshotForHub(
                                                            activeIdentityGuid,
                                                            movedCharacterId,
                                                            movementParticipant != null ? movementParticipant.CharacterName : activeCharacterName,
                                                            shiftedSlot);
                                                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, previousCharacterId);
                                                        if (shiftedSnapshot != null)
                                                        {
                                                            currentHubInstance.AddCharacter(shiftedSnapshot);
                                                        }
                                                    }
                                                }

                                                var movementCharacterId = movedCharacterId;
                                                if (IsNullOrWhiteSpace(movementCharacterId))
                                                {
                                                    if (movementParticipant != null)
                                                    {
                                                        movementCharacterId = movementParticipant.CharacterId;
                                                    }
                                                }

                                                if (currentHubInstance == null && !IsNullOrWhiteSpace(movementHubId) && _portedHubInstanceManager != null)
                                                {
                                                    currentHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(movementHubId);
                                                }

                                                if (currentHubInstance != null && !IsNullOrWhiteSpace(movementCharacterId))
                                                {
                                                    currentHubInstance.QueueMoveRequest(movementCharacterId, new Vector2D(movedX, movedY));
                                                    currentHubInstanceId = currentHubInstance.HubId;
                                                }

                                                var firstMoveReadyActivated = TryActivateHubReadiness(peer, movementHubId, "first-move");
                                                if (firstMoveReadyActivated)
                                                {
                                                    cancelHubReadyFallback("first-move");
                                                }

                                                BroadcastHubMovement(movementHubId, peer, movementCharacterId, movedX, movedY);
                                            }
                                            else
                                            {
                                                HubPresenceRegistry.Participant previousParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                                var hubIdForPresence = currentHubInstanceId;
                                                if (IsNullOrWhiteSpace(hubIdForPresence))
                                                {
                                                    hubIdForPresence = ResolveParticipantHubId(previousParticipant, null);
                                                }
                                                if (IsNullOrWhiteSpace(hubIdForPresence) && _userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    try
                                                    {
                                                        var slot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                                        if (slot != null && !IsNullOrWhiteSpace(slot.HubId))
                                                        {
                                                            hubIdForPresence = slot.HubId;
                                                        }
                                                    }
                                                    catch
                                                    {
                                                    }
                                                }

                                                var characterIdForPresence = !IsNullOrWhiteSpace(movedCharacterId)
                                                    ? movedCharacterId
                                                    : ResolveHubCharacterIdentifier(null, activeIdentityGuid, activeCareerIndex, previousParticipant != null ? previousParticipant.CharacterId : null);

                                                RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                                    peer,
                                                    activeIdentityGuid,
                                                    activeIdentityHash,
                                                    activeCareerIndex,
                                                    characterIdForPresence,
                                                    activeCharacterName,
                                                    hubIdForPresence,
                                                    movedX,
                                                    movedY,
                                                    "hub-enter-field-2");

                                                if (!IsNullOrWhiteSpace(hubIdForPresence))
                                                {
                                                    currentHubInstanceId = hubIdForPresence;
                                                    if (_portedHubInstanceManager != null)
                                                    {
                                                        currentHubInstance = _portedHubInstanceManager.RequestHubInstanceByHubId(currentHubInstanceId);
                                                    }
                                                }

                                                HubPresenceRegistry.Participant currentParticipant;
                                                _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);
                                                var previousHubId = ResolveParticipantHubId(previousParticipant, null);
                                                var currentParticipantHubId = ResolveParticipantHubId(currentParticipant, hubIdForPresence);

                                                if (previousParticipant != null
                                                    && !IsNullOrWhiteSpace(previousHubId)
                                                    && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                                    && !string.Equals(previousHubId, hubIdForPresence, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    BroadcastHubStateRemove(previousHubId, peer, previousParticipant.CharacterId);
                                                }

                                                if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipantHubId))
                                                {
                                                    var shouldBroadcastAdd = previousParticipant == null
                                                        || !string.Equals(previousHubId, currentParticipantHubId, StringComparison.OrdinalIgnoreCase)
                                                        || !string.Equals(previousParticipant.CharacterId, currentParticipant.CharacterId, StringComparison.OrdinalIgnoreCase);

                                                    if (shouldBroadcastAdd)
                                                    {
                                                        ClearHubAnnouncementsForPeerHub(peer, currentParticipantHubId);
                                                    }

                                                    _logger.Log(new
                                                    {
                                                        ts = RequestLogger.UtcNowIso(),
                                                        type = "hub-enter",
                                                        peer = peer,
                                                        hubId = currentParticipantHubId,
                                                        characterId = currentParticipant.CharacterId ?? string.Empty,
                                                        x = movedX,
                                                        y = movedY,
                                                        previousHubId = previousHubId ?? string.Empty,
                                                        previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                                        hubChanged = previousParticipant == null || !string.Equals(previousHubId, currentParticipantHubId, StringComparison.OrdinalIgnoreCase),
                                                    });

                                                    armHubReadyFallback(currentParticipantHubId, currentParticipant.CharacterId, "hub-enter-field-2");
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (shared.Value.FieldId == 3)
                                {
                                    cancelHubReadyFallback("hub-leave-field-3");
                                    HubPresenceRegistry.Participant leavingParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out leavingParticipant);
                                    if (currentHubInstance != null && leavingParticipant != null && !IsNullOrWhiteSpace(leavingParticipant.CharacterId))
                                    {
                                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, leavingParticipant.CharacterId);
                                        currentHubInstance = null;
                                    }
                                    RemoveHubPresenceWithBroadcast(peer);
                                }
                            }

                            var isRegularConnect = shared.Value.ApMsgId == 1
                                && shared.Value.FieldId == 3
                                && PayloadContains(payloadStrings, "RegularConnect");

                            if (isRegularConnect && !sentRegularConnectReply)
                            {
                                if (HandleRegularConnect(
                                    stream,
                                    peer,
                                    direct.Value,
                                    shared.Value,
                                    payloadStrings,
                                    stopEvent,
                                    connectionClosed,
                                    ref sentRegularConnectReply,
                                    ref sentAccountIntro,
                                    ref keepAliveLoopStarted,
                                    keepAliveMsgNo,
                                    ref gameClientEntityId,
                                    ref activeIdentityHash,
                                    ref activeIdentityGuid))
                                {
                                    return;
                                }
                            }

                            var isCareerBootstrapRequest = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && (shared.Value.FieldId == 10 || shared.Value.FieldId == 11);

                            var isLeaveCurrentCareer = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && shared.Value.FieldId == 12;

                            var isDeactivateCareer = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 2
                                && shared.Value.FieldId == 13;

                            if (isCareerBootstrapRequest && !sentEnterCareerUpdate)
                            {
                                if (HandleCareerBootstrapRequest(
                                    stream,
                                    peer,
                                    direct.Value,
                                    shared.Value,
                                    gameClientEntityId,
                                    activeIdentityHash,
                                    activeIdentityGuid,
                                    ref activeCareerIndex,
                                    ref activeCharacterName,
                                    ref currentHubInstanceId,
                                    ref currentHubInstance,
                                    ref cachedHubStatePayload,
                                    ref cachedCreationInfoPayload,
                                    completedStoryMissions,
                                    stopEvent,
                                    connectionClosed,
                                    delegate { return Interlocked.Read(ref metaRequestHubSeen); },
                                    armHubReadyFallback,
                                    ref sentMetaGameplayIntro,
                                    ref sentHubIntro,
                                    ref sentEnterCareerUpdate))
                                {
                                    return;
                                }
                            }

                            if (isLeaveCurrentCareer)
                            {
                                HandleLeaveCurrentCareerRequest(stream, peer, direct.Value, activeIdentityHash, cancelHubReadyFallback, ref currentHubInstanceId, ref sentEnterCareerUpdate);
                            }

                            if (isDeactivateCareer)
                            {
                                HandleDeactivateCareerRequest(stream, peer, direct.Value, shared.Value.Data, activeIdentityHash, ref activeCareerIndex, ref activeCharacterName, ref cachedCreationInfoPayload);
                            }

                            // MetaGameplayCommunicationObject callFields with no payload.
                            // See client __MetaGameplayCommunicationObject.cs:
                            // - field 5: GetMetagameplayDataSnapshot
                            // - field 10: GetHenchmanCollection
                            var isGetMetagameplayDataSnapshot = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 5;
                            if (isGetMetagameplayDataSnapshot)
                            {
                                // Keep this lightweight: just re-send the latest snapshot for the active slot.
                                var requestMsgNoBase = direct.Value.MsgNo + 90;
                                try
                                {
                                    CareerSlot slot = null;
                                    if (_userStore != null)
                                    {
                                        if (!IsNullOrWhiteSpace(activeIdentityHash))
                                        {
                                            slot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                        }
                                    }

                                    var zippedCareerInfo = (slot != null)
                                        ? _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, slot)
                                        : _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, activeCareerIndex, activeCharacterName, false);

                                    var metaSnapshotPayload = BuildUtf16StringPayload(zippedCareerInfo);
                                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), requestMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient in response to GetMetagameplayDataSnapshot");
                                }
                                catch
                                {
                                }
                            }

                            var isGetHenchmanCollection = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 10;
                            if (isGetHenchmanCollection)
                            {
                                var requestMsgNoBase = direct.Value.MsgNo + 100;
                                try
                                {
                                    var henchmanCollectionPayload = BuildUtf16StringPayload(SerializeDefaultHenchmanCollection());
                                    var henchmanCollectionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 27, henchmanCollectionPayload), requestMsgNoBase + 1);
                                    SendRawFrame(stream, peer, PrefixLength(henchmanCollectionCore), "sent MetaGameplayCommunicationObject SendHenchmanCollectionToClient in response to GetHenchmanCollection");
                                }
                                catch
                                {
                                }
                            }

                            var isMetaGameplayMessage = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && (shared.Value.FieldId == 1 || shared.Value.FieldId == 6 || shared.Value.FieldId == 7)
                                && payloadStrings.Count > 0;

                            var isMetaGameplayWrappedMessage = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 1
                                && payloadStrings.Count > 0;

                            // The character editor sends CharacterChangeCollection through MetaGameplayCommunicationObject.ChangeCharacter(...)
                            // which goes out on APlay field 6 (see client __MetaGameplayCommunicationObject.ChangeCharacter -> entity.callField(6)).
                            var isMetaGameplayChangeCharacter = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 6
                                && payloadStrings.Count > 0;

                            // Skill/talent purchases are sent via MetaGameplayCommunicationObject.ChangeSkillTrees(...)
                            // which goes out on APlay field 7 (see client __MetaGameplayCommunicationObject.ChangeSkillTrees -> entity.callField(7)).
                            var isMetaGameplayChangeSkillTrees = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 7
                                && payloadStrings.Count > 0;

                            // Hub shop purchases/sales are sent via MetaGameplayCommunicationObject.ChangeItemPosessions(...)
                            // which goes out on APlay field 8 (see client DesignedClient.cs field 8).
                            var isMetaGameplayChangeItemPosessions = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 8
                                && payloadStrings.Count > 0;

                            // Coop mission start (group-based) does not come through MetaGameplayCommunicationObject.Message.
                            // Instead, the client sends a UTF-16 string payload like:
                            //   CoopGroup<guid>_On_<mapName>S, ["<account>:0", ...], <mapName>, []
                            // If we don't respond with StartMissionAccepted/StartMissionForClients, the UI will sit at
                            // "Waiting for Game Server..." forever.
                            var coopIdentifier = hasPrepareMatchPayload
                                ? prepareMatchIdentifier
                                : (payloadStrings.Count > 0 ? payloadStrings[0] : null);
                            var isCoopMissionStart = !IsNullOrWhiteSpace(coopIdentifier)
                                && coopIdentifier.StartsWith("CoopGroup", StringComparison.OrdinalIgnoreCase)
                                && coopIdentifier.IndexOf("_On_", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (isCoopMissionStart)
                            {
                                var coopGroupName = coopIdentifier;
                                string selectedHenchmanParseSource;
                                var coopParsedSelections = ParseCoopMissionSelections(hasPrepareMatchPayload, prepareMatchSelectedHenchmen, out selectedHenchmanParseSource);
                                var mapName = ResolveCoopMissionMapName(hasPrepareMatchPayload, prepareMatchMapName, payloadStrings, coopGroupName);

                                currentMissionMapName = mapName;

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "coop-mission-start",
                                    peer = peer,
                                    apMsgId = shared.Value.ApMsgId,
                                    entityId = shared.Value.EntityId,
                                    fieldId = shared.Value.FieldId,
                                    coopGroupName = coopGroupName,
                                    mapName = mapName,
                                    memberList = hasPrepareMatchPayload
                                        ? prepareMatchPlayers
                                        : (payloadStrings.Count > 1 ? payloadStrings[1] : null),
                                    selectedHenchmanParseSource = selectedHenchmanParseSource,
                                    selectedHenchmenRawLength = hasPrepareMatchPayload && prepareMatchSelectedHenchmen != null
                                        ? prepareMatchSelectedHenchmen.Length
                                        : 0,
                                    henchSelectionCount = coopParsedSelections != null ? coopParsedSelections.Count : 0,
                                });

                                currentCoopGroupName = coopGroupName;
                                RegisterCoopMissionParticipant(coopGroupName, peer, stream);
                                UpdateCoopMissionHenchSelections(coopGroupName, activeIdentityGuid, coopParsedSelections);

                                var requestMsgNoBase = direct.Value.MsgNo + 250;

                                EnsureMissionEntitiesIntroduced(
                                    stream,
                                    peer,
                                    requestMsgNoBase,
                                    gameworldEntityId,
                                    missionInstanceEntityId,
                                    missionCommandEntityId,
                                    gameworldCommunicationObjectTypeId,
                                    missionInstanceCommunicationObjectTypeId,
                                    missionCommandCommunicationObjectTypeId,
                                    ref sentMissionEntityIntros);

                                var seed0 = 0x11111111u;
                                var seed1 = 0x22222222u;
                                var seed2 = 0x33333333u;
                                var seed3 = 0x44444444u;

                                var memberListRaw = hasPrepareMatchPayload
                                    ? prepareMatchPlayers
                                    : (payloadStrings.Count > 1 ? payloadStrings[1] : null);
                                var compressedMatchConfiguration = BuildCoopCompressedMatchConfiguration(
                                    mapName,
                                    coopGroupName,
                                    memberListRaw,
                                    activeIdentityGuid,
                                    activeIdentityHash,
                                    activeCareerIndex,
                                    activeCharacterName,
                                    gameClientEntityId,
                                    stopEvent);

                                // Coop missions must share one authoritative simulation across all peers.
                                // If each TCP connection has its own sim, neither side will ever observe the other
                                // player exhausting actions, so the team never ends and AI turns never start.
                                CoopMissionSessionState coopSession;
                                if (!TryGetOrCreateCoopMissionSession(coopGroupName, activeIdentityHash, activeCareerIndex, mapName, completedStoryMissions, out coopSession))
                                {
                                    SendMissionStartCancelled(stream, peer, requestMsgNoBase, "sent MetaGameplayCommunicationObject StartMissionCancelled (coop mission already completed)");
                                    continue;
                                }

                                simulationSessionSync = coopSession.SyncRoot;
                                simulationSession = AcquireCoopMissionSimulation(
                                    coopSession,
                                    peer,
                                    coopGroupName,
                                    mapName,
                                    activeIdentityHash,
                                    activeCareerIndex,
                                    ref seed0,
                                    ref seed1,
                                    ref seed2,
                                    ref seed3,
                                    ref compressedMatchConfiguration);

                                SendMissionStartAccepted(
                                    stream,
                                    peer,
                                    requestMsgNoBase,
                                    seed0,
                                    seed1,
                                    seed2,
                                    seed3,
                                    compressedMatchConfiguration,
                                    gameworldEntityId,
                                    missionInstanceEntityId,
                                    missionCommandEntityId,
                                    "sent MetaGameplayCommunicationObject StartMissionAccepted (coop map=" + mapName + ")");

                                SendMissionStartForClients(stream, peer, stopEvent, requestMsgNoBase, missionInstanceEntityId, "sent MissionInstanceCommunicationObject StartMissionForClients (coop)");

                                continue;
                            }

                            if (_userStore != null && isMetaGameplayChangeSkillTrees)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && (rawMessage.IndexOf("SkillTreeChanges", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTreeTechnichalName", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkillTechnichalName", StringComparison.Ordinal) >= 0))
                                {
                                    SkillTreeChanges requestedChanges = null;
                                    try
                                    {
                                        requestedChanges = JsonFxSerializerProvider.Current.Deserialize<SkillTreeChanges>(rawMessage);
                                    }
                                    catch
                                    {
                                        requestedChanges = null;
                                    }

                                    if (requestedChanges == null)
                                    {
                                        continue;
                                    }

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        var appliedSkillChanges = _skillPurchaseService.Apply(slot, requestedChanges);

                                        if (appliedSkillChanges.Persisted)
                                        {
                                            try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }
                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "skilltree-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            applyReset = appliedSkillChanges.ApplyReset,
                                            purchases = requestedChanges.Purchases != null ? requestedChanges.Purchases.Length : 0,
                                            applied = appliedSkillChanges.AppliedCount,
                                            persisted = appliedSkillChanges.Persisted,
                                            karmaBefore = appliedSkillChanges.KarmaBefore,
                                            karmaRefunded = appliedSkillChanges.KarmaRefunded,
                                            karmaCostApplied = appliedSkillChanges.KarmaSpent,
                                            karmaAfter = slot.Karma,
                                        });

                                        if (appliedSkillChanges.ShouldNotifyClient)
                                        {
                                            try
                                            {
                                                var msgNoBase = direct.Value.MsgNo + 2;
                                                var skillTreeChangedJson = JsonFxSerializerProvider.Current.Serialize<SkillTreeChanges>(appliedSkillChanges.AppliedChanges);
                                                var skillChangedPayload = BuildUtf16StringPayload(skillTreeChangedJson);
                                                var skillChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 34, skillChangedPayload), msgNoBase);
                                                SendRawFrame(stream, peer, PrefixLength(skillChangedCore), "sent MetaGameplayCommunicationObject SkillTreeChanged in response to ChangeSkillTrees");
                                            }
                                            catch
                                            {
                                            }
                                        }
                                    }
                                }
                            }

                            if (_userStore != null && isMetaGameplayChangeItemPosessions)
                            {
                                var rawMessage = payloadStrings[0];
                                if (!IsNullOrWhiteSpace(rawMessage)
                                    && rawMessage.IndexOf("ItemPossessionChanges", StringComparison.Ordinal) >= 0)
                                {
                                    ItemPossessionChanges requestedChanges = null;
                                    try
                                    {
                                        requestedChanges = JsonFxSerializerProvider.Current.Deserialize<ItemPossessionChanges>(rawMessage);
                                    }
                                    catch
                                    {
                                        requestedChanges = null;
                                    }

                                    if (requestedChanges == null)
                                    {
                                        continue;
                                    }

                                    var slotIndex = activeCareerIndex;
                                    if (slotIndex < 0)
                                    {
                                        slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                    }

                                    var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                    if (slot != null)
                                    {
                                        var appliedShopChanges = _shopInventoryService.Apply(slot, requestedChanges);

                                        if (appliedShopChanges.Persisted)
                                        {
                                            try { _userStore.UpsertCareer(activeIdentityHash, slot); } catch { }
                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "item-possession-change",
                                            peer = peer,
                                            careerIndex = slotIndex,
                                            shopKeeper = requestedChanges.ShopKeeper,
                                            itemChanges = requestedChanges.ItemChanges != null ? requestedChanges.ItemChanges.Length : 0,
                                            applied = appliedShopChanges.ShopChanges != null && appliedShopChanges.ShopChanges.AppliedChanges != null ? appliedShopChanges.ShopChanges.AppliedChanges.Length : 0,
                                            failed = appliedShopChanges.ShopChanges != null && appliedShopChanges.ShopChanges.Failed,
                                            nuyenBefore = appliedShopChanges.NuyenBefore,
                                            totalNuyenChange = appliedShopChanges.ShopChanges != null ? appliedShopChanges.ShopChanges.TotalNuyenChange : 0,
                                            nuyen = slot.Nuyen,
                                        });

                                        // Push authoritative inventory + wallet to the client.
                                        // DesignedClient.cs fields:
                                        // - 31: InventoryChanged(serializedInventory, serializedShopChanges)
                                        // - 32: WalletChanged(serializedWallet)
                                        try
                                        {
                                            var msgNoBase = direct.Value.MsgNo + 20;
                                            var serializedInventory = SerializeInventoryFromSlot(slot);
                                            var serializedShopChanges = InventorySerializer.SerializeShopItemChanges(appliedShopChanges.ShopChanges);

                                            var inventoryChangedPayload = BuildUtf16StringPayload(serializedInventory, serializedShopChanges);
                                            var inventoryChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 31, inventoryChangedPayload), msgNoBase + 1);
                                            SendRawFrame(stream, peer, PrefixLength(inventoryChangedCore), "sent MetaGameplayCommunicationObject InventoryChanged in response to ChangeItemPosessions");

                                            var serializedWallet = SerializeWalletForSlot(slot);
                                            var walletChangedPayload = BuildUtf16StringPayload(serializedWallet);
                                            var walletChangedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 32, walletChangedPayload), msgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(walletChangedCore), "sent MetaGameplayCommunicationObject WalletChanged in response to ChangeItemPosessions");
                                        }
                                        catch
                                        {
                                        }

                                        // Also push an updated metagameplay snapshot so reload flows stay consistent.
                                        try
                                        {
                                            var msgNoBase = direct.Value.MsgNo + 30;
                                            var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                            var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                            var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                            SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after ChangeItemPosessions");
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }

                            // Character changes can arrive via the MetaGameplay entity even if the decoded
                            // ApMsgId/EntityId/FieldId don't match our current expectations.
                            // If we have a decoded JSON payload and it looks like a CharacterChangeCollection,
                            // apply it unconditionally.
                            if (_userStore != null && payloadStrings.Count > 0 && PayloadContains(payloadStrings, "CharacterChangeCollection"))
                            {
                                var rawMessage = payloadStrings[0];
                                var slotIndex = activeCareerIndex;
                                if (slotIndex < 0)
                                {
                                    slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                }

                                var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                if (slot != null)
                                {
                                    var wasPendingPersistenceCreation = slot.PendingPersistenceCreation;
                                    CharacterChangeApplicationResult characterChangeResult;
                                    if (TryApplyCharacterChangeCollectionToSlot(rawMessage, slot, out characterChangeResult))
                                    {
                                        var changed = characterChangeResult.Changed;
                                        if (changed || characterChangeResult.ShouldSendCorrection)
                                        {
                                            if (!changed && characterChangeResult.ShouldSendCorrection)
                                            {
                                                _logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "career-change-collection-ignored",
                                                    peer = peer,
                                                    careerIndex = slotIndex,
                                                    reason = "portrait-update-after-creation",
                                                    oldPortrait = characterChangeResult.IgnoredPortraitOld,
                                                    newPortrait = characterChangeResult.IgnoredPortraitNew,
                                                    currentPortrait = slot.PortraitPath,
                                                });
                                            }

                                            if (changed && slot.PendingPersistenceCreation)
                                            {
                                                slot.PendingPersistenceCreation = false;
                                            }

                                            // If we just committed a brand new career creation, reset story progress to the
                                            // expected starting state. This helps the client compute the next mandatory mission
                                            // (prologue) after the intro splash.
                                            //
                                            // IMPORTANT: The user may create a new runner in an already-occupied slot. In that
                                            // case we must clear any previous campaign progress (e.g., prologue marked Completed),
                                            // otherwise the client will attempt to start the prologue and we will cancel it as
                                            // "mission-completed", producing the in-game "Server aborted mission" popup.
                                            if (changed && wasPendingPersistenceCreation)
                                            {
                                                ResetNewCareerStoryProgression(slot, completedStoryMissions);
                                            }

                                            if (changed)
                                            {
                                                if (!IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }
                                            }

                                            if (changed)
                                            {
                                                _logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "career-change-collection-applied",
                                                    peer = peer,
                                                    careerIndex = slotIndex,
                                                    characterName = slot.CharacterName,
                                                    portraitPath = slot.PortraitPath,
                                                    voiceset = slot.Voiceset,
                                                    wantsBackgroundChange = slot.WantsBackgroundChange,
                                                    equippedItemsCount = slot.EquippedItems != null ? slot.EquippedItems.Count : 0,
                                                    bodytype = slot.Bodytype,
                                                    skinTextureIndex = slot.SkinTextureIndex,
                                                    backgroundStory = slot.BackgroundStory,
                                                });
                                            }

                                            if (!IsNullOrWhiteSpace(slot.CharacterName))
                                            {
                                                activeCharacterName = slot.CharacterName;
                                            }

                                            string refreshedHubId;
                                            cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                                                slot,
                                                activeIdentityGuid,
                                                slotIndex,
                                                false,
                                                currentHubInstance,
                                                out refreshedHubId,
                                                out currentHubInstance);
                                            currentHubInstanceId = refreshedHubId;

                                            var msgNoBase = direct.Value.MsgNo + 2;
                                            var summaryJson = BuildCareerSummaryJson(!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null);
                                            var updatePayload = BuildUtf16StringPayload(summaryJson);
                                            var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase);
                                            SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after CharacterChangeCollection");

                                            try
                                            {
                                                var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                                var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                                var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                                SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after CharacterChangeCollection");
                                            }
                                            catch
                                            {
                                            }

                                            // After character creation/customization commits (pending-creation becomes false), the client
                                            // expects updated creation-info + hub handoff; otherwise it can stall before starting the prologue.
                                            if (!slot.PendingPersistenceCreation && cachedHubStatePayload != null)
                                            {
                                                try
                                                {
                                                    var pendingJson = "{\"PendingPersistenceCreation\":" + (slot.PendingPersistenceCreation ? "true" : "false") + ",\"DataVersionChanged\":false}";
                                                    cachedCreationInfoPayload = BuildUtf16StringPayload(pendingJson);
                                                    var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), msgNoBase + 3);
                                                    SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }

                                                // Some client flows also expect a dedicated CharacterChanged event after ChangeCharacter,
                                                // not only a full metagame snapshot.
                                                try
                                                {
                                                    var characterIdentifier = !IsNullOrWhiteSpace(slot.CharacterIdentifier)
                                                        ? slot.CharacterIdentifier
                                                        : (activeIdentityGuid.ToString() + ":" + slotIndex.ToString(CultureInfo.InvariantCulture));
                                                    var pcs = BuildPlayerCharacterSnapshotForSlot(characterIdentifier, slot.CharacterName, slot);
                                                    var serializedPcs = PCSSerializer.SerializePlayerCharacterSnapshot(pcs);
                                                    var pcsPayload = BuildUtf16StringPayload(serializedPcs);
                                                    var pcsCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 33, pcsPayload), msgNoBase + 4);
                                                    SendRawFrame(stream, peer, PrefixLength(pcsCore), "sent MetaGameplayCommunicationObject CharacterChanged after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }
                                            }

                                            // After committing a new career, proactively broadcast story progress so the client
                                            // has a concrete "Main Campaign" chapter 0 + prologue mission state.
                                            if (changed && wasPendingPersistenceCreation)
                                            {
                                                var arm = Interlocked.Increment(ref postCreateArmGeneration);
                                                var commitMsgNo = direct.Value.MsgNo;
                                                var baselineSend = Interlocked.Read(ref metaSendMessageSeen);
                                                var baselineSetState = Interlocked.Read(ref metaSetStoryMissionStateSeen);
                                                var baselineStart = Interlocked.Read(ref metaStartSingleplayerMissionSeen);

                                                ThreadPool.QueueUserWorkItem(delegate
                                                {
                                                    // Give the UI a moment to finish swapping screens.
                                                    SleepWithStop(stopEvent, 1200);
                                                    if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                                    {
                                                        return;
                                                    }

                                                    try
                                                    {
                                                        var postCreateMsgNoBase = commitMsgNo + 40;

                                                        var chapterChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.ChapterChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"NewChapterIndex\":0}";
                                                        var chapterPayload = BuildUtf16StringPayload(chapterChangeJson);
                                                        var chapterCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, chapterPayload), postCreateMsgNoBase + 1);
                                                        SendRawFrame(stream, peer, PrefixLength(chapterCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (ChapterChange 0) after career creation commit");
                                                    }
                                                    catch
                                                    {
                                                    }

                                                    try
                                                    {
                                                        var missionChangeJson = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.MissionStateChange, Cliffhanger.SRO.ServerClientCommons\",\"Storyline\":\"Main Campaign\",\"Mission\":\"1_010_Prologue\",\"NewState\":\"Available\"}";
                                                        var missionPayload = BuildUtf16StringPayload(missionChangeJson);
                                                        var missionCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 36, missionPayload), commitMsgNo + 42);
                                                        SendRawFrame(stream, peer, PrefixLength(missionCore), "sent MetaGameplayCommunicationObject StoryprogressChanged (MissionStateChange Available) after career creation commit");
                                                    }
                                                    catch
                                                    {
                                                    }

                                                    // Watchdog: if intro finishes but no mission-start messages are sent, log once.
                                                    SleepWithStop(stopEvent, 10000);
                                                    if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                                                    {
                                                        return;
                                                    }

                                                    if (Interlocked.Read(ref postCreateArmGeneration) != arm)
                                                    {
                                                        return;
                                                    }

                                                    var sendNow = Interlocked.Read(ref metaSendMessageSeen);
                                                    var setNow = Interlocked.Read(ref metaSetStoryMissionStateSeen);
                                                    var startNow = Interlocked.Read(ref metaStartSingleplayerMissionSeen);
                                                    if (sendNow <= baselineSend && setNow <= baselineSetState && startNow <= baselineStart)
                                                    {
                                                        _logger.Log(new
                                                        {
                                                            ts = RequestLogger.UtcNowIso(),
                                                            type = "post-create-watchdog",
                                                            peer = peer,
                                                            note = "No MetaGameplay SendMessage observed after career creation commit; intro/mandatory-mission flow likely not triggered.",
                                                            sendMessages = sendNow,
                                                            setStoryMissionState = setNow,
                                                            startSingleplayerMission = startNow,
                                                        });
                                                    }
                                                });
                                            }
                                        }
                                    }
                                }
                            }

                            // MetaGameplayCommunicationObject.RequestStoryHubFor(...) is callField(2) on entity 3.
                            // The client expects a hub instance (field 37) in response; unsolicited hub pushes can be ignored.
                            var isMetaGameplayRequestStoryHubFor = shared.Value.ApMsgId == 1
                                && shared.Value.EntityId == 3
                                && shared.Value.FieldId == 2;
                            if (isMetaGameplayRequestStoryHubFor && cachedHubStatePayload != null)
                            {
                                string requestedHostCharacterId;
                                Guid requestedHostAccountId;
                                string routedHubId = null;
                                string routedHubSource = string.Empty;
                                CareerSlot routedSlot = null;

                                if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                {
                                    try
                                    {
                                        routedSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                    }
                                    catch
                                    {
                                        routedSlot = null;
                                    }
                                }

                                if (TryParseRequestStoryHubForPayload(shared.Value.Data, out requestedHostCharacterId, out requestedHostAccountId))
                                {
                                    if (requestedHostAccountId != Guid.Empty && requestedHostAccountId == activeIdentityGuid)
                                    {
                                        routedHubId = routedSlot != null && !IsNullOrWhiteSpace(routedSlot.HubId)
                                            ? routedSlot.HubId
                                            : DefaultHubId;
                                        routedHubSource = "self-career";
                                    }
                                    else if (requestedHostAccountId != Guid.Empty && TryResolveHubIdForAccount(requestedHostAccountId, out routedHubId))
                                    {
                                        routedHubSource = "host-account";
                                    }
                                    else if (!IsNullOrWhiteSpace(requestedHostCharacterId) && TryResolveHubIdForCharacter(requestedHostCharacterId, out routedHubId))
                                    {
                                        routedHubSource = "host-character";
                                    }
                                }

                                if (IsNullOrWhiteSpace(routedHubId) && !IsNullOrWhiteSpace(currentHubInstanceId))
                                {
                                    routedHubId = currentHubInstanceId;
                                    routedHubSource = "current-peer";
                                }

                                if (!IsNullOrWhiteSpace(routedHubId))
                                {
                                    HubPresenceRegistry.Participant previousParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                    var routedCharacterIdentifier = ResolveHubCharacterIdentifier(
                                        routedSlot,
                                        activeIdentityGuid,
                                        activeCareerIndex,
                                        previousParticipant != null ? previousParticipant.CharacterId : null);
                                    var routedCharacterName = routedSlot != null && !IsNullOrWhiteSpace(routedSlot.CharacterName)
                                        ? routedSlot.CharacterName
                                        : activeCharacterName;

                                    var transition = TryExecutePortedHubTransition(
                                        routedHubId,
                                        activeIdentityGuid,
                                        routedCharacterIdentifier,
                                        routedCharacterName,
                                        routedSlot,
                                        currentHubInstance);
                                    if (transition != null && transition.TargetHubInstance != null)
                                    {
                                        currentHubInstance = transition.TargetHubInstance;
                                        routedHubId = currentHubInstance.HubId;
                                    }

                                    var routedX = previousParticipant != null ? previousParticipant.X : 0f;
                                    var routedY = previousParticipant != null ? previousParticipant.Y : 0f;

                                    RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                        peer,
                                        activeIdentityGuid,
                                        activeIdentityHash,
                                        activeCareerIndex,
                                        routedCharacterIdentifier,
                                        routedCharacterName,
                                        routedHubId,
                                        routedX,
                                        routedY,
                                        "request-story-hub-for");

                                    HubPresenceRegistry.Participant currentParticipant;
                                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);
                                    var previousHubId = ResolveParticipantHubId(previousParticipant, null);
                                    var currentParticipantHubId = ResolveParticipantHubId(currentParticipant, routedHubId);

                                    var shouldBroadcastAdd = previousParticipant == null
                                        || !string.Equals(previousHubId, routedHubId, StringComparison.OrdinalIgnoreCase)
                                        || !string.Equals(previousParticipant.CharacterId, routedCharacterIdentifier, StringComparison.OrdinalIgnoreCase);

                                    if (previousParticipant != null
                                        && !IsNullOrWhiteSpace(previousHubId)
                                        && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                        && !string.Equals(previousHubId, routedHubId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        BroadcastHubStateRemove(previousHubId, peer, previousParticipant.CharacterId);
                                    }

                                    if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipantHubId))
                                    {
                                        if (shouldBroadcastAdd)
                                        {
                                            ClearHubAnnouncementsForPeerHub(peer, currentParticipantHubId);
                                        }

                                        armHubReadyFallback(currentParticipantHubId, currentParticipant.CharacterId, "request-story-hub-for");

                                    }

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "hub-join-eval",
                                        path = "RequestStoryHubFor",
                                        peer = peer,
                                        previousHubId = previousHubId ?? string.Empty,
                                        previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                        currentHubId = currentParticipantHubId ?? string.Empty,
                                        currentCharacterId = currentParticipant != null ? (currentParticipant.CharacterId ?? string.Empty) : string.Empty,
                                        shouldBroadcastAdd = shouldBroadcastAdd,
                                    });

                                    var serializedSharedHubState = currentHubInstance != null
                                        ? currentHubInstance.SerializedHubState()
                                        : BuildSerializedSharedHubStateOrFallback(routedHubId, routedCharacterIdentifier, routedCharacterName, routedSlot);
                                    cachedHubStatePayload = BuildMetaHubPushPayload(HubEntityId, serializedSharedHubState);
                                    currentHubInstanceId = routedHubId;

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "hub-route-storyhubfor",
                                        peer = peer,
                                        hostCharacterId = requestedHostCharacterId ?? string.Empty,
                                        hostAccountId = requestedHostAccountId != Guid.Empty ? requestedHostAccountId.ToString() : string.Empty,
                                        routedHubId = routedHubId,
                                        routeSource = routedHubSource,
                                    });
                                }

                                var requestMsgNoBase = direct.Value.MsgNo + 111;

                                try
                                {
                                    if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                                    {
                                        var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), requestMsgNoBase + 1);
                                        SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient in response to RequestStoryHubFor");
                                    }
                                }
                                catch
                                {
                                }

                                if (cachedCreationInfoPayload != null)
                                {
                                    try
                                    {
                                        if (!ShouldSuppressDuplicateHubPush(peer, true, cachedCreationInfoPayload))
                                        {
                                            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), requestMsgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged in response to RequestStoryHubFor");
                                        }
                                    }
                                    catch
                                    {
                                    }
                                }

                            }

                            if (isMetaGameplayMessage)
                            {
                                var rawMessage = payloadStrings[0];

                                // Diagnostics: decode MetaGameplayCommunicationObject.SendMessage(...) payloads.
                                if (isMetaGameplayWrappedMessage && rawMessage != null)
                                {
                                    Interlocked.Increment(ref metaSendMessageSeen);

                                    string messageType = null;
                                    try
                                    {
                                        var msgDict = TryDeserializeJsonDict(rawMessage);
                                        var content = msgDict != null ? (msgDict.Contains("Content") ? msgDict["Content"] as IDictionary : null) : null;
                                        if (content != null)
                                        {
                                            messageType = GetStringValue(content, "TypeName");
                                            if (IsNullOrWhiteSpace(messageType))
                                            {
                                                // Fallback: use the first key as a rough hint.
                                                foreach (DictionaryEntry entry in content)
                                                {
                                                    if (entry.Key != null)
                                                    {
                                                        messageType = entry.Key.ToString();
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        messageType = null;
                                    }

                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "metagameplay-sendmessage",
                                        peer = peer,
                                        messageType = messageType ?? string.Empty,
                                        preview = rawMessage.Length > 240 ? rawMessage.Substring(0, 240) : rawMessage,
                                    });

                                    if (rawMessage.IndexOf("SetStoryMissionStateMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaSetStoryMissionStateSeen);
                                    }
                                    if (rawMessage.IndexOf("StartSingleplayerMissionMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaStartSingleplayerMissionSeen);
                                    }
                                    if (rawMessage.IndexOf("RequestCurrentStorylineHubMessage", StringComparison.Ordinal) >= 0)
                                    {
                                        Interlocked.Increment(ref metaRequestHubSeen);
                                    }
                                }

                                // The client requests hub state via a wrapped message:
                                // MetaGameplayCommunicationAdapter.RequestCurrentStorylineHub() -> SendMessage(new RequestCurrentStorylineHubMessage())
                                // We must respond by pushing hub state + creation-info; otherwise the UI can get stuck waiting.
                                if (isMetaGameplayWrappedMessage
                                    && rawMessage != null
                                    && rawMessage.IndexOf("RequestCurrentStorylineHubMessage", StringComparison.Ordinal) >= 0
                                    && cachedHubStatePayload != null)
                                {
                                    if (!IsNullOrWhiteSpace(currentHubInstanceId))
                                    {
                                        HubPresenceRegistry.Participant previousParticipant;
                                        _hubPresenceRegistry.TryGetParticipantForPeer(peer, out previousParticipant);

                                        CareerSlot currentSlot = null;
                                        if (_userStore != null && !IsNullOrWhiteSpace(activeIdentityHash))
                                        {
                                            try
                                            {
                                                currentSlot = _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false);
                                            }
                                            catch
                                            {
                                                currentSlot = null;
                                            }
                                        }

                                        var currentCharacterIdentifier = ResolveHubCharacterIdentifier(
                                            currentSlot,
                                            activeIdentityGuid,
                                            activeCareerIndex,
                                            previousParticipant != null ? previousParticipant.CharacterId : null);
                                        var currentCharacterName = currentSlot != null && !IsNullOrWhiteSpace(currentSlot.CharacterName)
                                            ? currentSlot.CharacterName
                                            : activeCharacterName;

                                        var effectiveHubId = currentHubInstanceId;
                                        Guid followHostAccountId;
                                        string followHostHubId;
                                        if (PartyHubFollowRegistry.TryGetHostForMember(activeIdentityGuid, out followHostAccountId)
                                            && followHostAccountId != Guid.Empty
                                            && followHostAccountId != activeIdentityGuid
                                            && TryResolveHubIdForAccount(followHostAccountId, out followHostHubId)
                                            && !IsNullOrWhiteSpace(followHostHubId))
                                        {
                                            effectiveHubId = followHostHubId;
                                        }
                                        else if (currentSlot != null && !IsNullOrWhiteSpace(currentSlot.HubId))
                                        {
                                            effectiveHubId = currentSlot.HubId;
                                        }

                                        var transition = TryExecutePortedHubTransition(
                                            effectiveHubId,
                                            activeIdentityGuid,
                                            currentCharacterIdentifier,
                                            currentCharacterName,
                                            currentSlot,
                                            currentHubInstance);
                                        if (transition != null && transition.TargetHubInstance != null)
                                        {
                                            currentHubInstance = transition.TargetHubInstance;
                                            effectiveHubId = currentHubInstance.HubId;
                                        }

                                        currentHubInstanceId = effectiveHubId;

                                        var currentX = previousParticipant != null ? previousParticipant.X : 0f;
                                        var currentY = previousParticipant != null ? previousParticipant.Y : 0f;

                                        RegisterOrUpdateHubPresenceWithDuplicateRetire(
                                            peer,
                                            activeIdentityGuid,
                                            activeIdentityHash,
                                            activeCareerIndex,
                                            currentCharacterIdentifier,
                                            currentCharacterName,
                                            effectiveHubId,
                                            currentX,
                                            currentY,
                                            "request-current-storyline-hub");

                                        HubPresenceRegistry.Participant currentParticipant;
                                        _hubPresenceRegistry.TryGetParticipantForPeer(peer, out currentParticipant);
                                        var previousHubId = ResolveParticipantHubId(previousParticipant, null);
                                        var currentParticipantHubId = ResolveParticipantHubId(currentParticipant, effectiveHubId);

                                        var shouldBroadcastAdd = previousParticipant == null
                                            || !string.Equals(previousHubId, effectiveHubId, StringComparison.OrdinalIgnoreCase)
                                            || !string.Equals(previousParticipant.CharacterId, currentCharacterIdentifier, StringComparison.OrdinalIgnoreCase);

                                        if (previousParticipant != null
                                            && !IsNullOrWhiteSpace(previousHubId)
                                            && !IsNullOrWhiteSpace(previousParticipant.CharacterId)
                                            && !string.Equals(previousHubId, effectiveHubId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            BroadcastHubStateRemove(previousHubId, peer, previousParticipant.CharacterId);
                                        }

                                        if (currentParticipant != null && !IsNullOrWhiteSpace(currentParticipantHubId))
                                        {
                                            if (shouldBroadcastAdd)
                                            {
                                                ClearHubAnnouncementsForPeerHub(peer, currentParticipantHubId);
                                            }

                                            armHubReadyFallback(currentParticipantHubId, currentParticipant.CharacterId, "request-current-storyline-hub");

                                        }

                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "hub-join-eval",
                                            path = "RequestCurrentStorylineHubMessage",
                                            peer = peer,
                                            previousHubId = previousHubId ?? string.Empty,
                                            previousCharacterId = previousParticipant != null ? (previousParticipant.CharacterId ?? string.Empty) : string.Empty,
                                            currentHubId = currentParticipantHubId ?? string.Empty,
                                            currentCharacterId = currentParticipant != null ? (currentParticipant.CharacterId ?? string.Empty) : string.Empty,
                                            shouldBroadcastAdd = shouldBroadcastAdd,
                                        });

                                        var serializedSharedHubState = currentHubInstance != null
                                            ? currentHubInstance.SerializedHubState()
                                            : BuildSerializedSharedHubStateOrFallback(effectiveHubId, currentCharacterIdentifier, currentCharacterName, currentSlot);
                                        cachedHubStatePayload = BuildMetaHubPushPayload(HubEntityId, serializedSharedHubState);
                                    }

                                    var requestMsgNoBase = direct.Value.MsgNo + 110;

                                    if (!ShouldSuppressDuplicateHubPush(peer, false, cachedHubStatePayload))
                                    {
                                        var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), requestMsgNoBase + 1);
                                        SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient in response to RequestCurrentStorylineHubMessage");
                                    }

                                    if (cachedCreationInfoPayload != null)
                                    {
                                        if (!ShouldSuppressDuplicateHubPush(peer, true, cachedCreationInfoPayload))
                                        {
                                            var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), requestMsgNoBase + 2);
                                            SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged in response to RequestCurrentStorylineHubMessage");
                                        }
                                    }

                                }

                                // Character creation / customization sends a CharacterChangeCollection that includes
                                // NameChange: { OldName: "NewRunner", NewName: "ShadowZero" }.
                                // Persist that NewName to the active career slot so it survives restarts.
                                if (_userStore != null
                                    && rawMessage != null
                                    && (isMetaGameplayChangeCharacter || rawMessage.IndexOf("CharacterChangeCollection", StringComparison.Ordinal) >= 0))
                                {
                                    var hasAnyRelevantChange = rawMessage.IndexOf("\"NewName\"", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0
                                        || rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0;

                                    if (hasAnyRelevantChange)
                                    {
                                        var slotIndex = activeCareerIndex;
                                        if (slotIndex < 0)
                                        {
                                            slotIndex = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetLastCareerIndex(activeIdentityHash) : 0;
                                        }

                                        var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, slotIndex, false) : null;
                                        if (slot != null)
                                        {
                                            var changed = false;

                                            var newName = ExtractJsonStringValue(rawMessage, "NewName");
                                            if (!IsNullOrWhiteSpace(newName) && !string.Equals(slot.CharacterName, newName, StringComparison.Ordinal))
                                            {
                                                slot.CharacterName = newName;
                                                changed = true;
                                            }

                                            if (!slot.IsOccupied)
                                            {
                                                slot.IsOccupied = true;
                                                changed = true;
                                            }

                                            // Apply appearance changes if present.
                                            if (rawMessage.IndexOf("SkinTextureIndexChange", StringComparison.Ordinal) >= 0)
                                            {
                                                int skin;
                                                if (TryParseInt32(ExtractJsonStringValue(rawMessage, "NewIndex"), out skin))
                                                {
                                                    if (slot.SkinTextureIndex != skin)
                                                    {
                                                        slot.SkinTextureIndex = skin;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BackgroundStoryChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong story;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewStory"), out story))
                                                {
                                                    if (slot.BackgroundStory != story)
                                                    {
                                                        slot.BackgroundStory = story;
                                                        changed = true;
                                                    }
                                                }
                                            }
                                            if (rawMessage.IndexOf("BodyChange", StringComparison.Ordinal) >= 0)
                                            {
                                                ulong meta;
                                                ulong gender;
                                                if (TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewMetatype"), out meta)
                                                    && TryParseUInt64(ExtractJsonStringValue(rawMessage, "NewGender"), out gender))
                                                {
                                                    ulong bodytype;
                                                    if (TryResolveBodytypeId(meta, gender, out bodytype) && bodytype != 0UL)
                                                    {
                                                        if (slot.Bodytype != bodytype)
                                                        {
                                                            slot.Bodytype = bodytype;
                                                            changed = true;
                                                        }
                                                    }
                                                }
                                            }

                                            if (changed)
                                            {
                                                // After a successful creation/edit flow, treat it as committed.
                                                if (slot.PendingPersistenceCreation)
                                                {
                                                    slot.PendingPersistenceCreation = false;
                                                }
                                                if (!IsNullOrWhiteSpace(activeIdentityHash))
                                                {
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }

                                                if (!IsNullOrWhiteSpace(slot.CharacterName))
                                                {
                                                    activeCharacterName = slot.CharacterName;
                                                }

                                                // Refresh cached hub payload (used later when client requests hub state).
                                                string refreshedHubId;
                                                cachedHubStatePayload = BuildPortedHubStatePayloadForSlot(
                                                    slot,
                                                    activeIdentityGuid,
                                                    slotIndex,
                                                    false,
                                                    currentHubInstance,
                                                    out refreshedHubId,
                                                    out currentHubInstance);
                                                currentHubInstanceId = refreshedHubId;

                                                // Nudge client UI lists.
                                                var msgNoBase = direct.Value.MsgNo + 2;
                                                var summaryJson = BuildCareerSummaryJson(!IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetCareers(activeIdentityHash) : null);
                                                var updatePayload = BuildUtf16StringPayload(summaryJson);
                                                var updateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 2, 16, updatePayload), msgNoBase);
                                                SendRawFrame(stream, peer, PrefixLength(updateCore), "sent AccountCommunicationObject UpdateCareerSummaries after CharacterChangeCollection");

                                                // Send an updated metagame snapshot so the client doesn't revert to earlier defaults.
                                                try
                                                {
                                                    var zipped = _careerInfoGenerator.GetZippedCareerInfo(activeIdentityGuid, slotIndex, slot);
                                                    var metaSnapshotPayload = BuildUtf16StringPayload(zipped);
                                                    var metaSnapshotCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 26, metaSnapshotPayload), msgNoBase + 1);
                                                    SendRawFrame(stream, peer, PrefixLength(metaSnapshotCore), "sent MetaGameplayCommunicationObject SendMetagameplayDataSnapshotToClient after CharacterChangeCollection");
                                                }
                                                catch
                                                {
                                                }
                                            }
                                        }
                                    }
                                }

                                // Client-to-server request to update story progression.
                                // In the real game this updates the authoritative metagame state and is broadcast back.
                                // Important subtlety: the client UI updates from *server* StoryprogressChanged (MissionStateChange),
                                // not from its own outgoing SetStoryMissionStateMessage.
                                if (TryHandleSetStoryMissionStateMessage(
                                    isMetaGameplayWrappedMessage,
                                    rawMessage,
                                    direct.Value.MsgNo,
                                    stream,
                                    peer,
                                    completedStoryMissions,
                                    activeIdentityHash,
                                    activeIdentityGuid,
                                    activeCareerIndex,
                                    ref currentHubInstanceId,
                                    ref currentHubInstance,
                                    ref cachedHubStatePayload,
                                    cachedCreationInfoPayload))
                                {
                                }

                                // Persist NPC interactions so dialog/new-marker state behaves like retail on relaunch.
                                if (isMetaGameplayWrappedMessage && rawMessage != null && rawMessage.IndexOf("InteractedWithNpcMessage", StringComparison.Ordinal) >= 0)
                                {
                                    var storylineId = ExtractJsonStringValue(rawMessage, "StorylineId");
                                    var npcId = ExtractJsonStringValue(rawMessage, "NpcId");
                                    if (_userStore != null && !IsNullOrWhiteSpace(npcId) && string.Equals(storylineId, "Main Campaign", StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            var slot = !IsNullOrWhiteSpace(activeIdentityHash) ? _userStore.GetOrCreateCareer(activeIdentityHash, activeCareerIndex, false) : null;
                                            if (slot != null)
                                            {
                                                if (slot.MainCampaignInteractedNpcs == null)
                                                {
                                                    slot.MainCampaignInteractedNpcs = new List<string>();
                                                }
                                                if (!slot.MainCampaignInteractedNpcs.Contains(npcId))
                                                {
                                                    slot.MainCampaignInteractedNpcs.Add(npcId);
                                                    _userStore.UpsertCareer(activeIdentityHash, slot);
                                                }
                                            }
                                        }
                                        catch
                                        {
                                        }
                                    }

                                    // Echoing is optional; the client already updates locally before sending.
                                    var echoPayload = BuildUtf16StringPayload(rawMessage);
                                    var echoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 1, echoPayload), direct.Value.MsgNo + 4);
                                    SendRawFrame(stream, peer, PrefixLength(echoCore), "echoed MetaGameplayCommunicationObject Message (InteractedWithNpcMessage)");
                                }

                                if (isMetaGameplayWrappedMessage && rawMessage != null && rawMessage.IndexOf("StartSingleplayerMissionMessage", StringComparison.Ordinal) >= 0)
                                {
                                    var mapName = ExtractJsonStringValue(rawMessage, "MapName");
                                    if (IsNullOrWhiteSpace(mapName))
                                    {
                                        mapName = "1_010_Prologue";
                                    }

                                    var parsedSelections = TryExtractHenchmanSelections(rawMessage);

                                    currentMissionMapName = mapName;

                                    if (IsMissionCompletedForCareer(activeIdentityHash, activeCareerIndex, mapName, completedStoryMissions))
                                    {
                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "metagameplay",
                                            peer = peer,
                                            action = "start-mission-cancelled",
                                            reason = "mission-completed",
                                            mapName = mapName,
                                        });

                                        // The client is now waiting for either StartMissionAccepted or StartMissionCancelled.
                                        // If we do neither, it will remain stuck in a mission-start-in-progress state.
                                        var nudgeMsgNoBase = direct.Value.MsgNo + 250;
                                        SendMissionStartCancelledWithHubRestore(stream, peer, nudgeMsgNoBase, cachedHubStatePayload, cachedCreationInfoPayload);
                                        continue;
                                    }

                                    var requestMsgNoBase = direct.Value.MsgNo + 250;

                                    EnsureMissionEntitiesIntroduced(
                                        stream,
                                        peer,
                                        requestMsgNoBase,
                                        gameworldEntityId,
                                        missionInstanceEntityId,
                                        missionCommandEntityId,
                                        gameworldCommunicationObjectTypeId,
                                        missionInstanceCommunicationObjectTypeId,
                                        missionCommandCommunicationObjectTypeId,
                                        ref sentMissionEntityIntros);

                                    var seed0 = 0x11111111u;
                                    var seed1 = 0x22222222u;
                                    var seed2 = 0x33333333u;
                                    var seed3 = 0x44444444u;

                                    var selectedHenchmen = ResolveSelectedHenchmenForSoloMission(peer, mapName, parsedSelections, activeIdentityGuid, activeIdentityHash, activeCareerIndex);
                                    var compressedMatchConfiguration = BuildSoloMissionMatchConfiguration(mapName, activeIdentityGuid, activeIdentityHash, activeCareerIndex, activeCharacterName, gameClientEntityId, selectedHenchmen);

                                    try
                                    {
                                        simulationSession = CreateSoloMissionSimulation(peer, mapName, activeIdentityHash, activeCareerIndex, compressedMatchConfiguration, seed0, seed1, seed2, seed3);

                                        if (simulationSession != null)
                                        {
                                            MissionRuntimeRegistry.MarkSoloMissionStarted(peer);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        simulationSession = null;
                                        _logger.Log(new
                                        {
                                            ts = RequestLogger.UtcNowIso(),
                                            type = "sim",
                                            peer = peer,
                                            status = "failed",
                                            mapName = mapName,
                                            message = ex.Message,
                                        });
                                    }

                                    SendMissionStartAccepted(
                                        stream,
                                        peer,
                                        requestMsgNoBase,
                                        seed0,
                                        seed1,
                                        seed2,
                                        seed3,
                                        compressedMatchConfiguration,
                                        gameworldEntityId,
                                        missionInstanceEntityId,
                                        missionCommandEntityId,
                                        "sent MetaGameplayCommunicationObject StartMissionAccepted (map=" + mapName + ")");

                                    SendMissionStartForClients(stream, peer, stopEvent, requestMsgNoBase, missionInstanceEntityId, "sent MissionInstanceCommunicationObject StartMissionForClients");
                                }
                            }

                            var isMissionCommandCall = shared.Value.ApMsgId == 1 && shared.Value.EntityId == missionCommandEntityId;
                            if (isMissionCommandCall)
                            {
                                HandleMissionCommandCall(
                                    stream,
                                    peer,
                                    shared.Value.FieldId,
                                    shared.Value.Data,
                                    direct.Value.MsgNo,
                                    gameworldEntityId,
                                    gameClientEntityId,
                                    activeIdentityHash,
                                    activeIdentityGuid,
                                    activeCareerIndex,
                                    activeCharacterName,
                                    currentMissionMapName,
                                    completedStoryMissions,
                                    ref currentCoopGroupName,
                                    ref simulationSession,
                                    ref simulationSessionSync,
                                    ref cachedHubStatePayload,
                                    cachedCreationInfoPayload);
                            }
                        }

                        TryFlushPendingCharacterStatePushes(activeIdentityGuid, activeIdentityHash, activeCareerIndex, peer, stream);

                        var chunk = ReadChunk(stream);
                        if (chunk.Length == 0)
                        {
                            if (simulationSession != null && IsNullOrWhiteSpace(currentCoopGroupName))
                            {
                                MissionRuntimeRegistry.MarkSoloMissionEnded(peer);
                            }

                            cancelHubReadyFallback("socket-closed");
                            connectionClosed.Set();
                            HubPresenceRegistry.Participant disconnectedParticipant;
                            _hubPresenceRegistry.TryGetParticipantForPeer(peer, out disconnectedParticipant);
                            if (currentHubInstance != null && disconnectedParticipant != null && !IsNullOrWhiteSpace(disconnectedParticipant.CharacterId))
                            {
                                _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, disconnectedParticipant.CharacterId);
                                currentHubInstance = null;
                            }
                            RemoveHubPresenceWithBroadcast(peer);
                            _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "aplay-conn", peer = peer, note = "socket closed" });
                            break;
                        }

                        buffer.AddRange(chunk);
                    }

                    if (!IsNullOrWhiteSpace(currentCoopGroupName))
                    {
                        UnregisterCoopMissionParticipant(currentCoopGroupName, peer);
                    }

                    cancelHubReadyFallback("connection-teardown");
                    HubPresenceRegistry.Participant teardownParticipant;
                    _hubPresenceRegistry.TryGetParticipantForPeer(peer, out teardownParticipant);
                    if (currentHubInstance != null && teardownParticipant != null && !IsNullOrWhiteSpace(teardownParticipant.CharacterId))
                    {
                        _portedHubInstanceManager.RemoveCharacterFromHub(currentHubInstance, teardownParticipant.CharacterId);
                        currentHubInstance = null;
                    }
                    RemoveHubPresenceWithBroadcast(peer);
                    UnregisterHubPeerStream(peer, stream);
                }
            }
        }

        private void HandleHttpProbe(NetworkStream stream, string peer, byte[] first)
        {
            var firstLine = Encoding.ASCII.GetString(first).Split(new[] { "\r\n" }, 2, StringSplitOptions.None)[0];
            var advertisedServerAddress = TryGetConfiguredAPlayServerAddress() ?? string.Format("127.0.0.1:{0}", _options.APlayPort);
            var body = firstLine != null && firstLine.IndexOf("/servers", StringComparison.OrdinalIgnoreCase) >= 0
                ? Encoding.ASCII.GetBytes(advertisedServerAddress)
                : Encoding.ASCII.GetBytes("OK");

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.0 200 OK\r\n"
                + "Content-Type: text/plain; charset=utf-8\r\n"
                + "Connection: close\r\n"
                + "Content-Length: " + body.Length + "\r\n\r\n");

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-http-reply",
                requestLine = firstLine,
                body = Encoding.ASCII.GetString(body),
                contentLength = body.Length,
            });

            stream.Write(response, 0, response.Length);
            stream.Write(body, 0, body.Length);
        }

        private string TryGetConfiguredAPlayServerAddress()
        {
            // The game hits the APlay endpoint as an HTTP probe ("/servers*.txt") and expects the response
            // to contain the address it should connect to. For non-local hosting, this must match the
            // served BaseConfiguration.ServerAddress value.
            try
            {
                if (_options == null || IsNullOrWhiteSpace(_options.ConfigDir))
                {
                    return null;
                }

                var configPath = Path.Combine(_options.ConfigDir, "config.xml");
                if (!File.Exists(configPath))
                {
                    return null;
                }

                var doc = new XmlDocument();
                doc.Load(configPath);

                // config.xml doesn't currently use a default XML namespace, but be resilient anyway.
                var node = doc.SelectSingleNode("//ServerAddress") ?? doc.SelectSingleNode("//*[local-name()='ServerAddress']");
                var value = node != null ? (node.InnerText ?? string.Empty).Trim() : null;
                if (IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                // Be tolerant of accidental scheme/path additions (e.g. "http://host:5055/").
                if (value.IndexOf("://", StringComparison.Ordinal) > 0)
                {
                    Uri uri;
                    if (Uri.TryCreate(value, UriKind.Absolute, out uri) && !IsNullOrWhiteSpace(uri.Host))
                    {
                        var port = uri.IsDefaultPort ? _options.APlayPort : uri.Port;
                        return string.Format("{0}:{1}", uri.Host, port);
                    }
                }

                return value;
            }
            catch
            {
                return null;
            }
        }

        private void SendRawFrame(NetworkStream stream, string peer, byte[] decoded, string note)
        {
            var frameBytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(decoded) + "\0");
            try
            {
                lock (stream)
                {
                    stream.Write(frameBytes, 0, frameBytes.Length);
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "stream-disposed",
                });
                return;
            }
            catch (IOException ioex)
            {
                var socketErrorCode = string.Empty;
                var socketEx = ioex.InnerException as SocketException;
                if (socketEx != null)
                {
                    socketErrorCode = socketEx.SocketErrorCode.ToString();
                }

                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "io-exception",
                    message = ioex.Message,
                    socketErrorCode = socketErrorCode,
                });
                return;
            }
            catch (SocketException sex)
            {
                _logger.LogLow(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "aplay-frame-send-disconnected",
                    peer = peer,
                    note = note,
                    reason = "socket-exception",
                    message = sex.Message,
                    socketErrorCode = sex.SocketErrorCode.ToString(),
                });
                return;
            }

            var payload = new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-frame-sent",
                peer = peer,
                frame = Encoding.ASCII.GetString(frameBytes, 0, frameBytes.Length - 1),
                decodedLen = decoded.Length,
                decodedHex = ToHexString(decoded, 0, decoded.Length).ToLowerInvariant(),
                note = note,
            };

            // Keep-alives are high-frequency and drown out useful signal.
            if (note != null && note.IndexOf("KeepAlive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _logger.LogLow(payload);
            }
            else
            {
                _logger.Log(payload);
            }
        }

    }
}
