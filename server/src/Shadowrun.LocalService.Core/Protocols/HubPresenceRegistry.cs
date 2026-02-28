using System;
using System.Collections.Generic;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal sealed class HubPresenceRegistry
    {
        internal sealed class Participant
        {
            public string Peer;
            public Guid AccountId;
            public string IdentityHash;
            public int CareerIndex;
            public string CharacterId;
            public string CharacterName;
            public string HubId;
            public float X;
            public float Y;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, Participant> _byPeer = new Dictionary<string, Participant>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _peersByHub = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, HashSet<string>> _peersByAccount = new Dictionary<Guid, HashSet<string>>();
        private readonly Dictionary<string, string> _peerByCharacterId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public void RegisterOrUpdate(
            string peer,
            Guid accountId,
            string identityHash,
            int careerIndex,
            string characterId,
            string characterName,
            string hubId,
            float x,
            float y)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_lock)
            {
                Participant p;
                if (!_byPeer.TryGetValue(peer, out p) || p == null)
                {
                    p = new Participant();
                    p.Peer = peer;
                    _byPeer[peer] = p;
                }

                RemoveFromIndexes_NoLock(p);

                p.AccountId = accountId;
                p.IdentityHash = identityHash;
                p.CareerIndex = careerIndex;
                p.CharacterId = characterId;
                p.CharacterName = characterName;
                p.HubId = hubId;
                p.X = x;
                p.Y = y;

                AddToIndexes_NoLock(p);
            }
        }

        public void SetHubForPeer(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return;
            }

            lock (_lock)
            {
                Participant p;
                if (!_byPeer.TryGetValue(peer, out p) || p == null)
                {
                    p = new Participant();
                    p.Peer = peer;
                    _byPeer[peer] = p;
                }

                if (string.Equals(p.HubId, hubId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RemoveFromHub_NoLock(p.Peer, p.HubId);
                p.HubId = hubId;
                AddToHub_NoLock(p.Peer, p.HubId);
            }
        }

        public void UpdatePosition(string peer, float x, float y)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_lock)
            {
                Participant p;
                if (_byPeer.TryGetValue(peer, out p) && p != null)
                {
                    p.X = x;
                    p.Y = y;
                }
            }
        }

        public bool TryGetHubIdForPeer(string peer, out string hubId)
        {
            hubId = null;
            if (IsNullOrWhiteSpace(peer))
            {
                return false;
            }

            lock (_lock)
            {
                Participant p;
                if (!_byPeer.TryGetValue(peer, out p) || p == null || IsNullOrWhiteSpace(p.HubId))
                {
                    return false;
                }

                hubId = p.HubId;
                return true;
            }
        }

        public bool TryGetHubIdForAccount(Guid accountId, out string hubId)
        {
            hubId = null;
            if (accountId == Guid.Empty)
            {
                return false;
            }

            lock (_lock)
            {
                HashSet<string> peers;
                if (!_peersByAccount.TryGetValue(accountId, out peers) || peers == null || peers.Count == 0)
                {
                    return false;
                }

                foreach (var peer in peers)
                {
                    Participant p;
                    if (_byPeer.TryGetValue(peer, out p) && p != null && !IsNullOrWhiteSpace(p.HubId))
                    {
                        hubId = p.HubId;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryGetHubIdForCharacter(string characterId, out string hubId)
        {
            hubId = null;
            if (IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            lock (_lock)
            {
                string peer;
                if (!_peerByCharacterId.TryGetValue(characterId, out peer) || IsNullOrWhiteSpace(peer))
                {
                    return false;
                }

                Participant p;
                if (_byPeer.TryGetValue(peer, out p) && p != null && !IsNullOrWhiteSpace(p.HubId))
                {
                    hubId = p.HubId;
                    return true;
                }
            }

            return false;
        }

        public bool TryGetPeerForCharacter(string characterId, out string peer)
        {
            peer = null;
            if (IsNullOrWhiteSpace(characterId))
            {
                return false;
            }

            lock (_lock)
            {
                return _peerByCharacterId.TryGetValue(characterId, out peer) && !IsNullOrWhiteSpace(peer);
            }
        }

        public IList<Participant> GetParticipantsInHub(string hubId)
        {
            var result = new List<Participant>();
            if (IsNullOrWhiteSpace(hubId))
            {
                return result;
            }

            lock (_lock)
            {
                HashSet<string> peers;
                if (!_peersByHub.TryGetValue(hubId, out peers) || peers == null || peers.Count == 0)
                {
                    return result;
                }

                foreach (var peer in peers)
                {
                    Participant p;
                    if (!_byPeer.TryGetValue(peer, out p) || p == null)
                    {
                        continue;
                    }

                    var copy = new Participant();
                    copy.Peer = p.Peer;
                    copy.AccountId = p.AccountId;
                    copy.IdentityHash = p.IdentityHash;
                    copy.CareerIndex = p.CareerIndex;
                    copy.CharacterId = p.CharacterId;
                    copy.CharacterName = p.CharacterName;
                    copy.HubId = p.HubId;
                    copy.X = p.X;
                    copy.Y = p.Y;
                    result.Add(copy);
                }
            }

            return result;
        }

        public bool TryGetParticipantForPeer(string peer, out Participant participant)
        {
            participant = null;
            if (IsNullOrWhiteSpace(peer))
            {
                return false;
            }

            lock (_lock)
            {
                Participant p;
                if (!_byPeer.TryGetValue(peer, out p) || p == null)
                {
                    return false;
                }

                var copy = new Participant();
                copy.Peer = p.Peer;
                copy.AccountId = p.AccountId;
                copy.IdentityHash = p.IdentityHash;
                copy.CareerIndex = p.CareerIndex;
                copy.CharacterId = p.CharacterId;
                copy.CharacterName = p.CharacterName;
                copy.HubId = p.HubId;
                copy.X = p.X;
                copy.Y = p.Y;
                participant = copy;
                return true;
            }
        }

        public void RemovePeer(string peer)
        {
            if (IsNullOrWhiteSpace(peer))
            {
                return;
            }

            lock (_lock)
            {
                Participant p;
                if (!_byPeer.TryGetValue(peer, out p) || p == null)
                {
                    return;
                }

                RemoveFromIndexes_NoLock(p);
                _byPeer.Remove(peer);
            }
        }

        private void RemoveFromIndexes_NoLock(Participant p)
        {
            if (p == null)
            {
                return;
            }

            RemoveFromHub_NoLock(p.Peer, p.HubId);

            if (p.AccountId != Guid.Empty)
            {
                HashSet<string> peers;
                if (_peersByAccount.TryGetValue(p.AccountId, out peers) && peers != null)
                {
                    peers.Remove(p.Peer);
                    if (peers.Count == 0)
                    {
                        _peersByAccount.Remove(p.AccountId);
                    }
                }
            }

            if (!IsNullOrWhiteSpace(p.CharacterId))
            {
                string existing;
                if (_peerByCharacterId.TryGetValue(p.CharacterId, out existing) && string.Equals(existing, p.Peer, StringComparison.OrdinalIgnoreCase))
                {
                    _peerByCharacterId.Remove(p.CharacterId);
                }
            }
        }

        private void AddToIndexes_NoLock(Participant p)
        {
            if (p == null)
            {
                return;
            }

            AddToHub_NoLock(p.Peer, p.HubId);

            if (p.AccountId != Guid.Empty)
            {
                HashSet<string> peers;
                if (!_peersByAccount.TryGetValue(p.AccountId, out peers) || peers == null)
                {
                    peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _peersByAccount[p.AccountId] = peers;
                }
                peers.Add(p.Peer);
            }

            if (!IsNullOrWhiteSpace(p.CharacterId))
            {
                _peerByCharacterId[p.CharacterId] = p.Peer;
            }
        }

        private void RemoveFromHub_NoLock(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return;
            }

            HashSet<string> peers;
            if (_peersByHub.TryGetValue(hubId, out peers) && peers != null)
            {
                peers.Remove(peer);
                if (peers.Count == 0)
                {
                    _peersByHub.Remove(hubId);
                }
            }
        }

        private void AddToHub_NoLock(string peer, string hubId)
        {
            if (IsNullOrWhiteSpace(peer) || IsNullOrWhiteSpace(hubId))
            {
                return;
            }

            HashSet<string> peers;
            if (!_peersByHub.TryGetValue(hubId, out peers) || peers == null)
            {
                peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _peersByHub[hubId] = peers;
            }
            peers.Add(peer);
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
