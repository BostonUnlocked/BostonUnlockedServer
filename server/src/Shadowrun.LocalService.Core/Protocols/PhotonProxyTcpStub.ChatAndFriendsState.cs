using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using Cliffhanger.ChatAndFriends.Interfaces.DTOs;
using PhotonProxy.ChatAndFriends.Client.DTOs;
using PhotonProxy.Common.ServiceCommunication;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class PhotonProxyTcpStub
    {
        private sealed class ChatAndFriendsState
        {
            private struct PendingAccountEvent
            {
                public Guid AccountId;
                public MessageEventParameters Event;
            }

            private readonly PhotonProxyTcpStub _owner;
            private readonly object _lock = new object();

            public ChatAndFriendsState(PhotonProxyTcpStub owner)
            {
                _owner = owner;
            }

            private readonly Dictionary<Guid, Peer> _peersByConnId = new Dictionary<Guid, Peer>();
            private readonly Dictionary<Guid, List<Guid>> _connIdsByAccountId = new Dictionary<Guid, List<Guid>>();

            private readonly Dictionary<string, HashSet<Guid>> _channelMembers = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

            private int _nextGroupId = 1;
            private readonly Dictionary<int, GroupRecord> _groupsById = new Dictionary<int, GroupRecord>();

            private int _nextInvitationId = 1;
            private readonly Dictionary<int, InvitationRecord> _invitationsById = new Dictionary<int, InvitationRecord>();
            private readonly Dictionary<Guid, List<int>> _invitationIdsByInvitee = new Dictionary<Guid, List<int>>();

            private sealed class Peer
            {
                public Guid ConnectionId;
                public Guid AccountId;
                public string Endpoint;
                public NetworkStream Stream;
                public object SendLock;
                public User User;
            }

            private sealed class GroupRecord
            {
                public Group Group;
                public Guid OwnerAccountId;
                public readonly HashSet<Guid> MemberAccountIds = new HashSet<Guid>();
                public readonly Dictionary<string, string> GroupData = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            private sealed class InvitationRecord
            {
                public int InvitationId;
                public int GroupId;
                public Guid Inviter;
                public Guid Invitee;
                public DateTime InvitationTimeUtc;
                public bool WasDelivered;
            }

            public void RegisterOrUpdatePeer(Guid connectionId, Guid accountId, string endpoint, NetworkStream stream)
            {
                if (connectionId == Guid.Empty || accountId == Guid.Empty || stream == null)
                {
                    return;
                }

                lock (_lock)
                {
                    Peer peer;
                    if (!_peersByConnId.TryGetValue(connectionId, out peer) || peer == null)
                    {
                        peer = new Peer
                        {
                            ConnectionId = connectionId,
                            SendLock = new object(),
                        };
                        _peersByConnId[connectionId] = peer;
                    }

                    peer.AccountId = accountId;
                    peer.Endpoint = endpoint;
                    peer.Stream = stream;
                    peer.User = CreateUser(accountId);

                    List<Guid> connIds;
                    if (!_connIdsByAccountId.TryGetValue(accountId, out connIds) || connIds == null)
                    {
                        connIds = new List<Guid>();
                        _connIdsByAccountId[accountId] = connIds;
                    }
                    if (!connIds.Contains(connectionId))
                    {
                        connIds.Add(connectionId);
                    }
                }
            }

            public void Unregister(Guid connectionId)
            {
                if (connectionId == Guid.Empty)
                {
                    return;
                }

                List<PendingAccountEvent> pending = null;
                lock (_lock)
                {
                    Peer peer;
                    if (_peersByConnId.TryGetValue(connectionId, out peer) && peer != null)
                    {
                        _peersByConnId.Remove(connectionId);

                        if (peer.AccountId != Guid.Empty)
                        {
                            List<Guid> connIds;
                            if (_connIdsByAccountId.TryGetValue(peer.AccountId, out connIds) && connIds != null)
                            {
                                connIds.Remove(connectionId);
                                var accountWentOffline = connIds.Count == 0;
                                if (accountWentOffline)
                                {
                                    _connIdsByAccountId.Remove(peer.AccountId);
                                    if (pending == null)
                                    {
                                        pending = new List<PendingAccountEvent>();
                                    }
                                    HandleAccountOffline_NoLock(peer.AccountId, pending);
                                }
                            }
                        }
                    }

                    foreach (var kvp in _channelMembers)
                    {
                        if (kvp.Value != null)
                        {
                            kvp.Value.Remove(connectionId);
                        }
                    }
                }

                if (pending != null && pending.Count > 0)
                {
                    for (var i = 0; i < pending.Count; i++)
                    {
                        var p = pending[i];
                        if (p.AccountId == Guid.Empty || p.Event == null)
                        {
                            continue;
                        }
                        SendEventToAccount(p.AccountId, p.Event);
                    }
                }
            }

            private void HandleAccountOffline_NoLock(Guid accountId, List<PendingAccountEvent> pending)
            {
                if (accountId == Guid.Empty)
                {
                    return;
                }

                if (pending == null)
                {
                    pending = new List<PendingAccountEvent>();
                }

                // NOTE: caller holds _lock.
                var groupsToDisband = new List<int>();
                var groupsToRemoveFrom = new List<int>();

                foreach (var kvp in _groupsById)
                {
                    var groupId = kvp.Key;
                    var rec = kvp.Value;
                    if (rec == null || rec.Group == null)
                    {
                        continue;
                    }

                    if (!rec.MemberAccountIds.Contains(accountId))
                    {
                        continue;
                    }

                    if (rec.OwnerAccountId == accountId && !rec.Group.IsPersistent)
                    {
                        groupsToDisband.Add(groupId);
                    }
                    else
                    {
                        groupsToRemoveFrom.Add(groupId);
                    }
                }

                for (var i = 0; i < groupsToDisband.Count; i++)
                {
                    GroupRecord rec;
                    if (_groupsById.TryGetValue(groupsToDisband[i], out rec) && rec != null && rec.Group != null)
                    {
                        var groupSnapshot = CloneGroup(rec);
                        var notify = rec.MemberAccountIds != null ? rec.MemberAccountIds.ToArray() : new Guid[0];
                        DisbandGroup_NoLock(groupsToDisband[i]);

                        var evt = BuildGroupsListChangedEvent(groupSnapshot, 0);
                        for (var n = 0; n < notify.Length; n++)
                        {
                            var m = notify[n];
                            if (m == Guid.Empty)
                            {
                                continue;
                            }
                            pending.Add(new PendingAccountEvent { AccountId = m, Event = evt });
                        }
                    }
                    else
                    {
                        DisbandGroup_NoLock(groupsToDisband[i]);
                    }
                }

                for (var i = 0; i < groupsToRemoveFrom.Count; i++)
                {
                    GroupRecord rec;
                    if (_groupsById.TryGetValue(groupsToRemoveFrom[i], out rec) && rec != null && rec.Group != null)
                    {
                        // Notify remaining members that this user left (disconnect/offline).
                        var groupId = rec.Group.Id;
                        RemoveMemberFromGroup_NoLock(rec, accountId);

                        var groupSnapshotAfter = CloneGroup(rec);
                        var leftEvt = BuildGroupMemberLeftEvent(accountId, groupSnapshotAfter, "GroupManager.LeftGroup");
                        var notify = rec.MemberAccountIds != null ? rec.MemberAccountIds.ToArray() : new Guid[0];
                        for (var n = 0; n < notify.Length; n++)
                        {
                            var m = notify[n];
                            if (m == Guid.Empty)
                            {
                                continue;
                            }
                            pending.Add(new PendingAccountEvent { AccountId = m, Event = leftEvt });
                        }

                        if (rec.MemberAccountIds.Count == 0)
                        {
                            // Group became empty; tell any still-online members to refresh groups list.
                            DisbandGroup_NoLock(groupId);
                        }
                    }
                }
            }

            private static Dictionary<string, object> BuildGroupDataObject(Group group)
            {
                if (group == null)
                {
                    return null;
                }

                return new Dictionary<string, object>
                {
                    { "Id", group.Id },
                    { "GroupName", group.GroupName },
                    { "ChannelName", group.ChannelName },
                    { "IsPersistent", group.IsPersistent },
                    { "Capacity", group.Capacity },
                    { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                };
            }

            private static MessageEventParameters BuildGroupMemberLeftEvent(Guid memberId, Group group, string reason)
            {
                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupMemberLeftMessage, Cliffhanger.ChatAndFriends.Client";
                root["PlayerWhoLeftGroup"] = memberId.ToString();
                root["Group"] = BuildGroupDataObject(group);
                root["Reason"] = string.IsNullOrEmpty(reason) ? "GroupManager.LeftGroup" : reason;

                return new MessageEventParameters
                {
                    Type = "GroupMemberLeftMessage",
                    Payload = Json.Serialize(root),
                };
            }

            private static MessageEventParameters BuildGroupMemberKickedEvent(Guid kickedMemberId, Guid? kickerAccountId, Group group, string reason)
            {
                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupMemberKickedMessage, Cliffhanger.ChatAndFriends.Client";
                root["PlayerWhoWasKicked"] = kickedMemberId.ToString();
                root["PlayerWhoKicked"] = kickerAccountId.HasValue ? kickerAccountId.Value.ToString() : null;
                root["Group"] = BuildGroupDataObject(group);
                root["Reason"] = string.IsNullOrEmpty(reason) ? "GroupManager.Kicked" : reason;

                return new MessageEventParameters
                {
                    Type = "GroupMemberKickedMessage",
                    Payload = Json.Serialize(root),
                };
            }

            private static MessageEventParameters BuildGroupsListChangedEvent(Group group, int action)
            {
                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupsListChangedMessage, Cliffhanger.ChatAndFriends.Client";
                root["GroupData"] = BuildGroupDataObject(group);
                root["Action"] = action;

                return new MessageEventParameters
                {
                    Type = "GroupsListChangedMessage",
                    Payload = Json.Serialize(root),
                };
            }

            public void JoinChannel(Guid connectionId, string channelName)
            {
                if (connectionId == Guid.Empty || string.IsNullOrEmpty(channelName))
                {
                    return;
                }

                Peer peer;
                lock (_lock)
                {
                    if (!_peersByConnId.TryGetValue(connectionId, out peer) || peer == null)
                    {
                        return;
                    }

                    HashSet<Guid> members;
                    if (!_channelMembers.TryGetValue(channelName, out members) || members == null)
                    {
                        members = new HashSet<Guid>();
                        _channelMembers[channelName] = members;
                    }

                    if (!members.Add(connectionId))
                    {
                        return;
                    }
                }

                // Notify participants (including the joiner) that someone joined.
                BroadcastChannelParticipantChanged(channelName, peer.AccountId, true);
            }

            public void LeaveChannel(Guid connectionId, string channelName)
            {
                if (connectionId == Guid.Empty || string.IsNullOrEmpty(channelName))
                {
                    return;
                }

                Peer peer;
                var removed = false;

                lock (_lock)
                {
                    if (!_peersByConnId.TryGetValue(connectionId, out peer) || peer == null)
                    {
                        return;
                    }

                    HashSet<Guid> members;
                    if (_channelMembers.TryGetValue(channelName, out members) && members != null)
                    {
                        removed = members.Remove(connectionId);
                    }
                }

                if (removed)
                {
                    BroadcastChannelParticipantChanged(channelName, peer.AccountId, false);
                }
            }

            public List<User> GetChannelParticipants(string channelName)
            {
                if (string.IsNullOrEmpty(channelName))
                {
                    return new List<User>();
                }

                lock (_lock)
                {
                    HashSet<Guid> members;
                    if (!_channelMembers.TryGetValue(channelName, out members) || members == null || members.Count == 0)
                    {
                        return new List<User>();
                    }

                    var seen = new HashSet<Guid>();
                    var participants = new List<User>();

                    foreach (var connId in members)
                    {
                        Peer peer;
                        if (!_peersByConnId.TryGetValue(connId, out peer) || peer == null)
                        {
                            continue;
                        }

                        if (peer.AccountId == Guid.Empty)
                        {
                            continue;
                        }

                        if (!seen.Add(peer.AccountId))
                        {
                            continue;
                        }

                        participants.Add(peer.User ?? CreateUser(peer.AccountId));
                    }

                    return participants;
                }
            }

            public void BroadcastTextMessage(string channelName, Guid senderAccountId, string text)
            {
                if (string.IsNullOrEmpty(channelName) || senderAccountId == Guid.Empty)
                {
                    return;
                }

                var payload = new TextMessageEventParameters
                {
                    Sender = senderAccountId,
                    Text = text ?? string.Empty,
                    ChannelName = channelName,
                    ServerTimestamp = DateTime.UtcNow,
                };

                // Normal channel behavior (requires JoinChannelRequest).
                var hadTargets = BroadcastToChannelIfAny(channelName, payload, senderAccountId);

                // Party chat uses the group's ChannelName ("Group_<id>") but clients do not always explicitly join.
                // Treat group membership as implicit channel membership.
                if (!hadTargets && channelName.StartsWith("Group_", StringComparison.OrdinalIgnoreCase))
                {
                    BroadcastToGroupChannelIfAny(channelName, payload, senderAccountId);
                }
            }

            public void SendTextMessageToAccount(Guid toAccountId, string channelName, Guid senderAccountId, string text)
            {
                if (toAccountId == Guid.Empty)
                {
                    return;
                }

                var effectiveSender = senderAccountId != Guid.Empty ? senderAccountId : toAccountId;
                var effectiveChannel = !string.IsNullOrEmpty(channelName) ? channelName : "Global";

                var payload = new TextMessageEventParameters
                {
                    Sender = effectiveSender,
                    Text = text ?? string.Empty,
                    ChannelName = effectiveChannel,
                    ServerTimestamp = DateTime.UtcNow,
                };

                SendEventToAccount(toAccountId, payload);
            }

            private void BroadcastChannelParticipantChanged(string channelName, Guid participant, bool added)
            {
                if (string.IsNullOrEmpty(channelName) || participant == Guid.Empty)
                {
                    return;
                }

                var payload = new ChannelParticipantListChangedEventParameters
                {
                    Participant = participant,
                    ChannelName = channelName,
                    ChangeType = added ? (byte)1 : (byte)0,
                };

                BroadcastToChannel(channelName, payload, Guid.Empty);
            }

            private void BroadcastToChannel(string channelName, ISerializableMessage payload, Guid excludeAccountId)
            {
                List<Peer> targets;
                lock (_lock)
                {
                    HashSet<Guid> members;
                    if (!_channelMembers.TryGetValue(channelName, out members) || members == null || members.Count == 0)
                    {
                        return;
                    }

                    targets = new List<Peer>(members.Count);
                    foreach (var connId in members)
                    {
                        Peer peer;
                        if (!_peersByConnId.TryGetValue(connId, out peer) || peer == null)
                        {
                            continue;
                        }

                        if (excludeAccountId != Guid.Empty && peer.AccountId == excludeAccountId)
                        {
                            continue;
                        }

                        targets.Add(peer);
                    }
                }

                for (var i = 0; i < targets.Count; i++)
                {
                    SendEventToPeer(targets[i], payload);
                }
            }

            private bool BroadcastToChannelIfAny(string channelName, ISerializableMessage payload, Guid excludeAccountId)
            {
                List<Peer> targets;
                lock (_lock)
                {
                    HashSet<Guid> members;
                    if (!_channelMembers.TryGetValue(channelName, out members) || members == null || members.Count == 0)
                    {
                        return false;
                    }

                    targets = new List<Peer>(members.Count);
                    foreach (var connId in members)
                    {
                        Peer peer;
                        if (!_peersByConnId.TryGetValue(connId, out peer) || peer == null)
                        {
                            continue;
                        }

                        if (excludeAccountId != Guid.Empty && peer.AccountId == excludeAccountId)
                        {
                            continue;
                        }

                        targets.Add(peer);
                    }
                }

                for (var i = 0; i < targets.Count; i++)
                {
                    SendEventToPeer(targets[i], payload);
                }

                return targets.Count > 0;
            }

            private void BroadcastToGroupChannelIfAny(string groupChannelName, ISerializableMessage payload, Guid excludeAccountId)
            {
                List<Guid> memberAccounts;
                lock (_lock)
                {
                    memberAccounts = new List<Guid>();
                    foreach (var rec in _groupsById.Values)
                    {
                        if (rec == null || rec.Group == null)
                        {
                            continue;
                        }
                        if (!string.Equals(rec.Group.ChannelName, groupChannelName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        memberAccounts.AddRange(rec.MemberAccountIds);
                        break;
                    }
                }

                if (memberAccounts.Count == 0)
                {
                    return;
                }

                for (var i = 0; i < memberAccounts.Count; i++)
                {
                    var accountId = memberAccounts[i];
                    if (accountId == Guid.Empty || (excludeAccountId != Guid.Empty && accountId == excludeAccountId))
                    {
                        continue;
                    }
                    SendEventToAccount(accountId, payload);
                }
            }

            private static User CreateUser(Guid accountId)
            {
                return new User
                {
                    AccountId = accountId,
                    IsOnline = true,
                    FriendshipOrigin = "offline",
                };
            }

            public void NotifySomeoneAddedYouAsFriend(Guid fromAccountId, Guid toAccountId)
            {
                if (fromAccountId == Guid.Empty || toAccountId == Guid.Empty)
                {
                    return;
                }

                var isOnline = IsAccountOnline(fromAccountId);

                // Build JSON payload compatible with JsonFxSerializer(TypeHintName="$type").
                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.SomeoneAddedYouAsFriendMessage, Cliffhanger.ChatAndFriends.Client";
                root["UserData"] = new Dictionary<string, object>
                {
                    { "AccountId", fromAccountId.ToString() },
                    { "IsOnline", isOnline },
                    { "FriendshipOrigin", "offline" },
                    { "ProfileData", null },
                };

                var evt = new MessageEventParameters
                {
                    Type = "SomeoneAddedYouAsFriendMessage",
                    Payload = Json.Serialize(root),
                };

                SendEventToAccount(toAccountId, evt);
            }

            public void PushFriendChanged(Guid toAccountId, Guid friendAccountId, bool isOnline)
            {
                if (toAccountId == Guid.Empty || friendAccountId == Guid.Empty)
                {
                    return;
                }

                // Build JSON payload compatible with JsonFxSerializer(TypeHintName="$type").
                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.FriendChangedMessage, Cliffhanger.ChatAndFriends.Client";
                root["UserData"] = new Dictionary<string, object>
                {
                    { "AccountId", friendAccountId.ToString() },
                    { "IsOnline", isOnline },
                    { "FriendshipOrigin", "offline" },
                    { "ProfileData", null },
                };

                var evt = new MessageEventParameters
                {
                    Type = "FriendChangedMessage",
                    Payload = Json.Serialize(root),
                };

                SendEventToAccount(toAccountId, evt);
            }

            public bool IsAccountOnline(Guid accountId)
            {
                if (accountId == Guid.Empty)
                {
                    return false;
                }

                lock (_lock)
                {
                    List<Guid> connIds;
                    return _connIdsByAccountId.TryGetValue(accountId, out connIds) && connIds != null && connIds.Count > 0;
                }
            }

            // ----- Groups & invitations (minimal for party chat) -----

            public Group CreateGroup(Guid creatorAccountId, string groupName, int capacity, bool isPersistent)
            {
                if (creatorAccountId == Guid.Empty)
                {
                    return null;
                }

                if (string.IsNullOrEmpty(groupName))
                {
                    groupName = "Group";
                }

                if (capacity <= 0)
                {
                    capacity = 4;
                }

                lock (_lock)
                {
                    var id = _nextGroupId++;
                    var group = new Group
                    {
                        Id = id,
                        GroupName = groupName,
                        ChannelName = "Group_" + id,
                        IsPersistent = isPersistent,
                        Capacity = capacity,
                        Members = new List<User> { CreateUser(creatorAccountId) },
                    };

                    var rec = new GroupRecord { Group = group, OwnerAccountId = creatorAccountId };
                    rec.MemberAccountIds.Add(creatorAccountId);
                    rec.GroupData["Leader"] = creatorAccountId.ToString();
                    _groupsById[id] = rec;

                    if (!string.IsNullOrEmpty(groupName) && groupName.StartsWith("CoopGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        CoopGroupHostRegistry.SetLeader(groupName, creatorAccountId);
                    }
                    return group;
                }
            }

            public Dictionary<string, string> GetGroupDataSnapshot(int groupId)
            {
                if (groupId <= 0)
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                lock (_lock)
                {
                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null)
                    {
                        return new Dictionary<string, string>(StringComparer.Ordinal);
                    }

                    return new Dictionary<string, string>(rec.GroupData, StringComparer.Ordinal);
                }
            }

            public void SetGroupData(Guid senderAccountId, int groupId, string key, string value)
            {
                if (senderAccountId == Guid.Empty || groupId <= 0 || string.IsNullOrEmpty(key))
                {
                    return;
                }

                GroupRecord rec;
                Group group;
                List<Guid> notifyAccounts;
                GroupDataChangedAction action;

                lock (_lock)
                {
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null || rec.Group == null)
                    {
                        return;
                    }
                    if (!rec.MemberAccountIds.Contains(senderAccountId))
                    {
                        return;
                    }

                    action = rec.GroupData.ContainsKey(key) ? GroupDataChangedAction.Changed : GroupDataChangedAction.Added;
                    rec.GroupData[key] = value;

                    group = CloneGroup(rec);
                    notifyAccounts = rec.MemberAccountIds.ToList();
                }

                PushGroupDataChangedMessage(senderAccountId, group, action, key, value, notifyAccounts);

                if (!string.IsNullOrEmpty(group.GroupName) && group.GroupName.StartsWith("CoopGroup", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(key, "Leader", StringComparison.OrdinalIgnoreCase))
                    {
                        Guid leader;
                        if (TryParseGuid(value, out leader))
                        {
                            CoopGroupHostRegistry.SetLeader(group.GroupName, leader);
                        }
                    }
                }
            }

            public void DeleteGroupData(Guid senderAccountId, int groupId, string key)
            {
                if (senderAccountId == Guid.Empty || groupId <= 0 || string.IsNullOrEmpty(key))
                {
                    return;
                }

                GroupRecord rec;
                Group group;
                List<Guid> notifyAccounts;

                lock (_lock)
                {
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null || rec.Group == null)
                    {
                        return;
                    }
                    if (!rec.MemberAccountIds.Contains(senderAccountId))
                    {
                        return;
                    }

                    rec.GroupData.Remove(key);
                    group = CloneGroup(rec);
                    notifyAccounts = rec.MemberAccountIds.ToList();
                }

                PushGroupDataChangedMessage(senderAccountId, group, GroupDataChangedAction.Deleted, key, null, notifyAccounts);
            }

            public void BroadcastToGroup(Guid senderAccountId, int groupId, string data)
            {
                if (senderAccountId == Guid.Empty || groupId <= 0 || string.IsNullOrEmpty(data))
                {
                    return;
                }

                Group group;
                List<Guid> notifyAccounts;

                string groupName = null;
                string leaderValue = null;
                var looksLikeHostReady = false;

                lock (_lock)
                {
                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null || rec.Group == null)
                    {
                        return;
                    }
                    if (!rec.MemberAccountIds.Contains(senderAccountId))
                    {
                        return;
                    }

                    group = CloneGroup(rec);
                    groupName = rec.Group != null ? rec.Group.GroupName : null;
                    notifyAccounts = rec.MemberAccountIds.ToList();
                }

                // Track coop group leader/host so APlay can assign NPC control consistently.
                if (!string.IsNullOrEmpty(groupName) && groupName.StartsWith("CoopGroup", StringComparison.OrdinalIgnoreCase))
                {
                    // Broadcast payload is JSON: {"TypeName":"SRO.Client.ChatAndFriends.GroupDataObject, SRO.Client","Key":"Leader","Data":"<guid>"}
                    // Keep this parsing intentionally simple/robust.
                    if (data.IndexOf("\"Key\"", StringComparison.OrdinalIgnoreCase) >= 0
                        && data.IndexOf("Leader", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        leaderValue = TryExtractQuotedJsonValue(data, "Data");
                    }
                    if (data.IndexOf("HostAllMembersReady", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        looksLikeHostReady = true;
                    }

                    Guid leader;
                    if (!string.IsNullOrEmpty(leaderValue) && TryParseGuid(leaderValue, out leader))
                    {
                        CoopGroupHostRegistry.SetLeader(groupName, leader);
                    }
                    else if (looksLikeHostReady)
                    {
                        // Fallback: treat sender as host if we don't have an explicit Leader value.
                        CoopGroupHostRegistry.SetLeader(groupName, senderAccountId);
                    }
                }

                PushBroadcastDataMessage(senderAccountId, group, data, notifyAccounts);
            }

            private static string TryExtractQuotedJsonValue(string json, string key)
            {
                if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                {
                    return null;
                }

                // Very small helper to find patterns like "Key":"Value".
                // Not a full JSON parser; just enough for our known payloads.
                var needle = "\"" + key + "\"";
                var idx = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    return null;
                }

                idx = json.IndexOf(':', idx);
                if (idx < 0)
                {
                    return null;
                }
                idx++;
                while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\t' || json[idx] == '\r' || json[idx] == '\n'))
                {
                    idx++;
                }

                if (idx >= json.Length || json[idx] != '"')
                {
                    return null;
                }

                idx++;
                var end = json.IndexOf('"', idx);
                if (end < 0)
                {
                    return null;
                }
                return json.Substring(idx, end - idx);
            }

            private static bool TryParseGuid(string value, out Guid guid)
            {
                guid = Guid.Empty;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }
                try
                {
                    guid = new Guid(value.Trim());
                    return guid != Guid.Empty;
                }
                catch
                {
                    guid = Guid.Empty;
                    return false;
                }
            }

            private void PushBroadcastDataMessage(Guid senderAccountId, Group group, string data, List<Guid> notifyAccounts)
            {
                if (senderAccountId == Guid.Empty || group == null || string.IsNullOrEmpty(data))
                {
                    return;
                }

                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.BroadcastDataMessage, Cliffhanger.ChatAndFriends.Client";
                root["SenderId"] = senderAccountId.ToString();
                root["Data"] = data;
                root["TimeSent"] = DateTime.UtcNow.ToString("o");
                root["Group"] = new Dictionary<string, object>
                {
                    { "Id", group.Id },
                    { "GroupName", group.GroupName },
                    { "ChannelName", group.ChannelName },
                    { "IsPersistent", group.IsPersistent },
                    { "Capacity", group.Capacity },
                    { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                };

                var evt = new MessageEventParameters
                {
                    Type = "BroadcastDataMessage",
                    Payload = Json.Serialize(root),
                };

                if (notifyAccounts == null)
                {
                    return;
                }

                for (var i = 0; i < notifyAccounts.Count; i++)
                {
                    var accountId = notifyAccounts[i];
                    if (accountId == Guid.Empty || accountId == senderAccountId)
                    {
                        continue;
                    }
                    SendEventToAccount(accountId, evt);
                }
            }

            private void PushGroupDataChangedMessage(Guid senderAccountId, Group group, GroupDataChangedAction action, string key, string value, List<Guid> notifyAccounts)
            {
                if (senderAccountId == Guid.Empty || group == null || string.IsNullOrEmpty(key))
                {
                    return;
                }

                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupDataChangedMessage, Cliffhanger.ChatAndFriends.Client";
                root["Action"] = (int)action;
                root["Key"] = key;
                root["Value"] = value;
                root["Group"] = new Dictionary<string, object>
                {
                    { "Id", group.Id },
                    { "GroupName", group.GroupName },
                    { "ChannelName", group.ChannelName },
                    { "IsPersistent", group.IsPersistent },
                    { "Capacity", group.Capacity },
                    { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                };

                var evt = new MessageEventParameters
                {
                    Type = "GroupDataChangedMessage",
                    Payload = Json.Serialize(root),
                };

                if (notifyAccounts == null)
                {
                    return;
                }

                for (var i = 0; i < notifyAccounts.Count; i++)
                {
                    var accountId = notifyAccounts[i];
                    if (accountId == Guid.Empty || accountId == senderAccountId)
                    {
                        continue;
                    }
                    SendEventToAccount(accountId, evt);
                }
            }

            public List<Group> ListGroupsFor(Guid accountId)
            {
                lock (_lock)
                {
                    var groups = new List<Group>();
                    foreach (var rec in _groupsById.Values)
                    {
                        if (rec == null || rec.Group == null)
                        {
                            continue;
                        }
                        if (accountId == Guid.Empty || rec.MemberAccountIds.Contains(accountId))
                        {
                            groups.Add(CloneGroup(rec));
                        }
                    }
                    return groups;
                }
            }

            public List<User> ListGroupMembers(int groupId)
            {
                lock (_lock)
                {
                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null)
                    {
                        return new List<User>();
                    }
                    return CloneGroup(rec).Members ?? new List<User>();
                }
            }

            public string InviteToGroup(Guid inviter, int groupId, Guid invitee)
            {
                if (inviter == Guid.Empty || invitee == Guid.Empty)
                {
                    return "PlayerNotFound";
                }

                var invitationWasCreated = false;

                lock (_lock)
                {
                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null)
                    {
                        return "PlayerNotFound";
                    }

                    if (!rec.MemberAccountIds.Contains(inviter))
                    {
                        return "PlayerNotFound";
                    }

                    if (rec.MemberAccountIds.Contains(invitee))
                    {
                        return "AlreadyMember";
                    }

                    // Prevent duplicates.
                    List<int> invIds;
                    if (_invitationIdsByInvitee.TryGetValue(invitee, out invIds) && invIds != null)
                    {
                        for (var i = 0; i < invIds.Count; i++)
                        {
                            InvitationRecord existing;
                            if (_invitationsById.TryGetValue(invIds[i], out existing) && existing != null)
                            {
                                if (existing.GroupId == groupId && existing.Inviter == inviter)
                                {
                                    return "DuplicateInvitation";
                                }
                            }
                        }
                    }

                    var invitationId = _nextInvitationId++;
                    var inv = new InvitationRecord
                    {
                        InvitationId = invitationId,
                        GroupId = groupId,
                        Inviter = inviter,
                        Invitee = invitee,
                        InvitationTimeUtc = DateTime.UtcNow,
                        WasDelivered = false,
                    };
                    _invitationsById[invitationId] = inv;

                    if (invIds == null)
                    {
                        invIds = new List<int>();
                        _invitationIdsByInvitee[invitee] = invIds;
                    }
                    invIds.Add(invitationId);

                    invitationWasCreated = true;
                }

                // Immediately push a GroupInvitationReceivedMessage if the invitee is online.
                // This prevents the "Invite" UI from spinning waiting for a poll.
                if (invitationWasCreated)
                {
                    PushInvitationsTo(invitee);
                }

                return "Success";
            }

            public void PushInvitationsTo(Guid inviteeAccountId)
            {
                if (inviteeAccountId == Guid.Empty)
                {
                    return;
                }

                // If the invitee isn't currently connected, don't mark anything as delivered.
                lock (_lock)
                {
                    List<Guid> connIds;
                    if (!_connIdsByAccountId.TryGetValue(inviteeAccountId, out connIds) || connIds == null || connIds.Count == 0)
                    {
                        return;
                    }
                }

                var invitationPayload = BuildInvitationReceivedPayload(inviteeAccountId, true, true);
                if (string.IsNullOrEmpty(invitationPayload))
                {
                    return;
                }

                var evt = new MessageEventParameters
                {
                    Type = "GroupInvitationReceivedMessage",
                    Payload = invitationPayload,
                };

                SendEventToAccount(inviteeAccountId, evt);
            }

            public AcceptInvitationResponse AcceptInvitation(Guid inviteeAccountId, int invitationId)
            {
                Group group;
                string code;
                Guid inviterAccountId;
                List<Guid> notifyAccounts;

                lock (_lock)
                {
                    InvitationRecord inv;
                    if (!_invitationsById.TryGetValue(invitationId, out inv) || inv == null || inv.Invitee != inviteeAccountId)
                    {
                        return new AcceptInvitationResponse { ResultCode = "InvitationNotFound", GroupData = null };
                    }

                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(inv.GroupId, out rec) || rec == null || rec.Group == null)
                    {
                        RemoveInvitationNoThrow(inv);
                        return new AcceptInvitationResponse { ResultCode = "GroupWasDisbanded", GroupData = null };
                    }

                    // If the inviter is no longer in the group, treat the invitation as stale.
                    // This matches the observed client behavior: accepting a stale invite can trigger hub-travel sync and get stuck.
                    if (!rec.MemberAccountIds.Contains(inv.Inviter) || rec.MemberAccountIds.Count == 0)
                    {
                        RemoveInvitationNoThrow(inv);
                        return new AcceptInvitationResponse { ResultCode = "GroupWasDisbanded", GroupData = null };
                    }

                    if (rec.MemberAccountIds.Contains(inviteeAccountId))
                    {
                        RemoveInvitationNoThrow(inv);
                        return new AcceptInvitationResponse { ResultCode = "AlreadyMember", GroupData = CloneGroup(rec) };
                    }

                    if (rec.MemberAccountIds.Count >= rec.Group.Capacity)
                    {
                        return new AcceptInvitationResponse { ResultCode = "CapacityExceeded", GroupData = CloneGroup(rec) };
                    }

                    rec.MemberAccountIds.Add(inviteeAccountId);
                    var members = rec.Group.Members ?? (rec.Group.Members = new List<User>());
                    members.Add(CreateUser(inviteeAccountId));

                    inviterAccountId = inv.Inviter;
                    RemoveInvitationNoThrow(inv);
                    group = CloneGroup(rec);
                    code = "Success";

                    notifyAccounts = rec.MemberAccountIds.ToList();
                }

                if (string.Equals(code, "Success", StringComparison.OrdinalIgnoreCase) && group != null)
                {
                    SendGroupInvitationAcceptedMessage(inviterAccountId, inviteeAccountId, group, notifyAccounts);
                }

                return new AcceptInvitationResponse { ResultCode = code, GroupData = group };
            }

            public DeclineInvitationResponse DeclineInvitation(Guid inviteeAccountId, int invitationId, string reason)
            {
                Guid inviterAccountId;
                Group group;

                lock (_lock)
                {
                    InvitationRecord inv;
                    if (!_invitationsById.TryGetValue(invitationId, out inv) || inv == null || inv.Invitee != inviteeAccountId)
                    {
                        return new DeclineInvitationResponse { ResultCode = "Success" };
                    }

                    inviterAccountId = inv.Inviter;

                    GroupRecord rec;
                    if (_groupsById.TryGetValue(inv.GroupId, out rec) && rec != null && rec.Group != null)
                    {
                        group = CloneGroup(rec);
                    }
                    else
                    {
                        group = null;
                    }

                    RemoveInvitationNoThrow(inv);
                }

                if (group != null && inviterAccountId != Guid.Empty)
                {
                    SendGroupInvitationDeclinedMessage(inviterAccountId, inviteeAccountId, group, reason);
                }

                return new DeclineInvitationResponse { ResultCode = "Success" };
            }

            private void SendGroupInvitationAcceptedMessage(Guid inviterAccountId, Guid inviteeAccountId, Group group, List<Guid> notifyAccounts)
            {
                if (inviterAccountId == Guid.Empty || inviteeAccountId == Guid.Empty || group == null)
                {
                    return;
                }

                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupInvitationAcceptedMessage, Cliffhanger.ChatAndFriends.Client";
                root["Invitee"] = new Dictionary<string, object>
                {
                    { "AccountId", inviteeAccountId.ToString() },
                    { "IsOnline", true },
                    { "FriendshipOrigin", "offline" },
                    { "ProfileData", null },
                };

                root["Group"] = new Dictionary<string, object>
                {
                    { "Id", group.Id },
                    { "GroupName", group.GroupName },
                    { "ChannelName", group.ChannelName },
                    { "IsPersistent", group.IsPersistent },
                    { "Capacity", group.Capacity },
                    { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                };

                var evt = new MessageEventParameters
                {
                    Type = "GroupInvitationAcceptedMessage",
                    Payload = Json.Serialize(root),
                };

                // Notify all current group members except the invitee (the invitee already knows they accepted).
                // Also ensure the inviter/host always receives this message even if they're missing from notifyAccounts.
                var sentToInviter = false;
                if (notifyAccounts != null && notifyAccounts.Count > 0)
                {
                    for (var i = 0; i < notifyAccounts.Count; i++)
                    {
                        var accountId = notifyAccounts[i];
                        if (accountId == Guid.Empty || accountId == inviteeAccountId)
                        {
                            continue;
                        }
                        if (accountId == inviterAccountId)
                        {
                            sentToInviter = true;
                        }
                        SendEventToAccount(accountId, evt);
                    }
                }

                if (!sentToInviter)
                {
                    SendEventToAccount(inviterAccountId, evt);
                }
            }

            private void SendGroupInvitationDeclinedMessage(Guid inviterAccountId, Guid inviteeAccountId, Group group, string reason)
            {
                if (inviterAccountId == Guid.Empty || inviteeAccountId == Guid.Empty || group == null)
                {
                    return;
                }

                var root = new Dictionary<string, object>();
                root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupInvitationDeclinedMessage, Cliffhanger.ChatAndFriends.Client";
                root["Invitee"] = new Dictionary<string, object>
                {
                    { "AccountId", inviteeAccountId.ToString() },
                    { "IsOnline", true },
                    { "FriendshipOrigin", "offline" },
                    { "ProfileData", null },
                };
                root["Group"] = new Dictionary<string, object>
                {
                    { "Id", group.Id },
                    { "GroupName", group.GroupName },
                    { "ChannelName", group.ChannelName },
                    { "IsPersistent", group.IsPersistent },
                    { "Capacity", group.Capacity },
                    { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                };
                root["Reason"] = string.IsNullOrEmpty(reason) ? "Declined" : reason;

                var evt = new MessageEventParameters
                {
                    Type = "GroupInvitationDeclinedMessage",
                    Payload = Json.Serialize(root),
                };

                SendEventToAccount(inviterAccountId, evt);
            }

            public string RemoveGroupMember(Guid requesterAccountId, int groupId, Guid memberId)
            {
                if (groupId <= 0)
                {
                    return "GroupNotExists";
                }
                if (requesterAccountId == Guid.Empty)
                {
                    return "YouAreNotTheGroupMember";
                }
                if (memberId == Guid.Empty)
                {
                    return "UserToKickIsNotGroupMember";
                }

                var pending = new List<PendingAccountEvent>();
                string result;
                lock (_lock)
                {
                    GroupRecord rec;
                    if (!_groupsById.TryGetValue(groupId, out rec) || rec == null || rec.Group == null)
                    {
                        return "GroupNotExists";
                    }

                    if (!rec.MemberAccountIds.Contains(requesterAccountId))
                    {
                        return "YouAreNotTheGroupMember";
                    }

                    if (!rec.MemberAccountIds.Contains(memberId))
                    {
                        return "UserToKickIsNotGroupMember";
                    }

                    var isSelfLeave = requesterAccountId == memberId;
                    var ownerLeft = rec.OwnerAccountId == memberId;
                    var isPersistent = rec.Group.IsPersistent;

                    RemoveMemberFromGroup_NoLock(rec, memberId);

                    // Snapshot group AFTER removal (used for filtering in client and for group caches).
                    var groupAfter = CloneGroup(rec);
                    var remainingMembers = rec.MemberAccountIds != null ? rec.MemberAccountIds.ToArray() : new Guid[0];

                    if (isSelfLeave)
                    {
                        var leftEvt = BuildGroupMemberLeftEvent(memberId, groupAfter, "GroupManager.LeftGroup");
                        // Notify remaining members.
                        for (var i = 0; i < remainingMembers.Length; i++)
                        {
                            var a = remainingMembers[i];
                            if (a != Guid.Empty)
                            {
                                pending.Add(new PendingAccountEvent { AccountId = a, Event = leftEvt });
                            }
                        }
                        // Also notify the leaver (helps UI cleanup if the group object lingers briefly).
                        pending.Add(new PendingAccountEvent { AccountId = memberId, Event = leftEvt });
                    }
                    else
                    {
                        var kickedEvt = BuildGroupMemberKickedEvent(memberId, requesterAccountId, groupAfter, "GroupManager.Kicked");
                        // Notify remaining members.
                        for (var i = 0; i < remainingMembers.Length; i++)
                        {
                            var a = remainingMembers[i];
                            if (a != Guid.Empty)
                            {
                                pending.Add(new PendingAccountEvent { AccountId = a, Event = kickedEvt });
                            }
                        }
                        // Notify the kicked member too (Session listens for "I was kicked").
                        pending.Add(new PendingAccountEvent { AccountId = memberId, Event = kickedEvt });
                    }

                    // Non-persistent groups are treated as party instances; when the owner leaves, the group is closed.
                    if (ownerLeft && !isPersistent)
                    {
                        DisbandGroup_NoLock(groupId);

                        var deletedEvt = BuildGroupsListChangedEvent(groupAfter, 0);
                        for (var i = 0; i < remainingMembers.Length; i++)
                        {
                            var a = remainingMembers[i];
                            if (a != Guid.Empty)
                            {
                                pending.Add(new PendingAccountEvent { AccountId = a, Event = deletedEvt });
                            }
                        }
                        pending.Add(new PendingAccountEvent { AccountId = memberId, Event = deletedEvt });

                        result = "Ok";
                    }
                    else
                    {
                        // Disband empty groups to avoid stale invitations being accepted later.
                        if (rec.MemberAccountIds.Count == 0)
                        {
                            DisbandGroup_NoLock(groupId);
                            var deletedEvt = BuildGroupsListChangedEvent(groupAfter, 0);
                            pending.Add(new PendingAccountEvent { AccountId = memberId, Event = deletedEvt });
                        }
                        result = "Ok";
                    }
                }

                for (var i = 0; i < pending.Count; i++)
                {
                    var p = pending[i];
                    if (p.AccountId == Guid.Empty || p.Event == null)
                    {
                        continue;
                    }
                    SendEventToAccount(p.AccountId, p.Event);
                }

                return result;
            }

            private void RemoveMemberFromGroup_NoLock(GroupRecord rec, Guid memberId)
            {
                if (rec == null || rec.Group == null || memberId == Guid.Empty)
                {
                    return;
                }

                rec.MemberAccountIds.Remove(memberId);
                if (rec.Group.Members != null)
                {
                    for (var i = rec.Group.Members.Count - 1; i >= 0; i--)
                    {
                        var u = rec.Group.Members[i];
                        if (u != null && u.AccountId == memberId)
                        {
                            rec.Group.Members.RemoveAt(i);
                        }
                    }
                }
            }

            private void DisbandGroup_NoLock(int groupId)
            {
                if (groupId <= 0)
                {
                    return;
                }

                GroupRecord rec;
                if (_groupsById.TryGetValue(groupId, out rec) && rec != null)
                {
                    _groupsById.Remove(groupId);
                }
            }

            private void RemoveInvitationNoThrow(InvitationRecord inv)
            {
                try
                {
                    _invitationsById.Remove(inv.InvitationId);
                    List<int> list;
                    if (_invitationIdsByInvitee.TryGetValue(inv.Invitee, out list) && list != null)
                    {
                        list.Remove(inv.InvitationId);
                        if (list.Count == 0)
                        {
                            _invitationIdsByInvitee.Remove(inv.Invitee);
                        }
                    }
                }
                catch
                {
                }
            }

            private static Group CloneGroup(GroupRecord rec)
            {
                var group = rec != null ? rec.Group : null;
                if (group == null)
                {
                    return null;
                }

                var members = new List<User>();
                if (group.Members != null)
                {
                    for (var i = 0; i < group.Members.Count; i++)
                    {
                        var u = group.Members[i];
                        if (u != null)
                        {
                            members.Add(new User { AccountId = u.AccountId, IsOnline = u.IsOnline, FriendshipOrigin = u.FriendshipOrigin });
                        }
                    }
                }

                return new Group
                {
                    Id = group.Id,
                    GroupName = group.GroupName,
                    ChannelName = group.ChannelName,
                    IsPersistent = group.IsPersistent,
                    Capacity = group.Capacity,
                    Members = members,
                };
            }

            private string BuildInvitationReceivedPayload(Guid inviteeAccountId, bool onlyUndelivered, bool markDelivered)
            {
                lock (_lock)
                {
                    List<int> ids;
                    if (!_invitationIdsByInvitee.TryGetValue(inviteeAccountId, out ids) || ids == null || ids.Count == 0)
                    {
                        return null;
                    }

                    // Build JSON payload compatible with JsonFxSerializer(TypeHintName="$type").
                    var root = new Dictionary<string, object>();
                    root["$type"] = "Cliffhanger.ChatAndFriends.Client.Messaging.DTOs.GroupInvitationReceivedMessage, Cliffhanger.ChatAndFriends.Client";

                    var invitations = new List<object>();

                    for (var i = 0; i < ids.Count; i++)
                    {
                        InvitationRecord inv;
                        if (!_invitationsById.TryGetValue(ids[i], out inv) || inv == null)
                        {
                            continue;
                        }

                        if (onlyUndelivered && inv.WasDelivered)
                        {
                            continue;
                        }

                        GroupRecord rec;
                        if (!_groupsById.TryGetValue(inv.GroupId, out rec) || rec == null || rec.Group == null)
                        {
                            RemoveInvitationNoThrow(inv);
                            continue;
                        }

                        // If the inviter is no longer in the group, the invitation is stale.
                        if (!rec.MemberAccountIds.Contains(inv.Inviter) || rec.MemberAccountIds.Count == 0)
                        {
                            RemoveInvitationNoThrow(inv);
                            continue;
                        }

                        var group = CloneGroup(rec);

                        if (markDelivered)
                        {
                            inv.WasDelivered = true;
                        }

                        var invObj = new Dictionary<string, object>();
                        invObj["Id"] = inv.InvitationId;
                        invObj["InvitationTime"] = inv.InvitationTimeUtc.ToString("o");

                        invObj["Inviter"] = new Dictionary<string, object>
                        {
                            { "AccountId", inv.Inviter.ToString() },
                            { "IsOnline", true },
                            { "FriendshipOrigin", "offline" },
                            { "ProfileData", null },
                        };

                        invObj["Group"] = group != null
                            ? new Dictionary<string, object>
                            {
                                { "Id", group.Id },
                                { "GroupName", group.GroupName },
                                { "ChannelName", group.ChannelName },
                                { "IsPersistent", group.IsPersistent },
                                { "Capacity", group.Capacity },
                                { "Members", group.Members != null ? group.Members.Select(m => m.AccountId.ToString()).ToArray() : new string[0] },
                            }
                            : null;

                        invObj["GroupData"] = new object[0];
                        invitations.Add(invObj);
                    }

                    if (invitations.Count == 0)
                    {
                        return null;
                    }

                    root["Invitations"] = invitations;
                    return Json.Serialize(root);
                }
            }

            private static readonly JavaScriptSerializer Json = CreateJsonSerializer();

            private static JavaScriptSerializer CreateJsonSerializer()
            {
                var ser = new JavaScriptSerializer();
                ser.RecursionLimit = 64;
                ser.MaxJsonLength = int.MaxValue;
                return ser;
            }

            // ----- sending helpers -----

            private void SendEventToAccount(Guid accountId, ISerializableMessage payload)
            {
                if (accountId == Guid.Empty)
                {
                    return;
                }

                List<Peer> peers;
                lock (_lock)
                {
                    List<Guid> connIds;
                    if (!_connIdsByAccountId.TryGetValue(accountId, out connIds) || connIds == null || connIds.Count == 0)
                    {
                        return;
                    }

                    peers = new List<Peer>(connIds.Count);
                    for (var i = 0; i < connIds.Count; i++)
                    {
                        Peer peer;
                        if (_peersByConnId.TryGetValue(connIds[i], out peer) && peer != null)
                        {
                            peers.Add(peer);
                        }
                    }
                }

                for (var i = 0; i < peers.Count; i++)
                {
                    SendEventToPeer(peers[i], payload);
                }
            }

            private void SendEventToPeer(Peer peer, ISerializableMessage payload)
            {
                if (peer == null || peer.Stream == null)
                {
                    return;
                }

                if (_owner == null)
                {
                    return;
                }

                // Build PhotonClientMessage (protobuf) and wrap into Photon EventData (binary protocol).
                var clientMessage = new PhotonClientMessage
                {
                    MessageId = Guid.NewGuid(),
                    Payload = payload,
                };

                byte[] clientMessageBytes;
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        // ClientSerializer can serialize PhotonClientMessage even though it isn't ISerializableMessage.
                        SerializeToStream(ms, clientMessage);
                        clientMessageBytes = ms.ToArray();
                    }
                }
                catch
                {
                    return;
                }

                var eventPayload = BuildEventDataWithSingleByteArrayParam(0x00, 100, clientMessageBytes);

                lock (peer.SendLock)
                {
                    _owner.SendPhotonFrame(peer.Stream, peer.Endpoint ?? "unknown", eventPayload, "event");
                }
            }

            private static byte[] BuildEventDataWithSingleByteArrayParam(byte eventCode, byte parameterKey, byte[] parameterValue)
            {
                var payload = new List<byte>(16 + (parameterValue != null ? parameterValue.Length : 0));
                payload.Add(0xF3);
                payload.Add(0x04);
                payload.Add(eventCode);

                payload.Add(0x00);
                payload.Add(0x01);

                payload.Add(parameterKey);
                payload.Add(0x78);

                var len = parameterValue != null ? parameterValue.Length : 0;
                payload.Add((byte)(len >> 24));
                payload.Add((byte)(len >> 16));
                payload.Add((byte)(len >> 8));
                payload.Add((byte)len);

                if (parameterValue != null && parameterValue.Length > 0)
                {
                    payload.AddRange(parameterValue);
                }

                return payload.ToArray();
            }
        }

        private static void SerializeToStream(Stream stream, object message)
        {
            // NOTE: we instantiate ClientSerializer per PhotonProxyTcpStub instance, but for event sends we only need its
            // static known-types table; creating a throwaway serializer here keeps the state class self-contained.
            var serializer = new ClientSerializer();
            serializer.Serialize(stream, message);
        }
    }
}
