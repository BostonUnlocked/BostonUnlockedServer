namespace Shadowrun.LocalService.Core.Protocols.MissionCommands
{
    internal sealed class ParsedMissionCommandRequest
    {
        public ParsedMissionCommandRequest(
            MissionCommandKind kind,
            ushort gameClientRefType,
            ulong gameClientRefId,
            int? weaponIndex,
            int? skillIndex,
            int? skillId,
            int? agentId,
            int? targetX,
            int? targetY)
        {
            Kind = kind;
            GameClientRefType = gameClientRefType;
            GameClientRefId = gameClientRefId;
            WeaponIndex = weaponIndex;
            SkillIndex = skillIndex;
            SkillId = skillId;
            AgentId = agentId;
            TargetX = targetX;
            TargetY = targetY;
        }

        public MissionCommandKind Kind { get; private set; }

        public ushort GameClientRefType { get; private set; }

        public ulong GameClientRefId { get; private set; }

        public int? WeaponIndex { get; private set; }

        public int? SkillIndex { get; private set; }

        public int? SkillId { get; private set; }

        public int? AgentId { get; private set; }

        public int? TargetX { get; private set; }

        public int? TargetY { get; private set; }
    }
}