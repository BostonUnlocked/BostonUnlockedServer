namespace Shadowrun.LocalService.Core.AILogic
{
    public sealed class AiReasonCount
    {
        public string Reason { get; set; }
        public int Count { get; set; }
    }

    public sealed class AiAttackTargetScanEntry
    {
        public int? EntityId { get; set; }
        public int? TeamId { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public string Relationship { get; set; }
        public bool? InteractiveObject { get; set; }
        public bool? IsPlayersPlayerCharacter { get; set; }
        public bool? HasDetection { get; set; }
        public bool? HasStatus { get; set; }
        public bool? IsDeadOrDespawned { get; set; }
        public float? DistanceToAttacker { get; set; }
        public int? CandidateX { get; set; }
        public int? CandidateY { get; set; }
        public string RejectionReason { get; set; }
        public bool? DryRunSkillSuccessful { get; set; }
        public int? DryRunTargetWorkspaceCount { get; set; }
        public bool? ChosenTarget { get; set; }
    }

    public sealed class AiPlanningDiagnostics
    {
        public string DecisionNote { get; set; }
        public string DebugStage { get; set; }
        public long? DebugDecisionSequence { get; set; }
        public long? DebugDecisionParentSequence { get; set; }
        public string DebugDecisionPhase { get; set; }
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
        public string DebugChosenTargetRelationship { get; set; }
        public string DebugEnemyPick { get; set; }
        public string DebugEnemyReason { get; set; }
        public int? DebugEnemyX { get; set; }
        public int? DebugEnemyY { get; set; }
        public int? DebugEnemyCandidateCount { get; set; }
        public int? DebugAttackCandidateCount { get; set; }
        public int? DebugAttackEvaluatedTargetCount { get; set; }
        public bool? DebugAttackUsedSelfTarget { get; set; }
        public string DebugAttackFailureReason { get; set; }
        public int? DebugAttackDetailCount { get; set; }
        public AiReasonCount[] DebugAttackRejectionCounts { get; set; }
        public AiAttackTargetScanEntry[] DebugAttackTargetDetails { get; set; }
        public int? DebugReachableCellCount { get; set; }
        public int? DebugReducingCellCount { get; set; }
        public bool? DebugAvoidedImmediateBacktrack { get; set; }
        public int? DebugMoveSourceX { get; set; }
        public int? DebugMoveSourceY { get; set; }
        public float? DebugMoveBaselineScore { get; set; }
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

    }
}