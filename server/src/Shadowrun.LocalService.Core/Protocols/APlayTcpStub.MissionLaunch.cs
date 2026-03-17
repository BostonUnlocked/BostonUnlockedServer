using System;
using System.Net.Sockets;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private void EnsureMissionEntitiesIntroduced(
            NetworkStream stream,
            string peer,
            ulong requestMsgNoBase,
            ulong gameworldEntityId,
            ulong missionInstanceEntityId,
            ulong missionCommandEntityId,
            ushort gameworldCommunicationObjectTypeId,
            ushort missionInstanceCommunicationObjectTypeId,
            ushort missionCommandCommunicationObjectTypeId,
            ref bool sentMissionEntityIntros)
        {
            if (sentMissionEntityIntros)
            {
                return;
            }

            var gameworldIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes(gameworldEntityId), BitConverter.GetBytes(gameworldCommunicationObjectTypeId), BitConverter.GetBytes(0));
            var gameworldIntroCore = BuildCoreDirectSystem(1, gameworldIntroRaw, requestMsgNoBase + 1);
            SendRawFrame(stream, peer, PrefixLength(gameworldIntroCore), "sent AP introduce shared entity (type=7 gameworld communication object, id=5)");

            var missionInstanceIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes(missionInstanceEntityId), BitConverter.GetBytes(missionInstanceCommunicationObjectTypeId), BitConverter.GetBytes(0));
            var missionInstanceIntroCore = BuildCoreDirectSystem(1, missionInstanceIntroRaw, requestMsgNoBase + 2);
            SendRawFrame(stream, peer, PrefixLength(missionInstanceIntroCore), "sent AP introduce shared entity (type=9 mission instance communication object, id=6)");

            var missionCommandIntroRaw = Concat(new byte[] { 3 }, BitConverter.GetBytes(missionCommandEntityId), BitConverter.GetBytes(missionCommandCommunicationObjectTypeId), BitConverter.GetBytes(0));
            var missionCommandIntroCore = BuildCoreDirectSystem(1, missionCommandIntroRaw, requestMsgNoBase + 3);
            SendRawFrame(stream, peer, PrefixLength(missionCommandIntroCore), "sent AP introduce shared entity (type=10 mission command communication object, id=7)");

            var gameworldOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(gameworldEntityId, gameworldCommunicationObjectTypeId), requestMsgNoBase + 4);
            SendRawFrame(stream, peer, PrefixLength(gameworldOwnerCore), "sent AP shared-entity set-owner (entity=5)");

            var missionInstanceOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionInstanceEntityId, missionInstanceCommunicationObjectTypeId), requestMsgNoBase + 5);
            SendRawFrame(stream, peer, PrefixLength(missionInstanceOwnerCore), "sent AP shared-entity set-owner (entity=6)");

            var missionCommandOwnerCore = BuildCoreDirectSystem(1, BuildApSharedEntitySetOwner(missionCommandEntityId, missionCommandCommunicationObjectTypeId), requestMsgNoBase + 6);
            SendRawFrame(stream, peer, PrefixLength(missionCommandOwnerCore), "sent AP shared-entity set-owner (entity=7)");

            sentMissionEntityIntros = true;
        }

        private void SendMissionStartCancelled(NetworkStream stream, string peer, ulong requestMsgNoBase, string note)
        {
            var cancelledCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 24, new byte[0]), requestMsgNoBase);
            SendRawFrame(stream, peer, PrefixLength(cancelledCore), note);
        }

        private void SendMissionStartCancelledWithHubRestore(
            NetworkStream stream,
            string peer,
            ulong requestMsgNoBase,
            byte[] cachedHubStatePayload,
            byte[] cachedCreationInfoPayload)
        {
            SendMissionStartCancelled(stream, peer, requestMsgNoBase, "sent MetaGameplayCommunicationObject StartMissionCancelled (mission already completed)");

            if (cachedHubStatePayload != null && cachedCreationInfoPayload != null)
            {
                var hubStateCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 37, cachedHubStatePayload), requestMsgNoBase + 1);
                SendRawFrame(stream, peer, PrefixLength(hubStateCore), "sent MetaGameplayCommunicationObject SendHubCommunicationObjectToClient (mission already completed)");

                var creationInfoCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 38, cachedCreationInfoPayload), requestMsgNoBase + 2);
                SendRawFrame(stream, peer, PrefixLength(creationInfoCore), "sent MetaGameplayCommunicationObject CreationInfoChanged (mission already completed)");
            }
        }

        private byte[] BuildMissionStartAcceptedPayload(
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3,
            string compressedMatchConfiguration,
            ulong gameworldEntityId,
            ulong missionInstanceEntityId,
            ulong missionCommandEntityId)
        {
            return Concat(
                BitConverter.GetBytes(1L),
                BitConverter.GetBytes(seed0),
                BitConverter.GetBytes(seed1),
                BitConverter.GetBytes(seed2),
                BitConverter.GetBytes(seed3),
                BuildUtf16StringPayload(compressedMatchConfiguration),
                BitConverter.GetBytes(gameworldEntityId),
                BitConverter.GetBytes(missionInstanceEntityId),
                BitConverter.GetBytes(missionCommandEntityId));
        }

        private void SendMissionStartAccepted(
            NetworkStream stream,
            string peer,
            ulong requestMsgNoBase,
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3,
            string compressedMatchConfiguration,
            ulong gameworldEntityId,
            ulong missionInstanceEntityId,
            ulong missionCommandEntityId,
            string note)
        {
            var startMissionAcceptedPayload = BuildMissionStartAcceptedPayload(
                seed0,
                seed1,
                seed2,
                seed3,
                compressedMatchConfiguration,
                gameworldEntityId,
                missionInstanceEntityId,
                missionCommandEntityId);

            var startMissionAcceptedCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, 3, 23, startMissionAcceptedPayload), requestMsgNoBase + 7);
            SendRawFrame(stream, peer, PrefixLength(startMissionAcceptedCore), note);
        }

        private byte[] BuildMissionStartForClientsFrame(ulong requestMsgNoBase, ulong missionInstanceEntityId)
        {
            var startMissionForClientsCore = BuildCoreDirectSystem(1, BuildApSharedFieldEvent(5, missionInstanceEntityId, 0, new byte[0]), requestMsgNoBase + 8);
            return PrefixLength(startMissionForClientsCore);
        }

        private void SendMissionStartForClients(
            NetworkStream stream,
            string peer,
            ulong requestMsgNoBase,
            ulong missionInstanceEntityId,
            string note)
        {
            SendRawFrame(stream, peer, BuildMissionStartForClientsFrame(requestMsgNoBase, missionInstanceEntityId), note);
        }
    }
}
