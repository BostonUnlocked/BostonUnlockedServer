using System;
using Cliffhanger.SRO.ServerClientCommons.Definitions;
using Cliffhanger.SRO.ServerClientCommons.Definitions.LootConditions;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;

namespace Shadowrun.LocalService.Core.Simulation
{
    internal sealed class LocalMissionLootController : IMissionLootController
    {
        internal sealed class LootGrant
        {
            public string LootTable;
            public string ItemId;
            public int Delta;
            public int SellPrice;
            public int Nuyen;
        }

        private readonly IStaticData _staticData;
        private readonly IRandomNumberGenerator _random;
        private readonly string _storyLine;
        private readonly int _chapter;
        private readonly Action<LootGrant> _onGrant;
        private readonly Action<string> _onGiveLootCalled;

        private readonly object _lock = new object();
        private readonly System.Collections.Generic.List<LootGrant> _pending = new System.Collections.Generic.List<LootGrant>();

        public LocalMissionLootController(IStaticData staticData, IRandomNumberGenerator random, string storyLine, int chapter, Action<LootGrant> onGrant, Action<string> onGiveLootCalled)
        {
            _staticData = staticData;
            _random = random;
            _storyLine = !string.IsNullOrEmpty(storyLine) ? storyLine : "Main Campaign";
            _chapter = chapter;
            _onGrant = onGrant;
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

            try
            {
                var parameters = new LootConditionParameters();
                parameters.StoryLine = _storyLine;
                parameters.Chapter = _chapter;
                parameters.VirtualChapter = 0;
                parameters.CurrentTime = DateTime.UtcNow;

                var inventory = new Inventory();
                var flavourData = _staticData.ItemFlavourData ?? new ItemFlavourData();
                var augmentationFactory = new ItemAugmentationFactory(_staticData.MetagameplayData, flavourData, _random);

                ItemChange rolled = RollLootProcessor.RollLoot(parameters, _staticData.ServerData.LootTables, _random, _staticData.MetagameplayData, augmentationFactory, lootTable, inventory, null);
                if (rolled == null || string.IsNullOrEmpty(rolled.ItemDefintionId) || rolled.Delta <= 0)
                {
                    return;
                }

                var definition = _staticData.MetagameplayData != null ? _staticData.MetagameplayData.GetDefinitionForItemId(rolled.ItemDefintionId) : null;
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

                if (_onGrant != null)
                {
                    _onGrant(new LootGrant
                    {
                        LootTable = lootTable,
                        ItemId = rolled.ItemDefintionId,
                        Delta = rolled.Delta,
                        SellPrice = sellPrice,
                        Nuyen = nuyen,
                    });
                }

                lock (_lock)
                {
                    _pending.Add(new LootGrant
                    {
                        LootTable = lootTable,
                        ItemId = rolled.ItemDefintionId,
                        Delta = rolled.Delta,
                        SellPrice = sellPrice,
                        Nuyen = nuyen,
                    });
                }
            }
            catch
            {
            }
        }

        internal LootGrant[] DrainPending()
        {
            lock (_lock)
            {
                if (_pending.Count == 0)
                {
                    return new LootGrant[0];
                }

                var copy = _pending.ToArray();
                _pending.Clear();
                return copy;
            }
        }
    }
}
