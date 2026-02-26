using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Minimal representation of an AI decision for a single activatable agent.
    /// Placeholder: extend as we implement real AI (movement, targeting, skill selection).
    /// </summary>
    public sealed class AiDecision
    {
        public AiDecision(Entity agent)
        {
            Agent = agent;
            TargetPosition = IntVector2D.Zero;
            SkillId = 0UL;
            HasAction = false;

            WeaponIndex = null;
            SkillIndex = null;

            IsMove = false;
            MoveTargetPosition = IntVector2D.Zero;

            DebugStage = null;
            DebugRotationType = null;
            DebugRotationCount = null;
            DebugRawSelection = 0UL;
            DebugResolvedActivityId = 0UL;
            DebugHasAiConfig = false;
            DebugHasLoadout = false;
            DebugSelectedWeaponIndex = null;
            DebugSelectedWeaponSkillCount = null;

            DebugPreferredEnemyId = null;
            DebugChosenEnemyId = null;
            DebugChosenEnemyTeamId = null;
            DebugChosenEnemyTeamAi = null;
            DebugChosenEnemyControlPlayerId = null;
            DebugChosenEnemyControlAi = null;
            DebugChosenEnemyIsPlayersPlayerCharacter = null;
            DebugChosenEnemyInteractiveObject = null;
            DebugEnemyPick = null;
            DebugEnemyReason = null;
            DebugEnemyX = null;
            DebugEnemyY = null;
            DebugEnemyCandidateCount = null;
            DebugReachableCellCount = null;
            DebugReducingCellCount = null;
            DebugAvoidedImmediateBacktrack = null;
            DebugCurrentDistToEnemy = null;
            DebugChosenMoveDistToEnemy = null;
            DebugChosenMoveDefensiveCover = null;
            DebugChosenMoveTargetCover = null;
            DebugChosenMoveScore = null;
            DebugChosenMoveChanceToHit = null;
            DebugChosenMoveWithinWalkRange = null;
            DebugProfileRange = null;
            DebugShotDistanceToTarget = null;
            DebugShotChanceToHit = null;
        }

        public Entity Agent { get; private set; }

        /// <summary>
        /// True when the engine decided to do something other than "end turn".
        /// </summary>
        public bool HasAction { get; set; }

        /// <summary>
        /// Skill/activity id to execute (if any).
        /// </summary>
        public ulong SkillId { get; set; }

        /// <summary>
        /// Weapon index used by the activity system (ammo costs, etc.).
        /// </summary>
        public int? WeaponIndex { get; set; }

        /// <summary>
        /// Skill index used by the activity system (ammo costs, etc.).
        /// </summary>
        public int? SkillIndex { get; set; }

        // Debug-only fields to make AI log output actionable during development.
        public string DebugStage { get; set; }
        public string DebugRotationType { get; set; }
        public int? DebugRotationCount { get; set; }
        public ulong DebugRawSelection { get; set; }
        public ulong DebugResolvedActivityId { get; set; }
        public bool DebugHasAiConfig { get; set; }
        public bool DebugHasLoadout { get; set; }
        public int? DebugSelectedWeaponIndex { get; set; }
        public int? DebugSelectedWeaponSkillCount { get; set; }

        // Debug: nearest-enemy selection / movement heuristics.
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

        /// <summary>
        /// Target position for position-targeted commands (if applicable).
        /// </summary>
        public IntVector2D TargetPosition { get; set; }

        /// <summary>
        /// Optional target entity.
        /// </summary>
        public Entity TargetEntity { get; set; }

        /// <summary>
        /// When true, the simulation should issue a FollowPath command to MoveTargetPosition.
        /// </summary>
        public bool IsMove { get; set; }

        public IntVector2D MoveTargetPosition { get; set; }
    }
}
