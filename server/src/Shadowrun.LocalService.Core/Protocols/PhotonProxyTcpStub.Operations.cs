using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using PhotonProxy.ChatAndFriends.Client.DTOs;
using PhotonProxy.Common.ServiceCommunication;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class PhotonProxyTcpStub
    {
    private static readonly object ItemValidationDataLock = new object();
    private static ItemValidationData _itemValidationData;
    private static string _itemValidationDataSourceDir;

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

                    try
                    {
                        var wasOnline = _chatAndFriends != null && _chatAndFriends.IsAccountOnline(accountId);
                        _chatAndFriends.RegisterOrUpdatePeer(state.ConnectionId, accountId, state.Endpoint, state.Stream);
                        var isOnline = _chatAndFriends != null && _chatAndFriends.IsAccountOnline(accountId);
                        if (!wasOnline && isOnline)
                        {
                            NotifyFriendsPresenceChanged(accountId, true);
                        }
                    }
                    catch
                    {
                    }

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

                    try
                    {
                        _chatAndFriends.JoinChannel(state.ConnectionId, req != null ? req.ChannelName : null);
                    }
                    catch
                    {
                    }

                    return new JoinChannelResponse { ChannelName = req != null ? req.ChannelName : null };
                }

                case "LeaveChannelRequest":
                {
                    var req = DeserializeMessage<LeaveChannelRequest>(requestPayload);
                    try
                    {
                        _chatAndFriends.LeaveChannel(state.ConnectionId, req != null ? req.ChannelName : null);
                    }
                    catch
                    {
                    }
                    return new LeaveChannelResponse();
                }

                case "GetChannelParticipantsRequest":
                {
                    var req = DeserializeMessage<GetChannelParticipantsRequest>(requestPayload);
                    var channelName = req != null ? req.ChannelName : null;
                    List<User> participants;
                    try
                    {
                        participants = _chatAndFriends.GetChannelParticipants(channelName);
                    }
                    catch
                    {
                        participants = new List<User> { state.LocalUser ?? CreateUser(state.AccountId) };
                    }

                    return new GetChannelParticipantsResponse { Participants = participants };
                }

                case "SendMessageToChannelRequest":
                {
                    var req = DeserializeMessage<SendMessageToChannelRequest>(requestPayload);
                    try
                    {
                        if (req != null)
                        {
                            if (!TryHandleGlobalSlashCommand(state, req))
                            {
                                _chatAndFriends.BroadcastTextMessage(req.ChannelName, state.AccountId, req.TextMessage);
                            }
                        }
                    }
                    catch
                    {
                    }
                    return new SendMessageToChannelResponse { Success = true };
                }

                case "ListFriendsRequest":
                {
                    var req = DeserializeMessage<ListFriendsRequest>(requestPayload);
                    var accountId = req != null && req.AccountId != Guid.Empty ? req.AccountId : state.AccountId;
                    var friends = new List<User>();
                    try
                    {
                        var friendIds = _friendsStore.GetFriends(accountId);
                        for (var i = 0; i < friendIds.Count; i++)
                        {
                            var friendId = friendIds[i];
                            friends.Add(CreateUser(friendId, _chatAndFriends != null && _chatAndFriends.IsAccountOnline(friendId)));
                        }
                    }
                    catch
                    {
                    }
                    return new ListFriendsResponse { Friends = friends };
                }

                case "AddFriendRequest":
                {
                    var req = DeserializeMessage<AddFriendRequest>(requestPayload);
                    var friendId = req != null ? req.FriendAccountId : Guid.Empty;

                    if (state.AccountId != Guid.Empty && friendId != Guid.Empty)
                    {
                        try
                        {
                            _friendsStore.AddFriendship(state.AccountId, friendId);
                            _chatAndFriends.NotifySomeoneAddedYouAsFriend(state.AccountId, friendId);
                        }
                        catch
                        {
                        }
                    }

                    return new AddFriendResponse { Friend = friendId != Guid.Empty ? CreateUser(friendId, _chatAndFriends != null && _chatAndFriends.IsAccountOnline(friendId)) : null };
                }

                case "RemoveFriendRequest":
                {
                    var req = DeserializeMessage<RemoveFriendRequest>(requestPayload);
                    var friendId = req != null ? req.FriendAccountId : Guid.Empty;
                    if (state.AccountId != Guid.Empty && friendId != Guid.Empty)
                    {
                        try
                        {
                            _friendsStore.RemoveFriendship(state.AccountId, friendId);
                        }
                        catch
                        {
                        }
                    }
                    return new RemoveFriendResponse();
                }

                case "PushInvitationsRequest":
                {
                    try
                    {
                        _chatAndFriends.PushInvitationsTo(state.AccountId);
                    }
                    catch
                    {
                    }
                    return new PushInvitationsResponse();
                }

                case "CreateGroupRequest":
                {
                    var req = DeserializeMessage<CreateGroupRequest>(requestPayload);
                    Group group;
                    try
                    {
                        group = _chatAndFriends.CreateGroup(state.AccountId, req != null ? req.GroupName : null, req != null ? req.Capacity : 4, req != null && req.IsPersistent);
                    }
                    catch
                    {
                        group = EnsureGroup(state, req);
                    }
                    return new CreateGroupResponse { GroupData = group };
                }

                case "ListGroupsRequest":
                {
                    try
                    {
                        return new ListGroupsResponse { Groups = _chatAndFriends.ListGroupsFor(state.AccountId) };
                    }
                    catch
                    {
                        var group = state.Group;
                        var groups = new List<Group>();
                        if (group != null)
                        {
                            groups.Add(group);
                        }
                        return new ListGroupsResponse { Groups = groups };
                    }
                }

                case "ListGroupMembersRequest":
                {
                    var req = DeserializeMessage<ListGroupMembersRequest>(requestPayload);
                    try
                    {
                        return new ListGroupMembersResponse { Members = _chatAndFriends.ListGroupMembers(req != null ? req.GroupId : 0) };
                    }
                    catch
                    {
                        var group = state.Group;
                        var members = new List<User>();
                        if (group != null && group.Members != null)
                        {
                            members.AddRange(group.Members);
                        }
                        return new ListGroupMembersResponse { Members = members };
                    }
                }

                case "InviteToGroupRequest":
                {
                    var req = DeserializeMessage<InviteToGroupRequest>(requestPayload);
                    var code = "PlayerNotFound";
                    try
                    {
                        code = _chatAndFriends.InviteToGroup(state.AccountId, req != null ? req.GroupId : 0, req != null ? req.InviteeId : Guid.Empty);
                    }
                    catch
                    {
                    }
                    return new InviteToGroupResponse { ResultCode = code };
                }

                case "AcceptInvitationRequest":
                {
                    var req = DeserializeMessage<AcceptInvitationRequest>(requestPayload);
                    try
                    {
                        return _chatAndFriends.AcceptInvitation(state.AccountId, req != null ? req.InvitationId : 0);
                    }
                    catch
                    {
                        return new AcceptInvitationResponse { ResultCode = "InvitationNotFound", GroupData = null };
                    }
                }

                case "DeclineInvitationRequest":
                {
                    var req = DeserializeMessage<DeclineInvitationRequest>(requestPayload);
                    try
                    {
                        return _chatAndFriends.DeclineInvitation(state.AccountId, req != null ? req.InvitationId : 0, req != null ? req.Reason : null);
                    }
                    catch
                    {
                        return new DeclineInvitationResponse { ResultCode = "Success" };
                    }
                }

                case "RemoveGroupMemberRequest":
                {
                    var req = DeserializeMessage<RemoveGroupMemberRequest>(requestPayload);
                    var result = "GroupNotExists";
                    try
                    {
                        result = _chatAndFriends.RemoveGroupMember(state.AccountId, req != null ? req.GroupId : 0, req != null ? req.MemberId : Guid.Empty);
                    }
                    catch
                    {
                    }
                    return new RemoveGroupMemberResponse { ResultCode = result };
                }

                case "SetGroupDataRequest":
                {
                    var req = DeserializeMessage<SetGroupDataRequest>(requestPayload);
                    if (req != null && req.Data != null && !string.IsNullOrEmpty(req.Data.Key))
                    {
                        if (_chatAndFriends != null && req.GroupId > 0)
                        {
                            _chatAndFriends.SetGroupData(state.AccountId, req.GroupId, req.Data.Key, req.Data.Value);
                        }
                        else
                        {
                            state.GroupData[req.Data.Key] = req.Data.Value;
                        }
                    }
                    return new SetGroupDataResponse();
                }

                case "DeleteGroupDataRequest":
                {
                    var req = DeserializeMessage<DeleteGroupDataRequest>(requestPayload);
                    if (req != null && !string.IsNullOrEmpty(req.Datakey))
                    {
                        if (_chatAndFriends != null && req.GroupId > 0)
                        {
                            _chatAndFriends.DeleteGroupData(state.AccountId, req.GroupId, req.Datakey);
                        }
                        else
                        {
                            state.GroupData.Remove(req.Datakey);
                        }
                    }
                    return new DeleteGroupDataResponse();
                }

                case "GetGroupDataRequest":
                {
                    var req = DeserializeMessage<GetGroupDataRequest>(requestPayload);
                    var entries = new List<GroupDataEntry>();
                    try
                    {
                        if (_chatAndFriends != null && req != null && req.GroupId > 0)
                        {
                            var snapshot = _chatAndFriends.GetGroupDataSnapshot(req.GroupId);
                            foreach (var kvp in snapshot)
                            {
                                entries.Add(new GroupDataEntry { Key = kvp.Key, Value = kvp.Value });
                            }
                        }
                        else
                        {
                            foreach (var kvp in state.GroupData)
                            {
                                entries.Add(new GroupDataEntry { Key = kvp.Key, Value = kvp.Value });
                            }
                        }
                    }
                    catch
                    {
                    }
                    return new GetGroupDataResponse { GroupData = entries };
                }

                case "BroadcastToGroupRequest":
                {
                    var req = DeserializeMessage<BroadcastToGroupRequest>(requestPayload);
                    if (req != null && !string.IsNullOrEmpty(req.Data))
                    {
                        state.LastGroupBroadcast = req.Data;

                        try
                        {
                            if (_chatAndFriends != null && req.GroupId > 0)
                            {
                                _chatAndFriends.BroadcastToGroup(state.AccountId, req.GroupId, req.Data);
                            }
                        }
                        catch
                        {
                        }
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

        private bool TryHandleGlobalSlashCommand(ConnectionState state, SendMessageToChannelRequest req)
        {
            if (state == null || req == null || state.AccountId == Guid.Empty)
            {
                return false;
            }

            var rawText = req.TextMessage;
            if (string.IsNullOrEmpty(rawText))
            {
                return false;
            }

            var trimmed = rawText.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '/')
            {
                return false;
            }

            if (!IsGlobalChannelName(req.ChannelName))
            {
                if (_chatAndFriends != null)
                {
                    _chatAndFriends.SendTextMessageToAccount(state.AccountId, req.ChannelName, Guid.Empty, "[server] Slash commands are only available in Global chat.");
                }
                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-command",
                    action = "rejected-non-global-channel",
                    senderAccountId = state.AccountId,
                    channel = req.ChannelName ?? string.Empty,
                    text = trimmed,
                });
                return true;
            }

            var context = BuildChatCommandContext(state, req.ChannelName, trimmed);
            var result = ExecuteChatCommand(context, trimmed);

            if (!IsNullOrEmpty(result.FeedbackMessage) && _chatAndFriends != null)
            {
                _chatAndFriends.SendTextMessageToAccount(state.AccountId, req.ChannelName, Guid.Empty, "[server] " + result.FeedbackMessage);
            }

            LogAdminEvent(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "chat-command",
                action = "feedback-sent",
                senderAccountId = state.AccountId,
                channel = req.ChannelName ?? string.Empty,
                success = result.Success,
                feedback = result.FeedbackMessage ?? string.Empty,
                text = trimmed,
            });

            return true;
        }

        private ChatCommandContext BuildChatCommandContext(ConnectionState state, string channelName, string rawCommandText)
        {
            var context = new ChatCommandContext();
            context.SenderAccountId = state != null ? state.AccountId : Guid.Empty;
            context.ChannelName = channelName;
            context.RawCommandText = rawCommandText;

            var identityHash = context.SenderAccountId != Guid.Empty
                ? context.SenderAccountId.ToString("D")
                : null;
            context.SenderIdentityHash = identityHash;

            if (_userStore != null && !IsNullOrEmpty(identityHash))
            {
                try
                {
                    var index = _userStore.GetLastCareerIndex(identityHash);
                    context.ActiveCareerIndex = index;
                    context.ActiveCareerSlot = _userStore.GetOrCreateCareer(identityHash, index, false);
                }
                catch
                {
                }
            }

            return context;
        }

        private ChatCommandResult ExecuteChatCommand(ChatCommandContext context, string trimmedCommandText)
        {
            if (context == null || IsNullOrEmpty(trimmedCommandText))
            {
                return ChatCommandResult.Fail("Invalid command context.");
            }

            var body = trimmedCommandText.Length > 1 ? trimmedCommandText.Substring(1) : string.Empty;
            if (IsNullOrEmpty(body))
            {
                return ChatCommandResult.Fail("Missing command name.");
            }

            var tokens = body.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return ChatCommandResult.Fail("Missing command name.");
            }

            var name = tokens[0];
            IChatCommand command;
            if (_chatCommands == null || !_chatCommands.TryGetValue(name, out command) || command == null)
            {
                return ChatCommandResult.Fail("Unknown command '/" + name + "'.");
            }

            if (command.RequiresAdmin && !IsChatCommandAuthorized(context.SenderAccountId))
            {
                return ChatCommandResult.Fail("You do not have permission to use '/" + command.Name + "'.");
            }

            var args = new string[tokens.Length - 1];
            if (args.Length > 0)
            {
                Array.Copy(tokens, 1, args, 0, args.Length);
            }

            try
            {
                var result = command.Execute(this, context, args);
                if (_logger != null)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-command",
                        command = command.Name,
                        senderAccountId = context.SenderAccountId,
                        senderIdentity = context.SenderIdentityHash ?? string.Empty,
                        careerIndex = context.ActiveCareerIndex,
                        success = result.Success,
                    });
                }
                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-command",
                    action = "executed",
                    command = command.Name,
                    senderAccountId = context.SenderAccountId,
                    senderIdentity = context.SenderIdentityHash ?? string.Empty,
                    careerIndex = context.ActiveCareerIndex,
                    success = result.Success,
                    requiresAdmin = command.RequiresAdmin,
                });
                return result;
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.Log(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-command-error",
                        command = command.Name,
                        senderAccountId = context.SenderAccountId,
                        error = ex.Message,
                    });
                }
                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-command-error",
                    command = command.Name,
                    senderAccountId = context.SenderAccountId,
                    error = ex.Message,
                });
                return ChatCommandResult.Fail("Command failed.");
            }
        }

        private void LogAdminEvent(object payload)
        {
            if (_logger == null)
            {
                return;
            }

            try
            {
                _logger.LogAdmin(payload);
            }
            catch
            {
            }
        }

        private bool IsChatCommandAuthorized(Guid accountId)
        {
            return accountId != Guid.Empty
                && _chatAdminAccountIds != null
                && _chatAdminAccountIds.Contains(accountId);
        }

        private static bool IsGlobalChannelName(string channelName)
        {
            if (string.IsNullOrEmpty(channelName))
            {
                return false;
            }

            return string.Equals(channelName, "Global", StringComparison.OrdinalIgnoreCase)
                || string.Equals(channelName, "SRO_Default", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryResolveItemValidationInfo(string itemCode, out bool isValidItemCode, out int itemCategory)
        {
            isValidItemCode = false;
            itemCategory = 0;

            if (IsNullOrEmpty(itemCode))
            {
                return false;
            }

            var data = GetOrLoadItemValidationData();
            if (data == null)
            {
                return false;
            }

            isValidItemCode = data.ValidItemCodes.Contains(itemCode);
            if (!isValidItemCode)
            {
                return true;
            }

            data.ItemCategoryByCode.TryGetValue(itemCode, out itemCategory);
            return true;
        }

        private bool TryResolveVariantCompatibility(int variantId, int itemCategory, out bool variantExists, out bool compatible)
        {
            variantExists = false;
            compatible = false;

            var data = GetOrLoadItemValidationData();
            if (data == null)
            {
                return false;
            }

            HashSet<int> categories;
            if (!data.VariantCompatibleCategories.TryGetValue(variantId, out categories) || categories == null)
            {
                return true;
            }

            variantExists = true;
            compatible = itemCategory > 0 && categories.Contains(itemCategory);
            return true;
        }

        private ItemValidationData GetOrLoadItemValidationData()
        {
            var staticDataDir = _options != null ? _options.StaticDataDir : null;
            if (IsNullOrEmpty(staticDataDir) || !Directory.Exists(staticDataDir))
            {
                return null;
            }

            lock (ItemValidationDataLock)
            {
                if (_itemValidationData != null && string.Equals(_itemValidationDataSourceDir, staticDataDir, StringComparison.OrdinalIgnoreCase))
                {
                    return _itemValidationData;
                }

                var loaded = LoadItemValidationData(staticDataDir);
                _itemValidationData = loaded;
                _itemValidationDataSourceDir = staticDataDir;
                return loaded;
            }
        }

        private ItemValidationData LoadItemValidationData(string staticDataDir)
        {
            var result = new ItemValidationData();

            try
            {
                var path = Path.Combine(staticDataDir, "metagameplay.json");
                if (!File.Exists(path))
                {
                    return result;
                }

                var json = File.ReadAllText(path);
                if (IsNullOrEmpty(json))
                {
                    return result;
                }

                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 100;

                var root = serializer.DeserializeObject(json);
                CollectItemValidationData(root, result);

                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-item-validation",
                    action = "loaded",
                    staticDataDir = staticDataDir,
                    itemCount = result.ValidItemCodes.Count,
                    variantCount = result.VariantCompatibleCategories.Count,
                });
            }
            catch (Exception ex)
            {
                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-item-validation",
                    action = "load-failed",
                    staticDataDir = staticDataDir,
                    error = ex.Message,
                });
            }

            return result;
        }

        private static void CollectItemValidationData(object node, ItemValidationData result)
        {
            if (node == null || result == null)
            {
                return;
            }

            var dict = node as IDictionary;
            if (dict != null)
            {
                var typeName = GetStringValue(dict, "TypeName");
                var idText = GetStringValue(dict, "Id");
                if (!IsNullOrEmpty(typeName)
                    && typeName.IndexOf("ItemDefinition", StringComparison.OrdinalIgnoreCase) >= 0
                    && !IsNullOrEmpty(idText))
                {
                    result.ValidItemCodes.Add(idText);

                    var itemCategory = 0;
                    if (TryGetIntValue(dict, "ItemCategory", out itemCategory)
                        || TryGetIntValue(dict, "ItemCategoryId", out itemCategory)
                        || TryGetIntValue(dict, "SkillTreeId", out itemCategory))
                    {
                        result.ItemCategoryByCode[idText] = itemCategory;
                    }
                }

                object compatibleObj;
                var hasCompatible = dict.Contains("CompatibleCategories");
                var variantId = 0;
                if (hasCompatible && dict.Contains("Id") && TryGetInt(dict["Id"], out variantId))
                {
                    compatibleObj = dict["CompatibleCategories"];
                    var compatibleCategories = ToIntSet(compatibleObj);
                    if (compatibleCategories != null && compatibleCategories.Count > 0)
                    {
                        result.VariantCompatibleCategories[variantId] = compatibleCategories;
                    }
                }

                foreach (DictionaryEntry entry in dict)
                {
                    CollectItemValidationData(entry.Value, result);
                }

                return;
            }

            var array = node as object[];
            if (array != null)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    CollectItemValidationData(array[i], result);
                }

                return;
            }

            var list = node as ArrayList;
            if (list != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    CollectItemValidationData(list[i], result);
                }
            }
        }

        private static string GetStringValue(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrEmpty(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }

            return dict[key] as string;
        }

        private static bool TryGetIntValue(IDictionary dict, string key, out int value)
        {
            value = 0;
            if (dict == null || IsNullOrEmpty(key) || !dict.Contains(key))
            {
                return false;
            }

            return TryGetInt(dict[key], out value);
        }

        private static bool TryGetInt(object rawValue, out int value)
        {
            value = 0;
            if (rawValue == null)
            {
                return false;
            }

            if (rawValue is int)
            {
                value = (int)rawValue;
                return true;
            }

            if (rawValue is long)
            {
                var longValue = (long)rawValue;
                if (longValue < int.MinValue || longValue > int.MaxValue)
                {
                    return false;
                }

                value = (int)longValue;
                return true;
            }

            if (rawValue is double)
            {
                var dbl = (double)rawValue;
                if (dbl < int.MinValue || dbl > int.MaxValue)
                {
                    return false;
                }

                value = (int)dbl;
                return true;
            }

            if (rawValue is decimal)
            {
                var dec = (decimal)rawValue;
                if (dec < int.MinValue || dec > int.MaxValue)
                {
                    return false;
                }

                value = (int)dec;
                return true;
            }

            var asString = rawValue as string;
            if (!IsNullOrEmpty(asString))
            {
                return int.TryParse(asString, out value);
            }

            return false;
        }

        private static HashSet<int> ToIntSet(object raw)
        {
            var result = new HashSet<int>();
            if (raw == null)
            {
                return result;
            }

            var arr = raw as object[];
            if (arr != null)
            {
                for (var i = 0; i < arr.Length; i++)
                {
                    var value = 0;
                    if (TryGetInt(arr[i], out value))
                    {
                        result.Add(value);
                    }
                }

                return result;
            }

            var list = raw as ArrayList;
            if (list != null)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var value = 0;
                    if (TryGetInt(list[i], out value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }

        private static bool IsNullOrEmpty(string value)
        {
            return string.IsNullOrEmpty(value);
        }

        private HashSet<Guid> LoadChatAdminAccountIds(LocalServiceOptions options)
        {
            var result = new HashSet<Guid>();
            if (options == null)
            {
                return result;
            }

            var path = options.ChatAdminConfigPath;
            if (string.IsNullOrEmpty(path))
            {
                if (!string.IsNullOrEmpty(options.DataDir))
                {
                    path = Path.Combine(options.DataDir, "chat-admins.json");
                }
                else
                {
                    return result;
                }
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(path))
                {
                    File.WriteAllText(path, "{\r\n  \"admins\": []\r\n}\r\n");
                    LogAdminEvent(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-admin-config",
                        action = "created-placeholder",
                        path = path,
                    });
                    return result;
                }

                var json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json) || json.Trim().Length == 0)
                {
                    File.WriteAllText(path, "{\r\n  \"admins\": []\r\n}\r\n");
                    LogAdminEvent(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-admin-config",
                        action = "rewrote-empty-placeholder",
                        path = path,
                    });
                    return result;
                }

                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(json);

                object[] adminsArray = null;
                var dict = root as Dictionary<string, object>;
                if (dict != null)
                {
                    object adminsRaw;
                    if (dict.TryGetValue("admins", out adminsRaw))
                    {
                        adminsArray = adminsRaw as object[];
                    }
                }
                else
                {
                    adminsArray = root as object[];
                }

                if (adminsArray != null)
                {
                    for (var i = 0; i < adminsArray.Length; i++)
                    {
                        var value = adminsArray[i] as string;
                        Guid parsed;
                        if (TryParseGuid(value, out parsed) && parsed != Guid.Empty)
                        {
                            result.Add(parsed);
                        }
                    }
                }

                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-admin-config",
                    action = "loaded",
                    path = path,
                    count = result.Count,
                });
            }
            catch (Exception ex)
            {
                LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-admin-config",
                    action = "load-failed",
                    path = path,
                    error = ex.Message,
                });
            }

            return result;
        }

        private static bool TryParseGuid(string value, out Guid parsed)
        {
            parsed = Guid.Empty;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                parsed = new Guid(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, IChatCommand> BuildChatCommandMap()
        {
            var map = new Dictionary<string, IChatCommand>(StringComparer.OrdinalIgnoreCase);
            RegisterChatCommand(map, new HelpChatCommand());
            RegisterChatCommand(map, new SetBalanceCommand("setkarma", true));
            RegisterChatCommand(map, new SetBalanceCommand("setnuyen", false));
            RegisterChatCommand(map, new AddItemCommand());
            return map;
        }

        private static void RegisterChatCommand(Dictionary<string, IChatCommand> map, IChatCommand command)
        {
            if (map == null || command == null || IsNullOrEmpty(command.Name))
            {
                return;
            }

            map[command.Name] = command;
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
            return CreateUser(accountId, true);
        }

        private static User CreateUser(Guid accountId, bool isOnline)
        {
            return new User
            {
                AccountId = accountId,
                IsOnline = isOnline,
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
            public Guid ConnectionId;
            public string Endpoint;
            public NetworkStream Stream;

            public Guid AccountId;
            public User LocalUser;
            public string LastChannelName;

            public int GroupId = 1;
            public Group Group;
            public readonly Dictionary<string, string> GroupData = new Dictionary<string, string>(StringComparer.Ordinal);
            public string LastGroupBroadcast;
        }

        private sealed class ChatCommandContext
        {
            public Guid SenderAccountId;
            public string SenderIdentityHash;
            public int ActiveCareerIndex;
            public CareerSlot ActiveCareerSlot;
            public string ChannelName;
            public string RawCommandText;
        }

        private sealed class ChatCommandResult
        {
            public bool Success;
            public string FeedbackMessage;

            public static ChatCommandResult Ok(string message)
            {
                return new ChatCommandResult
                {
                    Success = true,
                    FeedbackMessage = message,
                };
            }

            public static ChatCommandResult Fail(string message)
            {
                return new ChatCommandResult
                {
                    Success = false,
                    FeedbackMessage = message,
                };
            }
        }

        private interface IChatCommand
        {
            string Name { get; }
            bool RequiresAdmin { get; }
            ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args);
        }

        private sealed class HelpChatCommand : IChatCommand
        {
            public string Name { get { return "help"; } }
            public bool RequiresAdmin { get { return false; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                var isAdmin = owner.IsChatCommandAuthorized(context.SenderAccountId);
                if (isAdmin)
                {
                    return ChatCommandResult.Ok("Commands: /help, /setkarma {X}, /setnuyen {X}, /additem {ItemCode} [Variant]");
                }

                return ChatCommandResult.Ok("Commands: /help");
            }
        }

        private sealed class SetBalanceCommand : IChatCommand
        {
            private readonly string _name;
            private readonly bool _isKarma;

            public SetBalanceCommand(string name, bool isKarma)
            {
                _name = name;
                _isKarma = isKarma;
            }

            public string Name { get { return _name; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (args == null || args.Length != 1)
                {
                    return ChatCommandResult.Fail("Usage: /" + _name + " {X}");
                }

                int value;
                if (!int.TryParse(args[0], out value) || value < 0)
                {
                    return ChatCommandResult.Fail("Value must be a non-negative integer.");
                }

                if (owner._userStore == null || IsNullOrEmpty(context.SenderIdentityHash))
                {
                    return ChatCommandResult.Fail("Unable to resolve sender identity.");
                }

                var slot = context.ActiveCareerSlot;
                if (slot == null)
                {
                    return ChatCommandResult.Fail("Unable to resolve active character.");
                }

                if (_isKarma)
                {
                    slot.Karma = value;
                }
                else
                {
                    slot.Nuyen = value;
                }

                owner._userStore.UpsertCareer(context.SenderIdentityHash, slot);

                if (owner._characterStatePushBroker != null)
                {
                    owner._characterStatePushBroker.Enqueue(
                        context.SenderAccountId,
                        CharacterStatePushPaths.Wallet | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries);
                }

                var label = _isKarma ? "karma" : "nuyen";
                return ChatCommandResult.Ok("Set " + label + " to " + value.ToString() + " for career slot " + context.ActiveCareerIndex.ToString() + ".");
            }
        }

        private sealed class AddItemCommand : IChatCommand
        {
            public string Name { get { return "additem"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (args == null || args.Length < 1 || args.Length > 2)
                {
                    return ChatCommandResult.Fail("Usage: /additem {ItemCode} [Variant]");
                }

                var itemCode = args[0] != null ? args[0].Trim() : string.Empty;
                if (IsNullOrEmpty(itemCode))
                {
                    return ChatCommandResult.Fail("Item code is required.");
                }

                var isValidItemCode = false;
                var itemCategory = 0;
                var canValidate = owner.TryResolveItemValidationInfo(itemCode, out isValidItemCode, out itemCategory);
                if (!canValidate)
                {
                    return ChatCommandResult.Fail("Unable to validate item data from static-data.");
                }

                if (!isValidItemCode)
                {
                    return ChatCommandResult.Fail("Unknown item code '" + itemCode + "'.");
                }

                var quality = 0;
                var variant = -1;
                if (args.Length == 2)
                {
                    if (!int.TryParse(args[1], out variant))
                    {
                        return ChatCommandResult.Fail("Variant must be an integer between 2 and 405.");
                    }

                    if (variant < 2 || variant > 405)
                    {
                        return ChatCommandResult.Fail("Variant must be between 2 and 405.");
                    }

                    quality = variant <= 319 ? 1 : 2;

                    if (itemCategory <= 0)
                    {
                        return ChatCommandResult.Fail("Item '" + itemCode + "' does not expose an item category for variant compatibility checks.");
                    }

                    var variantExists = false;
                    var isCompatible = false;
                    var canResolveCompatibility = owner.TryResolveVariantCompatibility(variant, itemCategory, out variantExists, out isCompatible);
                    if (!canResolveCompatibility)
                    {
                        return ChatCommandResult.Fail("Unable to validate variant compatibility from static-data.");
                    }

                    if (!variantExists)
                    {
                        return ChatCommandResult.Fail("Unknown variant id '" + variant.ToString() + "'.");
                    }

                    if (!isCompatible)
                    {
                        return ChatCommandResult.Fail("Variant " + variant.ToString() + " is not compatible with item category " + itemCategory.ToString() + ".");
                    }
                }

                if (owner._userStore == null || IsNullOrEmpty(context.SenderIdentityHash))
                {
                    return ChatCommandResult.Fail("Unable to resolve sender identity.");
                }

                var slot = context.ActiveCareerSlot;
                if (slot == null)
                {
                    return ChatCommandResult.Fail("Unable to resolve active character.");
                }

                if (slot.ItemPossessions == null)
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.Ordinal);
                }

                var possessionKey = itemCode + "|" + quality.ToString() + "|" + variant.ToString();
                int existing;
                if (!slot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
                {
                    existing = 0;
                }

                var next = existing;
                try
                {
                    next = checked(existing + 1);
                }
                catch
                {
                    next = int.MaxValue;
                }

                slot.ItemPossessions[possessionKey] = next;
                owner._userStore.UpsertCareer(context.SenderIdentityHash, slot);

                if (owner._characterStatePushBroker != null)
                {
                    owner._characterStatePushBroker.Enqueue(
                        context.SenderAccountId,
                        CharacterStatePushPaths.Inventory | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries);
                }

                if (variant >= 0)
                {
                    return ChatCommandResult.Ok("Added 1x " + itemCode + " (variant " + variant.ToString() + ", quality " + quality.ToString() + ") to career slot " + context.ActiveCareerIndex.ToString() + ".");
                }

                return ChatCommandResult.Ok("Added 1x " + itemCode + " to career slot " + context.ActiveCareerIndex.ToString() + ".");
            }
        }

        private sealed class ItemValidationData
        {
            public readonly HashSet<string> ValidItemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ItemCategoryByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, HashSet<int>> VariantCompatibleCategories = new Dictionary<int, HashSet<int>>();
        }
    }
}
