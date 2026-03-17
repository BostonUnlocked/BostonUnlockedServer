using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Definitions.LootConditions;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class LocalMissionLootController : IMissionLootController
    {
        private sealed class UnlockContainerAdapter : IUnlockContainer
        {
            private readonly UnlockContainer _inner;

            public UnlockContainerAdapter(UnlockContainer inner)
            {
                _inner = inner;
            }

            public bool IsUnlocked(string unlock)
            {
                return _inner == null || _inner.IsUnlocked(unlock);
            }
        }

        private sealed class LootEvent
        {
            public int Sequence;
            public string LootTable;
            public DateTime CurrentTimeUtc;
        }

        internal sealed class LootGrant
        {
            public string LootTable;
            public string ItemId;
            public int Delta;
            public int Quality;
            public int Flavour;
            public int SellPrice;
            public int Nuyen;
        }

        private readonly IStaticData _staticData;
        private readonly uint _seed0;
        private readonly uint _seed1;
        private readonly uint _seed2;
        private readonly uint _seed3;
        private readonly string _storyLine;
        private readonly int _chapter;
        private readonly Action<string> _onGiveLootCalled;

        private readonly object _lock = new object();
        private readonly List<LootEvent> _events = new List<LootEvent>();
        private readonly Dictionary<string, int> _previewNextSequenceByParticipant = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private int _nextSequence;

        public LocalMissionLootController(IStaticData staticData, uint seed0, uint seed1, uint seed2, uint seed3, string storyLine, int chapter, Action<string> onGiveLootCalled)
        {
            _staticData = staticData;
            _seed0 = seed0;
            _seed1 = seed1;
            _seed2 = seed2;
            _seed3 = seed3;
            _storyLine = !string.IsNullOrEmpty(storyLine) ? storyLine : "Main Campaign";
            _chapter = chapter;
            _onGiveLootCalled = onGiveLootCalled;
        }

        public void GiveLoot(EntitySystem entitySystem, string lootTable)
        {
            if (_onGiveLootCalled != null)
            {
                try
                {
                    _onGiveLootCalled(lootTable);
                }
                catch
                {
                }
            }

            if (_staticData == null || _staticData.ServerData == null || _staticData.ServerData.LootTables == null)
            {
                return;
            }

            if (string.IsNullOrEmpty(lootTable))
            {
                return;
            }

            lock (_lock)
            {
                _events.Add(new LootEvent
                {
                    Sequence = _nextSequence++,
                    LootTable = lootTable,
                    CurrentTimeUtc = DateTime.UtcNow,
                });
            }
        }

        internal LootGrant[] ResolveLootForParticipant(string participantKey, LocalUserStore userStore, Guid identityGuid, CareerSlot slot)
        {
            return ResolveLootForParticipant(participantKey, userStore, identityGuid, slot, false);
        }

        internal LootGrant[] ResolvePreviewLootForParticipant(string participantKey, LocalUserStore userStore, Guid identityGuid, CareerSlot slot)
        {
            return ResolveLootForParticipant(participantKey, userStore, identityGuid, slot, true);
        }

        private LootGrant[] ResolveLootForParticipant(string participantKey, LocalUserStore userStore, Guid identityGuid, CareerSlot slot, bool previewOnly)
        {
            if (_staticData == null || _staticData.ServerData == null || _staticData.ServerData.LootTables == null || _staticData.MetagameplayData == null)
            {
                return new LootGrant[0];
            }

            participantKey = NormalizeParticipantKey(participantKey, identityGuid, slot);

            LootEvent[] eventsSnapshot;
            int previewNextSequence = 0;
            lock (_lock)
            {
                if (_events.Count == 0)
                {
                    return new LootGrant[0];
                }

                eventsSnapshot = _events.ToArray();

                if (previewOnly)
                {
                    _previewNextSequenceByParticipant.TryGetValue(participantKey, out previewNextSequence);
                }
            }

            var chapter = slot != null && slot.MainCampaignCurrentChapter > 0 ? slot.MainCampaignCurrentChapter : _chapter;
            var inventory = LocalMetagameplaySnapshotFactory.BuildInventoryFromSlot(slot);
            var unlocks = LocalMetagameplaySnapshotFactory.BuildUnlockContainer(userStore, identityGuid, slot);
            var unlockContainer = new UnlockContainerAdapter(unlocks);
            var storyProgress = LocalMetagameplaySnapshotFactory.BuildStoryProgress(slot);
            var flavourData = _staticData.ItemFlavourData ?? new ItemFlavourData();
            var grants = new List<LootGrant>();
            var maxResolvedSequence = previewNextSequence;

            for (var i = 0; i < eventsSnapshot.Length; i++)
            {
                var lootEvent = eventsSnapshot[i];
                if (lootEvent == null || string.IsNullOrEmpty(lootEvent.LootTable))
                {
                    continue;
                }

                if (previewOnly && lootEvent.Sequence < previewNextSequence)
                {
                    continue;
                }

                try
                {
                    var random = CreateParticipantRandom(participantKey, lootEvent.Sequence, lootEvent.LootTable);
                    var augmentationFactory = new ItemAugmentationFactory(_staticData.MetagameplayData, flavourData, random);
                    var parameters = new LootConditionParameters();
                    parameters.StoryLine = _storyLine;
                    parameters.Chapter = chapter;
                    parameters.VirtualChapter = 0;
                    parameters.CurrentTime = lootEvent.CurrentTimeUtc;
                    parameters.Unlocks = unlockContainer;
                    parameters.StoryProgressRuntimestate = storyProgress;

                    var rolled = RollLootProcessor.RollLoot(parameters, _staticData.ServerData.LootTables, random, _staticData.MetagameplayData, augmentationFactory, lootEvent.LootTable, inventory, null);
                    if (rolled == null || string.IsNullOrEmpty(rolled.ItemDefintionId) || rolled.Delta <= 0)
                    {
                        continue;
                    }

                    var definition = _staticData.MetagameplayData.GetDefinitionForItemId(rolled.ItemDefintionId);
                    var sellPrice = definition != null ? definition.SellPrice : 0;
                    var nuyen = 0;
                    if (sellPrice > 0)
                    {
                        try
                        {
                            checked
                            {
                                nuyen = sellPrice * rolled.Delta;
                            }
                        }
                        catch
                        {
                            nuyen = int.MaxValue;
                        }
                    }

                    grants.Add(new LootGrant
                    {
                        LootTable = lootEvent.LootTable,
                        ItemId = rolled.ItemDefintionId,
                        Delta = rolled.Delta,
                        Quality = rolled.Quality,
                        Flavour = rolled.Flavour,
                        SellPrice = sellPrice,
                        Nuyen = nuyen,
                    });

                    if (lootEvent.Sequence >= maxResolvedSequence)
                    {
                        maxResolvedSequence = lootEvent.Sequence + 1;
                    }
                }
                catch
                {
                }
            }

            if (previewOnly)
            {
                lock (_lock)
                {
                    if (maxResolvedSequence > previewNextSequence)
                    {
                        _previewNextSequenceByParticipant[participantKey] = maxResolvedSequence;
                    }
                }
            }

            return grants.ToArray();
        }

        private static string NormalizeParticipantKey(string participantKey, Guid identityGuid, CareerSlot slot)
        {
            if (!string.IsNullOrEmpty(participantKey))
            {
                return participantKey;
            }

            if (identityGuid != Guid.Empty)
            {
                return identityGuid.ToString("D", CultureInfo.InvariantCulture);
            }

            return slot != null && !string.IsNullOrEmpty(slot.CharacterName) ? slot.CharacterName : string.Empty;
        }

        private XORShiftRandomNumberGenerator CreateParticipantRandom(string participantKey, int eventSequence, string lootTable)
        {
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(_seed0));
            payload.AddRange(BitConverter.GetBytes(_seed1));
            payload.AddRange(BitConverter.GetBytes(_seed2));
            payload.AddRange(BitConverter.GetBytes(_seed3));
            payload.AddRange(BitConverter.GetBytes(eventSequence));
            payload.AddRange(Encoding.UTF8.GetBytes(participantKey ?? string.Empty));
            payload.Add(0);
            payload.AddRange(Encoding.UTF8.GetBytes(lootTable ?? string.Empty));

            var seedA = MakeSeed(payload.ToArray(), 0x9E3779B9u);
            var seedB = MakeSeed(Concat(payload, 0xA5A5A5A5u), 0x85EBCA6Bu);
            var seedC = MakeSeed(Concat(payload, 0xC2B2AE35u), 0x27D4EB2Fu);
            var seedD = MakeSeed(Concat(payload, 0x165667B1u), 0x6C8E9CF5u);
            return new XORShiftRandomNumberGenerator(seedA, seedB, seedC, seedD);
        }

        private static byte[] Concat(List<byte> payload, uint salt)
        {
            var buffer = new byte[payload.Count + 4];
            payload.CopyTo(buffer, 0);
            Array.Copy(BitConverter.GetBytes(salt), 0, buffer, payload.Count, 4);
            return buffer;
        }

        private static uint MakeSeed(byte[] payload, uint fallback)
        {
            var hash = ComputeFnv1a64(payload);
            var seed = (uint)(hash ^ (hash >> 32));
            if (seed == 0u)
            {
                seed = fallback;
            }

            return seed;
        }

        private static ulong ComputeFnv1a64(byte[] data)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            if (data == null)
            {
                return hash;
            }

            for (var i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= prime;
            }

            return hash;
        }
    }
}
