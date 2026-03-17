namespace Shadowrun.LocalService.Core.Protocols.MissionCommands
{
    internal enum MissionCommandKind
    {
        Unknown = 0,
        MissionReady = 1,
        LeaveMission = 2,
        FollowPath = 3,
        ActivateActiveSkill = 4,
    }
}