using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PhotonProxy.ChatAndFriends.Client.DTOs;
using PhotonProxy.Common.ServiceCommunication;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class PhotonProxyTcpStub
    {
        private static ServiceEnvelopeRequest ParseServiceEnvelopeRequest(byte[] payload)
        {
            if (payload == null || payload.Length < 5 || payload[0] != 0xF3 || payload[1] != 0x02 || payload[2] != 0x64)
            {
                return null;
            }

            var parameterCount = ReadUInt16BigEndian(payload, 3);
            var pos = 5;
            int operationId = 0;
            string operationName = null;
            byte[] requestPayload = null;
            var hasOperationId = false;

            for (var i = 0; i < parameterCount && pos + 2 <= payload.Length; i++)
            {
                var key = payload[pos++];
                var type = payload[pos++];

                // int32
                if (type == 0x69)
                {
                    if (pos + 4 > payload.Length)
                    {
                        return null;
                    }

                    var value = ReadInt32BigEndian(payload, pos);
                    pos += 4;
                    if (key == 12)
                    {
                        operationId = value;
                        hasOperationId = true;
                    }

                    continue;
                }

                // byte[]
                if (type == 0x78)
                {
                    if (pos + 4 > payload.Length)
                    {
                        return null;
                    }

                    var len = ReadInt32BigEndian(payload, pos);
                    pos += 4;
                    if (len < 0 || pos + len > payload.Length)
                    {
                        return null;
                    }

                    if (key == 15)
                    {
                        requestPayload = new byte[len];
                        Buffer.BlockCopy(payload, pos, requestPayload, 0, len);
                    }

                    pos += len;
                    continue;
                }

                // string (uint16 len)
                if (type == 0x73)
                {
                    if (pos + 2 > payload.Length)
                    {
                        return null;
                    }

                    var len = ReadUInt16BigEndian(payload, pos);
                    pos += 2;
                    if (pos + len > payload.Length)
                    {
                        return null;
                    }

                    if (key == 19)
                    {
                        operationName = Encoding.UTF8.GetString(payload, pos, len);
                    }

                    pos += len;
                    continue;
                }

                return null;
            }

            if (!hasOperationId)
            {
                return null;
            }

            return new ServiceEnvelopeRequest(operationId, operationName, requestPayload);
        }

        private static byte[] BuildOperationResponseWithSingleByteArrayParam(byte opCode, byte parameterKey, byte[] parameterValue)
        {
            var payload = new List<byte>(16 + (parameterValue != null ? parameterValue.Length : 0));
            payload.Add(0xF3);
            payload.Add(0x03);
            payload.Add(opCode);
            payload.Add(0x00);
            payload.Add(0x00);
            payload.Add(0x2A);
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

        private byte[] BuildSerializedErrorServiceResponse(int operationId, string errorDescription)
        {
            var response = new ServiceResponse
            {
                OperationId = operationId,
                ErrorDescription = errorDescription,
                Payload = null,
            };

            return SerializeMessage(response);
        }

        private byte[] BuildSerializedServiceResponse(ConnectionState state, int operationId, string operationName, byte[] requestPayload)
        {
            var payload = BuildPayload(state, operationName, requestPayload);

            var response = new ServiceResponse
            {
                OperationId = operationId,
                ErrorDescription = null,
                Payload = payload,
            };

            return SerializeMessage(response);
        }

        private ISerializableMessage BuildPayload(ConnectionState state, string operationName, byte[] requestPayload)
        {
            if (string.IsNullOrEmpty(operationName))
            {
                return new DisconnectResponse();
            }

            switch (operationName)
            {
                case "ConnectRequest":
                {
                    var req = DeserializeMessage<ConnectRequest>(requestPayload);
                    var accountId = ResolveAccountIdFromSession(req != null ? req.AccountSystemSession : Guid.Empty);

                    if (accountId == Guid.Empty)
                    {
                        try
                        {
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "photon",
                                op = "connect-rejected",
                                reason = "missing-or-unmapped-account-system-session",
                                sessionHash = req != null ? req.AccountSystemSession.ToString() : Guid.Empty.ToString(),
                            });
                        }
                        catch
                        {
                        }

                        return new DisconnectResponse();
                    }

                    state.AccountId = accountId;
                    state.LocalUser = CreateUser(accountId);

                    return new ConnectResponse { IdentityHash = accountId };
                }

                case "AddGlobalMessageSubscriptionRequest":
                    return new AddGlobalMessageSubscriptionResponse();

                case "RemoveGlobalMessageSubscriptionRequest":
                    return new RemoveGlobalMessageSubscriptionResponse();

                case "JoinChannelRequest":
                {
                    var req = DeserializeMessage<JoinChannelRequest>(requestPayload);
                    state.LastChannelName = req != null ? req.ChannelName : state.LastChannelName;
                    return new JoinChannelResponse { ChannelName = req != null ? req.ChannelName : null };
                }

                case "LeaveChannelRequest":
                    return new LeaveChannelResponse();

                case "GetChannelParticipantsRequest":
                {
                    return new GetChannelParticipantsResponse
                    {
                        Participants = new List<User> { state.LocalUser ?? CreateUser(state.AccountId) }
                    };
                }

                case "SendMessageToChannelRequest":
                    return new SendMessageToChannelResponse { Success = true };

                case "ListFriendsRequest":
                    return new ListFriendsResponse { Friends = new List<User>() };

                case "PushInvitationsRequest":
                    return new PushInvitationsResponse();

                case "CreateGroupRequest":
                {
                    var req = DeserializeMessage<CreateGroupRequest>(requestPayload);
                    var group = EnsureGroup(state, req);
                    return new CreateGroupResponse { GroupData = group };
                }

                case "ListGroupsRequest":
                {
                    var group = state.Group;
                    var groups = new List<Group>();
                    if (group != null)
                    {
                        groups.Add(group);
                    }
                    return new ListGroupsResponse { Groups = groups };
                }

                case "ListGroupMembersRequest":
                {
                    var group = state.Group;
                    var members = new List<User>();
                    if (group != null && group.Members != null)
                    {
                        members.AddRange(group.Members);
                    }
                    return new ListGroupMembersResponse { Members = members };
                }

                case "SetGroupDataRequest":
                {
                    var req = DeserializeMessage<SetGroupDataRequest>(requestPayload);
                    if (req != null && req.Data != null && !string.IsNullOrEmpty(req.Data.Key))
                    {
                        state.GroupData[req.Data.Key] = req.Data.Value;
                    }
                    return new SetGroupDataResponse();
                }

                case "DeleteGroupDataRequest":
                {
                    var req = DeserializeMessage<DeleteGroupDataRequest>(requestPayload);
                    if (req != null && !string.IsNullOrEmpty(req.Datakey))
                    {
                        state.GroupData.Remove(req.Datakey);
                    }
                    return new DeleteGroupDataResponse();
                }

                case "GetGroupDataRequest":
                {
                    var entries = new List<GroupDataEntry>();
                    foreach (var kvp in state.GroupData)
                    {
                        entries.Add(new GroupDataEntry { Key = kvp.Key, Value = kvp.Value });
                    }
                    return new GetGroupDataResponse { GroupData = entries };
                }

                case "BroadcastToGroupRequest":
                {
                    // For offline/solo we don't need to echo events back to the client yet; the UI updates its own local state.
                    // We still accept the broadcast and cache it under a synthetic key for debugging.
                    var req = DeserializeMessage<BroadcastToGroupRequest>(requestPayload);
                    if (req != null && !string.IsNullOrEmpty(req.Data))
                    {
                        state.LastGroupBroadcast = req.Data;
                    }
                    return new BroadcastToGroupResponse();
                }

                case "DisconnectRequest":
                    return new DisconnectResponse();

                default:
                {
                    // Log unmapped operation names so we can iterate quickly as the client hits new APIs.
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "photon-op64-unmapped",
                        operationName = operationName,
                        requestBytes = requestPayload != null ? requestPayload.Length : 0,
                        requestHex = requestPayload != null ? ToHexString(requestPayload, 0, Math.Min(64, requestPayload.Length)).ToLowerInvariant() : string.Empty,
                    });

                    return new DisconnectResponse();
                }
            }
        }

        private Guid ResolveAccountIdFromSession(Guid sessionHash)
        {
            if (sessionHash == Guid.Empty)
            {
                return Guid.Empty;
            }

            try
            {
                string identity;
                if (_sessionIdentityMap != null && _sessionIdentityMap.TryGetIdentityForSession(sessionHash.ToString(), out identity) && !string.IsNullOrEmpty(identity))
                {
                    return new Guid(identity);
                }
                if (_userStore != null && _userStore.TryGetIdentityForSession(sessionHash.ToString(), out identity) && !string.IsNullOrEmpty(identity))
                {
                    return new Guid(identity);
                }
            }
            catch
            {
            }

            // Enforced: do not fall back to a global/default identity.
            return Guid.Empty;
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

        private Group EnsureGroup(ConnectionState state, CreateGroupRequest req)
        {
            if (state.Group != null)
            {
                return state.Group;
            }

            var name = req != null ? req.GroupName : null;
            if (string.IsNullOrEmpty(name))
            {
                name = "SinglePlayer";
            }

            var capacity = req != null ? req.Capacity : 4;
            if (capacity <= 0)
            {
                capacity = 4;
            }

            var localUser = state.LocalUser ?? CreateUser(state.AccountId);
            state.LocalUser = localUser;

            state.Group = new Group
            {
                Id = state.GroupId,
                GroupName = name,
                ChannelName = "Group_" + state.GroupId,
                IsPersistent = req != null && req.IsPersistent,
                Capacity = capacity,
                Members = new List<User> { localUser },
            };

            return state.Group;
        }

        private T DeserializeMessage<T>(byte[] bytes) where T : class, ISerializableMessage
        {
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                using (var ms = new MemoryStream(bytes))
                {
                    return (T)_serializer.Deserialize(ms, null, typeof(T));
                }
            }
            catch
            {
                return null;
            }
        }

        private byte[] SerializeMessage(ISerializableMessage message)
        {
            if (message == null)
            {
                return new byte[0];
            }

            using (var ms = new MemoryStream())
            {
                _serializer.Serialize(ms, message);
                return ms.ToArray();
            }
        }

        private static byte[] EncodeVarint(uint value)
        {
            var bytes = new List<byte>(5);
            do
            {
                var chunk = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                {
                    chunk |= 0x80;
                }

                bytes.Add(chunk);
            }
            while (value != 0);

            return bytes.ToArray();
        }

        private static int ReadInt32BigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24)
                | (data[offset + 1] << 16)
                | (data[offset + 2] << 8)
                | data[offset + 3];
        }

        private static ushort ReadUInt16BigEndian(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private sealed class ServiceEnvelopeRequest
        {
            public readonly int OperationId;
            public readonly string OperationName;
            public readonly byte[] RequestPayload;

            public ServiceEnvelopeRequest(int operationId, string operationName, byte[] requestPayload)
            {
                OperationId = operationId;
                OperationName = operationName;
                RequestPayload = requestPayload;
            }
        }

        private sealed class ConnectionState
        {
            public Guid AccountId;
            public User LocalUser;
            public string LastChannelName;

            public int GroupId = 1;
            public Group Group;
            public readonly Dictionary<string, string> GroupData = new Dictionary<string, string>(StringComparer.Ordinal);
            public string LastGroupBroadcast;
        }
    }
}
