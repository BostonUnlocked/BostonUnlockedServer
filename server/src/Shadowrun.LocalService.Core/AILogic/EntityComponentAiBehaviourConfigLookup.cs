using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Default config lookup: read AI behaviour config directly from the entity system component.
    /// </summary>
    public sealed class EntityComponentAiBehaviourConfigLookup : IAiBehaviourConfigLookup
    {
        public bool TryGetConfig(Entity entity, IGameworldInstance gameworld, out AIBehaviourConfigurationComponent config)
        {
            config = null;

            if (gameworld == null || gameworld.EntitySystem == null)
            {
                return false;
            }

            AIBehaviourConfigurationComponent found;
            if (gameworld.EntitySystem.TryGetComponent<AIBehaviourConfigurationComponent>(entity, out found) && found != null)
            {
                config = found;
                return true;
            }

            return false;
        }
    }
}
