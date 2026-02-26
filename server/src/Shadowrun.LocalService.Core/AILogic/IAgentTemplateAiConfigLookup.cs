using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Optional lookup for cases where AI config is not present as a runtime component,
    /// but can be derived from static templates.
    /// </summary>
    public interface IAgentTemplateAiConfigLookup
    {
        bool TryGetConfig(IStaticData staticData, ulong agentTemplateId, out AIBehaviourConfigurationComponent config);
    }
}
