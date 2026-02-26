using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Resolves which AI behaviour configuration applies to a given runtime entity.
    /// In the shipped game this is typically an entity component populated from static-data (agent.json).
    /// </summary>
    public interface IAiBehaviourConfigLookup
    {
        bool TryGetConfig(Entity entity, IGameworldInstance gameworld, out AIBehaviourConfigurationComponent config);
    }
}
