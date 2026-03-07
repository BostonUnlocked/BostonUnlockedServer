namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class AiPlanningDiagnostics
    {
        public string DecisionNote { get; set; }
        public string DebugStage { get; set; }
        public string DebugRotationType { get; set; }
        public int? DebugRotationCount { get; set; }
        public ulong DebugRawSelection { get; set; }
        public ulong DebugResolvedActivityId { get; set; }
        public bool? DebugHasAiConfig { get; set; }
        public bool? DebugHasLoadout { get; set; }
        public int? DebugSelectedWeaponIndex { get; set; }
        public int? DebugSelectedWeaponSkillCount { get; set; }
        public int? DebugPreferredEnemyId { get; set; }
        public int? DebugChosenEnemyId { get; set; }
        public int? DebugChosenEnemyTeamId { get; set; }
        public bool? DebugChosenEnemyTeamAi { get; set; }
        public ulong? DebugChosenEnemyControlPlayerId { get; set; }
        public bool? DebugChosenEnemyControlAi { get; set; }
        public bool? DebugChosenEnemyIsPlayersPlayerCharacter { get; set; }
        public bool? DebugChosenEnemyInteractiveObject { get; set; }
        public string DebugEnemyPick { get; set; }
        public string DebugEnemyReason { get; set; }
        public int? DebugEnemyX { get; set; }
        public int? DebugEnemyY { get; set; }
        public int? DebugEnemyCandidateCount { get; set; }
        public int? DebugReachableCellCount { get; set; }
        public int? DebugReducingCellCount { get; set; }
        public bool? DebugAvoidedImmediateBacktrack { get; set; }
        public int? DebugCurrentDistToEnemy { get; set; }
        public int? DebugChosenMoveDistToEnemy { get; set; }
        public float? DebugChosenMoveDefensiveCover { get; set; }
        public float? DebugChosenMoveTargetCover { get; set; }
        public float? DebugChosenMoveScore { get; set; }
        public float? DebugChosenMoveChanceToHit { get; set; }
        public bool? DebugChosenMoveWithinWalkRange { get; set; }
        public int? DebugProfileRange { get; set; }
        public int? DebugShotDistanceToTarget { get; set; }
        public float? DebugShotChanceToHit { get; set; }
        public string InactiveSpawnManagerTag { get; set; }
        public bool ForceEndTurnForInactiveGroup { get; set; }

        public static AiPlanningDiagnostics FromDecision(AiDecision decision, string decisionNote)
        {
            var diagnostics = new AiPlanningDiagnostics();
            diagnostics.DecisionNote = decisionNote;
            if (decision == null)
            {
                return diagnostics;
            }

            diagnostics.DebugStage = decision.DebugStage;
            diagnostics.DebugRotationType = decision.DebugRotationType;
            diagnostics.DebugRotationCount = decision.DebugRotationCount;
            diagnostics.DebugRawSelection = decision.DebugRawSelection;
            diagnostics.DebugResolvedActivityId = decision.DebugResolvedActivityId;
            diagnostics.DebugHasAiConfig = decision.DebugHasAiConfig;
            diagnostics.DebugHasLoadout = decision.DebugHasLoadout;
            diagnostics.DebugSelectedWeaponIndex = decision.DebugSelectedWeaponIndex;
            diagnostics.DebugSelectedWeaponSkillCount = decision.DebugSelectedWeaponSkillCount;
            diagnostics.DebugPreferredEnemyId = decision.DebugPreferredEnemyId;
            diagnostics.DebugChosenEnemyId = decision.DebugChosenEnemyId;
            diagnostics.DebugChosenEnemyTeamId = decision.DebugChosenEnemyTeamId;
            diagnostics.DebugChosenEnemyTeamAi = decision.DebugChosenEnemyTeamAi;
            diagnostics.DebugChosenEnemyControlPlayerId = decision.DebugChosenEnemyControlPlayerId;
            diagnostics.DebugChosenEnemyControlAi = decision.DebugChosenEnemyControlAi;
            diagnostics.DebugChosenEnemyIsPlayersPlayerCharacter = decision.DebugChosenEnemyIsPlayersPlayerCharacter;
            diagnostics.DebugChosenEnemyInteractiveObject = decision.DebugChosenEnemyInteractiveObject;
            diagnostics.DebugEnemyPick = decision.DebugEnemyPick;
            diagnostics.DebugEnemyReason = decision.DebugEnemyReason;
            diagnostics.DebugEnemyX = decision.DebugEnemyX;
            diagnostics.DebugEnemyY = decision.DebugEnemyY;
            diagnostics.DebugEnemyCandidateCount = decision.DebugEnemyCandidateCount;
            diagnostics.DebugReachableCellCount = decision.DebugReachableCellCount;
            diagnostics.DebugReducingCellCount = decision.DebugReducingCellCount;
            diagnostics.DebugAvoidedImmediateBacktrack = decision.DebugAvoidedImmediateBacktrack;
            diagnostics.DebugCurrentDistToEnemy = decision.DebugCurrentDistToEnemy;
            diagnostics.DebugChosenMoveDistToEnemy = decision.DebugChosenMoveDistToEnemy;
            diagnostics.DebugChosenMoveDefensiveCover = decision.DebugChosenMoveDefensiveCover;
            diagnostics.DebugChosenMoveTargetCover = decision.DebugChosenMoveTargetCover;
            diagnostics.DebugChosenMoveScore = decision.DebugChosenMoveScore;
            diagnostics.DebugChosenMoveChanceToHit = decision.DebugChosenMoveChanceToHit;
            diagnostics.DebugChosenMoveWithinWalkRange = decision.DebugChosenMoveWithinWalkRange;
            diagnostics.DebugProfileRange = decision.DebugProfileRange;
            diagnostics.DebugShotDistanceToTarget = decision.DebugShotDistanceToTarget;
            diagnostics.DebugShotChanceToHit = decision.DebugShotChanceToHit;
            return diagnostics;
        }
    }
}