using System;
using System.Threading;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private ulong AllocateGameClientEntityId()
        {
            var next = Interlocked.Increment(ref _nextGameClientEntityId);
            if (next <= 0)
            {
                next = 1000;
                Interlocked.Exchange(ref _nextGameClientEntityId, next);
            }
            return unchecked((ulong)next);
        }

        private void RegisterGameClientEntityIdForIdentity(Guid identityGuid, ulong gameClientEntityId, string peer)
        {
            if (identityGuid == Guid.Empty || gameClientEntityId == 0UL)
            {
                return;
            }

            lock (_identityEntityIdLock)
            {
                _gameClientEntityIdByIdentity[identityGuid] = gameClientEntityId;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-identity-entityid",
                peer = peer,
                identityGuid = identityGuid,
                gameClientEntityId = gameClientEntityId,
            });
        }

        private bool TryGetGameClientEntityIdForIdentity(Guid identityGuid, out ulong gameClientEntityId)
        {
            gameClientEntityId = 0UL;
            if (identityGuid == Guid.Empty)
            {
                return false;
            }

            lock (_identityEntityIdLock)
            {
                return _gameClientEntityIdByIdentity.TryGetValue(identityGuid, out gameClientEntityId) && gameClientEntityId != 0UL;
            }
        }

        private ulong ReserveMetaGameplayMsgNos(int count)
        {
            if (count <= 0)
            {
                count = 1;
            }

            while (true)
            {
                var observed = Interlocked.Read(ref _metaGameplayOutMsgNoHighWatermark);
                var observedU = observed > 0 ? (ulong)observed : 0UL;
                var first = observedU + 1UL;
                if (first == 0UL)
                {
                    first = 1UL;
                }

                var last = first + (ulong)count - 1UL;
                if (Interlocked.CompareExchange(ref _metaGameplayOutMsgNoHighWatermark, (long)last, observed) == observed)
                {
                    return first;
                }
            }
        }
    }
}
