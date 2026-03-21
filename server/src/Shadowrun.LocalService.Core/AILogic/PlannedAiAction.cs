using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class PlannedAiAction
    {
        private PlannedAiAction(
            Entity agent,
            string commandName,
            IntVector2D targetPosition,
            int weaponIndex,
            int skillIndex,
            int skillId,
            AiPlanningDiagnostics diagnostics)
        {
            Agent = agent;
            CommandName = commandName;
            TargetPosition = targetPosition;
            WeaponIndex = weaponIndex;
            SkillIndex = skillIndex;
            SkillId = skillId;
            Diagnostics = diagnostics ?? new AiPlanningDiagnostics();
        }

        public Entity Agent { get; private set; }
        public string CommandName { get; private set; }
        public IntVector2D TargetPosition { get; private set; }
        public int WeaponIndex { get; private set; }
        public int SkillIndex { get; private set; }
        public int SkillId { get; private set; }
        public AiPlanningDiagnostics Diagnostics { get; private set; }

        public bool IsMove { get { return CommandName == "AI.Move"; } }
        public bool IsEndTurn { get { return CommandName == "AI.EndTeamTurn"; } }
        public bool IsSwitchToCombat { get { return CommandName == "AI.SwitchToCombat"; } }

        public static PlannedAiAction CreateMove(Entity agent, IntVector2D targetPosition, AiPlanningDiagnostics diagnostics)
        {
            return new PlannedAiAction(agent, "AI.Move", targetPosition, 0, 0, 0, diagnostics);
        }

        public static PlannedAiAction CreateSkill(Entity agent, int weaponIndex, int skillIndex, int skillId, IntVector2D targetPosition, AiPlanningDiagnostics diagnostics)
        {
            return new PlannedAiAction(agent, "AI.Decision", targetPosition, weaponIndex, skillIndex, skillId, diagnostics);
        }

        public static PlannedAiAction CreateSwitchToCombat(Entity agent, IntVector2D targetPosition)
        {
            return new PlannedAiAction(
                agent,
                "AI.SwitchToCombat",
                targetPosition,
                0,
                0,
                Simulation.ServerSimulationSession.SwitchToCombatSkillId,
                new AiPlanningDiagnostics
                {
                    DecisionNote = "switch-to-combat",
                    DebugStage = "switch-to-combat",
                    DebugResolvedActivityId = (ulong)Simulation.ServerSimulationSession.SwitchToCombatSkillId,
                });
        }

        public static PlannedAiAction CreateEndTurn(Entity agent, IntVector2D targetPosition, string decisionNote, string debugStage, int endTurnSkillId)
        {
            return CreateEndTurn(agent, targetPosition, decisionNote, debugStage, endTurnSkillId, null);
        }

        public static PlannedAiAction CreateEndTurn(Entity agent, IntVector2D targetPosition, string decisionNote, string debugStage, int endTurnSkillId, AiPlanningDiagnostics diagnostics)
        {
            var effectiveDiagnostics = diagnostics ?? new AiPlanningDiagnostics();
            effectiveDiagnostics.DecisionNote = decisionNote;
            effectiveDiagnostics.DebugStage = debugStage;

            return new PlannedAiAction(
                agent,
                "AI.EndTeamTurn",
                targetPosition,
                0,
                0,
                endTurnSkillId,
                effectiveDiagnostics);
        }
    }
}