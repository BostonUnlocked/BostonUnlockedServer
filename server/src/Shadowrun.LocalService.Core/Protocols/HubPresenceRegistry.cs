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

        public bool TryGetCharacterIdForAccount(Guid accountId, out string characterId)
        {
            characterId = null;
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
                    if (_byPeer.TryGetValue(peer, out p) && p != null && !IsNullOrWhiteSpace(p.CharacterId))
                    {
                        characterId = p.CharacterId;
                        return true;
                    }
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

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
