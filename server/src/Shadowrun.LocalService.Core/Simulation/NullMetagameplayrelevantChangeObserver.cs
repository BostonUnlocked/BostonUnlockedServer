using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Changes;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed class NullMetagameplayrelevantChangeObserver : IMetagameplayrelevantChangeObserver
    {
        public static readonly IMetagameplayrelevantChangeObserver Instance = new NullMetagameplayrelevantChangeObserver();

        private NullMetagameplayrelevantChangeObserver() { }

        public void PublishItemChange(Entity entity, ItemChange itemChange) { }
        public void PublishGiveItem(ulong playerId, ItemChange itemChange) { }
        public void PublishGiveCash(ulong playerId, CurrencyId currencyId, int karmaAmount) { }
        public void SubscribeItemChangeListener(IItemChangeListener listener) { }
        public void Initialize(EntitySystem entitySystem) { }
        public void PublishRollOnLootTableForPlayer(ulong playerId, string lootTable) { }
        public void PublishAllChanges() { }
        public void PublishGrantUnlock(ulong playerId, string unlockId) { }
        public void Subscribe(IUnlockChangeListener listener) { }
    }
}
