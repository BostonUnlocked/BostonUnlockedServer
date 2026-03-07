using System;
using System.Threading;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private const int HubReadyFallbackDelayMs = 2500;

        private sealed class HubReadyFallbackState
        {
            public readonly object SyncRoot = new object();
            public int Generation;
            public string HubId;
            public string CharacterId;
        }

        private static HubReadyFallbackState CreateHubReadyFallbackState()
        {
            return new HubReadyFallbackState();
        }

        private void CancelHubReadyFallback(HubReadyFallbackState state, string peer, string reason)
        {
            if (state == null)
            {
                return;
            }

            lock (state.SyncRoot)
            {
                state.Generation++;
                state.HubId = null;
                state.CharacterId = null;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-ready-fallback",
                peer = peer,
                status = "cancel",
                reason = reason ?? string.Empty,
            });
        }

        private void ArmHubReadyFallback(HubReadyFallbackState state, ManualResetEvent stopEvent, ManualResetEvent connectionClosed, string peer, string hubId, string characterId, string reason)
        {
            if (state == null || IsNullOrWhiteSpace(hubId) || IsNullOrWhiteSpace(characterId))
            {
                return;
            }

            if (IsHubPeerReady(peer, hubId))
            {
                return;
            }

            int generation;
            lock (state.SyncRoot)
            {
                state.Generation++;
                generation = state.Generation;
                state.HubId = hubId;
                state.CharacterId = characterId;
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "hub-ready-fallback",
                peer = peer,
                status = "arm",
                reason = reason ?? string.Empty,
                hubId = hubId,
                characterId = characterId,
                delayMs = HubReadyFallbackDelayMs,
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                SleepWithStop(stopEvent, HubReadyFallbackDelayMs);
                if (stopEvent.WaitOne(0) || connectionClosed.WaitOne(0))
                {
                    return;
                }

                string pendingHubId;
                string pendingCharacterId;
                lock (state.SyncRoot)
                {
                    if (generation != state.Generation)
                    {
                        return;
                    }

                    pendingHubId = state.HubId;
                    pendingCharacterId = state.CharacterId;
                }

                if (IsNullOrWhiteSpace(pendingHubId) || IsNullOrWhiteSpace(pendingCharacterId))
                {
                    return;
                }

                var activated = TryActivateHubReadiness(peer, pendingHubId, "fallback-delay");
                var total = activated
                    ? Interlocked.Increment(ref _hubReadyFallbackTriggeredTotal)
                    : Interlocked.Increment(ref _hubReadyFallbackSkippedTotal);

                lock (state.SyncRoot)
                {
                    if (generation == state.Generation)
                    {
                        state.Generation++;
                        state.HubId = null;
                        state.CharacterId = null;
                    }
                }

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "hub-ready-fallback",
                    peer = peer,
                    status = activated ? "triggered" : "skipped",
                    reason = "fallback-delay",
                    hubId = pendingHubId,
                    characterId = pendingCharacterId,
                    total = total,
                });
            });
        }
    }
}
