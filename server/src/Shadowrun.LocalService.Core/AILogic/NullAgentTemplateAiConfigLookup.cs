using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Null implementation used when no template-level AI config lookup is available.
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
