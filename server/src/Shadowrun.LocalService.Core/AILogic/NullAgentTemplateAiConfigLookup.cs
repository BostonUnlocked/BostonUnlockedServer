using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Placeholder implementation. We currently expect AI configs to be present on spawned entities.
    /// </summary>
    public sealed class NullAgentTemplateAiConfigLookup : IAgentTemplateAiConfigLookup
    {
        public bool TryGetConfig(IStaticData staticData, ulong agentTemplateId, out AIBehaviourConfigurationComponent config)
        {
            config = null;
            return false;
        }
    }
}
