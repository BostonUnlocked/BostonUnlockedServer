using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
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
    private static readonly object MainCampaignStorylineLock = new object();
    private static MainCampaignStoryline _mainCampaignStoryline;
    private static string _mainCampaignStorylineSourceDir;
    private const int SlashCommandFeedbackMaxLinesPerPage = 12;
    private const int SlashCommandFeedbackMaxCharsPerPage = 1200;

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
                    _logger.UpdateConnectionAccountId("photon", state != null ? state.Endpoint : null, state != null ? state.ConnectionHash : null, accountId);
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

                    PartyHubFollowRegistry.ClearForMember(state.AccountId);
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
                        var response = _chatAndFriends.AcceptInvitation(state.AccountId, req != null ? req.InvitationId : 0);
                        Guid hostAccountId;
                        if (response != null && string.Equals(response.ResultCode, "Success", StringComparison.OrdinalIgnoreCase)
                            && TryResolveGroupHostAccountId(response.GroupData, state.AccountId, out hostAccountId))
                        {
                            PartyHubFollowRegistry.SetHostForMember(state.AccountId, hostAccountId);
                            _logger.UpdateConnectionHostAccountId("photon", state != null ? state.Endpoint : null, state != null ? state.ConnectionHash : null, hostAccountId);
                        }
                        return response;
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
                        if (string.Equals(result, "Success", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(result, "Ok", StringComparison.OrdinalIgnoreCase))
                        {
                            if (req != null && req.MemberId != Guid.Empty)
                            {
                                PartyHubFollowRegistry.ClearForMember(req.MemberId);
                            }

                            if (req == null || req.MemberId == Guid.Empty || req.MemberId == state.AccountId)
                            {
                                PartyHubFollowRegistry.ClearForMember(state.AccountId);
                            }
                        }
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
                if (!IsBugCommandText(trimmed))
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
            }

            var context = BuildChatCommandContext(state, req.ChannelName, trimmed);
            var result = ExecuteChatCommand(context, trimmed);
            var feedbackMessages = EnumerateFeedbackMessages(result).ToArray();

            if (_chatAndFriends != null)
            {
                for (var i = 0; i < feedbackMessages.Length; i++)
                {
                    _chatAndFriends.SendTextMessageToAccount(state.AccountId, req.ChannelName, Guid.Empty, "[server] " + feedbackMessages[i]);
                }
            }

            LogAdminEvent(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "chat-command",
                action = "feedback-sent",
                senderAccountId = state.AccountId,
                channel = req.ChannelName ?? string.Empty,
                success = result != null && result.Success,
                feedbackCount = feedbackMessages.Length,
                feedback = feedbackMessages.Length > 0 ? feedbackMessages[0] : string.Empty,
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
                var result = command.Execute(this, context, args) ?? ChatCommandResult.Fail("Command failed.");
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
                        feedbackCount = CountFeedbackMessages(result),
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
                    feedbackCount = CountFeedbackMessages(result),
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

        private static bool IsBugCommandText(string trimmedText)
        {
            if (string.IsNullOrEmpty(trimmedText))
            {
                return false;
            }

            if (!trimmedText.StartsWith("/bug", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (trimmedText.Length == 4)
            {
                return true;
            }

            var next = trimmedText[4];
            return next == ' ' || next == '\t' || next == '\r' || next == '\n';
        }

        private static IEnumerable<string> EnumerateFeedbackMessages(ChatCommandResult result)
        {
            if (result == null)
            {
                yield break;
            }

            if (result.FeedbackMessages != null && result.FeedbackMessages.Length > 0)
            {
                for (var i = 0; i < result.FeedbackMessages.Length; i++)
                {
                    var message = result.FeedbackMessages[i];
                    if (!IsNullOrEmpty(message))
                    {
                        yield return message;
                    }
                }

                yield break;
            }

            if (!IsNullOrEmpty(result.FeedbackMessage))
            {
                yield return result.FeedbackMessage;
            }
        }

        private static int CountFeedbackMessages(ChatCommandResult result)
        {
            var count = 0;
            foreach (var ignored in EnumerateFeedbackMessages(result))
            {
                count++;
            }

            return count;
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
            RegisterChatCommand(map, new BugReportChatCommand());
            RegisterChatCommand(map, new AnnounceChatCommand());
            RegisterChatCommand(map, new ActiveMissionsChatCommand());
            RegisterChatCommand(map, new OnlinePlayersChatCommand());
            RegisterChatCommand(map, new ListPlayersChatCommand());
            RegisterChatCommand(map, new SetAvailableMissionCommand());
            RegisterChatCommand(map, new GetBalanceCommand("getkarma", true, ChatCommandTargetMode.Self));
            RegisterChatCommand(map, new GetBalanceCommand("getnuyen", false, ChatCommandTargetMode.Self));
            RegisterChatCommand(map, new SetBalanceCommand("setkarma", true, ChatCommandTargetMode.Self));
            RegisterChatCommand(map, new SetBalanceCommand("setnuyen", false, ChatCommandTargetMode.Self));
            RegisterChatCommand(map, new AddItemCommand("additem", ChatCommandTargetMode.Self));
            RegisterChatCommand(map, new GetBalanceCommand("othergetkarma", true, ChatCommandTargetMode.OtherByAccountId));
            RegisterChatCommand(map, new GetBalanceCommand("othergetnuyen", false, ChatCommandTargetMode.OtherByAccountId));
            RegisterChatCommand(map, new SetBalanceCommand("othersetkarma", true, ChatCommandTargetMode.OtherByAccountId));
            RegisterChatCommand(map, new SetBalanceCommand("othersetnuyen", false, ChatCommandTargetMode.OtherByAccountId));
            RegisterChatCommand(map, new AddItemCommand("otheradditem", ChatCommandTargetMode.OtherByAccountId));
            return map;
        }

        private bool TrySetAvailableMissionForCareer(ChatCommandContext context, string missionId, StoryMissionstate targetState, out string message)
        {
            message = "Command failed.";

            if (context == null || IsNullOrEmpty(context.SenderIdentityHash) || _userStore == null)
            {
                message = "Unable to resolve active character.";
                return false;
            }

            if (IsNullOrEmpty(missionId))
            {
                message = "Mission id is required.";
                return false;
            }

            MainCampaignMissionPlan plan;
            string resolveError;
            if (!TryResolveMainCampaignMissionPlan(missionId.Trim(), targetState, out plan, out resolveError) || plan == null)
            {
                message = IsNullOrEmpty(resolveError) ? ("Unknown mission '" + missionId + "'.") : resolveError;
                return false;
            }

            CareerSlot slot;
            try
            {
                slot = _userStore.GetOrCreateCareer(context.SenderIdentityHash, context.ActiveCareerIndex, false);
            }
            catch
            {
                slot = null;
            }

            if (slot == null)
            {
                message = "Unable to resolve active character.";
                return false;
            }

            slot.MainCampaignCurrentChapter = plan.TargetChapterIndex;
            slot.MainCampaignMissionStates = new Dictionary<string, string>(plan.MissionStates, StringComparer.OrdinalIgnoreCase);
            slot.MainCampaignInteractedNpcs = new List<string>();
            if (!IsNullOrEmpty(plan.TargetHub))
            {
                slot.HubId = plan.TargetHub;
            }

            _userStore.UpsertCareer(context.SenderIdentityHash, slot);
            context.ActiveCareerSlot = slot;

            if (_characterStatePushBroker != null)
            {
                _characterStatePushBroker.Enqueue(
                    context.SenderAccountId,
                    CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries);
            }

            message = "Set mission '" + plan.TargetMissionId + "' to " + plan.TargetMissionState
                + " for career slot " + context.ActiveCareerIndex.ToString()
                + " (completed prior missions: " + plan.CompletedBeforeCount.ToString() + ", cleared later missions: " + plan.ClearedAfterCount.ToString() + ").";
            return true;
        }

        private bool TryResolveMainCampaignMissionPlan(string missionId, StoryMissionstate targetState, out MainCampaignMissionPlan plan, out string error)
        {
            plan = null;
            error = null;

            if (IsNullOrEmpty(missionId))
            {
                error = "Mission id is required.";
                return false;
            }

            var storyline = GetOrLoadMainCampaignStoryline();
            if (storyline == null || storyline.Chapters == null || storyline.Chapters.Count == 0 || storyline.OrderedMissionIds == null || storyline.OrderedMissionIds.Count == 0)
            {
                error = "Main campaign mission data is unavailable (static-data).";
                return false;
            }

            var missionKey = missionId.Trim();
            int targetOrderedIndex;
            if (!storyline.OrderedIndexByMissionId.TryGetValue(missionKey, out targetOrderedIndex) || targetOrderedIndex < 0)
            {
                var suggestions = new List<string>();
                for (var i = 0; i < storyline.OrderedMissionIds.Count; i++)
                {
                    var candidate = storyline.OrderedMissionIds[i];
                    if (IsNullOrEmpty(candidate))
                    {
                        continue;
                    }
                    if (candidate.IndexOf(missionKey, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        suggestions.Add(candidate);
                        if (suggestions.Count >= 5)
                        {
                            break;
                        }
                    }
                }

                if (suggestions.Count > 0)
                {
                    error = "Unknown mission '" + missionId + "'. Similar: " + string.Join(", ", suggestions.ToArray()) + ".";
                }
                else
                {
                    error = "Unknown mission '" + missionId + "'.";
                }
                return false;
            }

            int targetChapterIndex;
            if (!storyline.ChapterIndexByMissionId.TryGetValue(missionKey, out targetChapterIndex) || targetChapterIndex < 0 || targetChapterIndex >= storyline.Chapters.Count)
            {
                error = "Unable to resolve chapter for mission '" + missionId + "'.";
                return false;
            }

            var missionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < storyline.OrderedMissionIds.Count; i++)
            {
                var currentMission = storyline.OrderedMissionIds[i];
                if (IsNullOrEmpty(currentMission))
                {
                    continue;
                }

                if (i < targetOrderedIndex)
                {
                    missionStates[currentMission] = StoryMissionstate.Completed.ToString();
                    continue;
                }

                if (i == targetOrderedIndex)
                {
                    missionStates[currentMission] = targetState.ToString();
                    break;
                }

                break;
            }

            var targetHub = storyline.Chapters[targetChapterIndex] != null ? storyline.Chapters[targetChapterIndex].Hub : null;
            var completedBefore = targetOrderedIndex;
            var clearedAfter = storyline.OrderedMissionIds.Count - targetOrderedIndex - 1;
            if (clearedAfter < 0)
            {
                clearedAfter = 0;
            }

            plan = new MainCampaignMissionPlan();
            plan.TargetMissionId = missionKey;
            plan.TargetMissionState = targetState.ToString();
            plan.TargetChapterIndex = targetChapterIndex;
            plan.TargetHub = targetHub;
            plan.CompletedBeforeCount = completedBefore;
            plan.ClearedAfterCount = clearedAfter;
            plan.MissionStates = missionStates;
            return true;
        }

        private MainCampaignStoryline GetOrLoadMainCampaignStoryline()
        {
            var staticDataDir = _options != null ? _options.StaticDataDir : null;
            if (IsNullOrEmpty(staticDataDir) || !Directory.Exists(staticDataDir))
            {
                return null;
            }

            lock (MainCampaignStorylineLock)
            {
                if (_mainCampaignStoryline != null && string.Equals(_mainCampaignStorylineSourceDir, staticDataDir, StringComparison.OrdinalIgnoreCase))
                {
                    return _mainCampaignStoryline;
                }

                _mainCampaignStoryline = LoadMainCampaignStoryline(staticDataDir);
                _mainCampaignStorylineSourceDir = staticDataDir;
                return _mainCampaignStoryline;
            }
        }

        private static MainCampaignStoryline LoadMainCampaignStoryline(string staticDataDir)
        {
            if (IsNullOrEmpty(staticDataDir) || !Directory.Exists(staticDataDir))
            {
                return null;
            }

            var metagameplayPath = Path.Combine(staticDataDir, "metagameplay.json");
            if (!File.Exists(metagameplayPath))
            {
                return null;
            }

            string json;
            try
            {
                json = File.ReadAllText(metagameplayPath);
            }
            catch
            {
                return null;
            }

            if (IsNullOrEmpty(json))
            {
                return null;
            }

            object root;
            try
            {
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                serializer.RecursionLimit = 100;
                root = serializer.DeserializeObject(json);
            }
            catch
            {
                return null;
            }

            var components = CoerceObjectArray(root);
            if (components == null || components.Length == 0)
            {
                return null;
            }

            for (var i = 0; i < components.Length; i++)
            {
                var comp = components[i] as IDictionary;
                if (comp == null)
                {
                    continue;
                }

                if (!comp.Contains("Storylines") || comp["Storylines"] == null)
                {
                    continue;
                }

                var storylines = CoerceObjectArray(comp["Storylines"]);
                if (storylines == null || storylines.Length == 0)
                {
                    continue;
                }

                for (var s = 0; s < storylines.Length; s++)
                {
                    var storylineDict = storylines[s] as IDictionary;
                    if (storylineDict == null)
                    {
                        continue;
                    }

                    var technicalName = storylineDict.Contains("TechnicalName") ? (storylineDict["TechnicalName"] as string) : null;
                    if (!string.Equals(technicalName, "Main Campaign", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var result = new MainCampaignStoryline();
                    var chapters = CoerceObjectArray(storylineDict.Contains("Chapters") ? storylineDict["Chapters"] : null);
                    if (chapters == null)
                    {
                        chapters = new object[0];
                    }

                    for (var c = 0; c < chapters.Length; c++)
                    {
                        var chapterDict = chapters[c] as IDictionary;
                        if (chapterDict == null)
                        {
                            continue;
                        }

                        var chapter = new MainCampaignChapter();
                        chapter.Index = c;
                        chapter.Hub = chapterDict.Contains("Hub") ? (chapterDict["Hub"] as string) : null;

                        var missionRefs = CoerceObjectArray(chapterDict.Contains("RequiredMissionsForNextChapter") ? chapterDict["RequiredMissionsForNextChapter"] : null);
                        if (missionRefs != null)
                        {
                            for (var m = 0; m < missionRefs.Length; m++)
                            {
                                var missionRef = missionRefs[m] as IDictionary;
                                if (missionRef == null)
                                {
                                    continue;
                                }

                                var missionId = missionRef.Contains("Mission") ? (missionRef["Mission"] as string) : null;
                                if (IsNullOrEmpty(missionId))
                                {
                                    continue;
                                }

                                if (!chapter.RequiredMissionIds.Contains(missionId))
                                {
                                    chapter.RequiredMissionIds.Add(missionId);
                                }

                                if (!result.ChapterIndexByMissionId.ContainsKey(missionId))
                                {
                                    result.ChapterIndexByMissionId[missionId] = c;
                                }

                                if (!result.OrderedIndexByMissionId.ContainsKey(missionId))
                                {
                                    result.OrderedIndexByMissionId[missionId] = result.OrderedMissionIds.Count;
                                    result.OrderedMissionIds.Add(missionId);
                                }
                            }
                        }

                        var sideMissionRefs = CoerceObjectArray(chapterDict.Contains("SideMissions") ? chapterDict["SideMissions"] : null);
                        if (sideMissionRefs != null)
                        {
                            for (var sm = 0; sm < sideMissionRefs.Length; sm++)
                            {
                                var sideMissionRef = sideMissionRefs[sm] as IDictionary;
                                if (sideMissionRef == null)
                                {
                                    continue;
                                }

                                var sideMissionId = sideMissionRef.Contains("Mission") ? (sideMissionRef["Mission"] as string) : null;
                                if (IsNullOrEmpty(sideMissionId))
                                {
                                    continue;
                                }

                                if (!result.ChapterIndexByMissionId.ContainsKey(sideMissionId))
                                {
                                    result.ChapterIndexByMissionId[sideMissionId] = c;
                                }

                                if (!result.OrderedIndexByMissionId.ContainsKey(sideMissionId))
                                {
                                    result.OrderedIndexByMissionId[sideMissionId] = result.OrderedMissionIds.Count;
                                    result.OrderedMissionIds.Add(sideMissionId);
                                }
                            }
                        }

                        result.Chapters.Add(chapter);
                    }

                    return result;
                }
            }

            return null;
        }

        private static object[] CoerceObjectArray(object raw)
        {
            if (raw == null)
            {
                return null;
            }

            var rootDict = raw as IDictionary;
            if (rootDict != null && rootDict.Contains("Components") && rootDict["Components"] != null)
            {
                raw = rootDict["Components"];
            }

            var arr = raw as object[];
            if (arr != null)
            {
                return arr;
            }

            var list = raw as ArrayList;
            if (list == null)
            {
                return null;
            }

            var copy = new object[list.Count];
            list.CopyTo(copy);
            return copy;
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

        private static bool TryResolveGroupHostAccountId(Group group, Guid requesterAccountId, out Guid hostAccountId)
        {
            hostAccountId = Guid.Empty;
            if (group == null || group.Members == null || group.Members.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < group.Members.Count; i++)
            {
                var member = group.Members[i];
                if (member == null || member.AccountId == Guid.Empty || member.AccountId == requesterAccountId)
                {
                    continue;
                }

                hostAccountId = member.AccountId;
                return true;
            }

            return false;
        }

        private bool TryResolveSenderCommandTarget(ChatCommandContext context, out ChatCommandTarget target, out string error)
        {
            target = null;
            error = "Unable to resolve sender identity.";

            if (context == null || context.SenderAccountId == Guid.Empty)
            {
                return false;
            }

            var identityHash = !IsNullOrEmpty(context.SenderIdentityHash)
                ? context.SenderIdentityHash
                : context.SenderAccountId.ToString("D");
            var careerIndex = context.ActiveCareerIndex;
            var slot = context.ActiveCareerSlot;

            if (slot == null && _userStore != null && !IsNullOrEmpty(identityHash))
            {
                try
                {
                    slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, false);
                }
                catch
                {
                    slot = null;
                }
            }

            if (slot == null)
            {
                error = "Unable to resolve active character.";
                return false;
            }

            HubPresenceRegistry.Participant participant;
            participant = null;
            if (_hubPresenceRegistry != null)
            {
                _hubPresenceRegistry.TryGetParticipantForAccount(context.SenderAccountId, out participant);
            }

            target = BuildChatCommandTarget(context.SenderAccountId, identityHash, careerIndex, slot, participant);
            error = null;
            return true;
        }

        private bool TryResolveConnectedCommandTarget(string accountIdText, out ChatCommandTarget target, out string error)
        {
            target = null;
            error = "Usage requires an account id.";

            Guid accountId;
            if (!TryParseGuid(accountIdText, out accountId) || accountId == Guid.Empty)
            {
                error = "AccountId must be a valid GUID.";
                return false;
            }

            if (_chatAndFriends == null)
            {
                error = "Chat service is unavailable.";
                return false;
            }

            if (!_chatAndFriends.IsAccountOnline(accountId))
            {
                error = "Target account is not currently connected.";
                return false;
            }

            var identityHash = accountId.ToString("D");
            if (_userStore == null)
            {
                error = "Persistence service is unavailable.";
                return false;
            }

            HubPresenceRegistry.Participant participant;
            participant = null;
            if (_hubPresenceRegistry != null)
            {
                _hubPresenceRegistry.TryGetParticipantForAccount(accountId, out participant);
            }

            var careerIndex = participant != null ? participant.CareerIndex : 0;
            if (participant == null)
            {
                try
                {
                    careerIndex = _userStore.GetLastCareerIndex(identityHash);
                }
                catch
                {
                    careerIndex = 0;
                }
            }

            CareerSlot slot;
            try
            {
                slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, false);
            }
            catch
            {
                slot = null;
            }

            if (slot == null)
            {
                error = "Unable to resolve the target's active character.";
                return false;
            }

            target = BuildChatCommandTarget(accountId, identityHash, careerIndex, slot, participant);
            error = null;
            return true;
        }

        private ChatCommandTarget BuildChatCommandTarget(Guid accountId, string identityHash, int careerIndex, CareerSlot slot, HubPresenceRegistry.Participant participant)
        {
            var target = new ChatCommandTarget();
            target.AccountId = accountId;
            target.IdentityHash = !IsNullOrEmpty(identityHash) ? identityHash : (accountId != Guid.Empty ? accountId.ToString("D") : string.Empty);
            target.ActiveCareerIndex = careerIndex;
            target.ActiveCareerSlot = slot;
            target.CharacterId = participant != null && !IsNullOrEmpty(participant.CharacterId)
                ? participant.CharacterId
                : (slot != null ? (slot.CharacterIdentifier ?? string.Empty) : string.Empty);
            target.HubId = participant != null && !IsNullOrEmpty(participant.HubId)
                ? participant.HubId
                : (slot != null ? (slot.HubId ?? string.Empty) : string.Empty);
            target.CharacterName = ResolveChatCommandCharacterName(accountId, participant, slot);
            return target;
        }

        private string ResolveChatCommandCharacterName(Guid accountId, HubPresenceRegistry.Participant participant, CareerSlot slot)
        {
            if (participant != null && !IsNullOrEmpty(participant.CharacterName))
            {
                return participant.CharacterName.Trim();
            }

            if (slot != null && !IsNullOrEmpty(slot.CharacterName))
            {
                return slot.CharacterName.Trim();
            }

            if (_userStore != null && accountId != Guid.Empty)
            {
                try
                {
                    var displayName = _userStore.GetDisplayName(accountId.ToString("D"));
                    if (!IsNullOrEmpty(displayName))
                    {
                        return displayName.Trim();
                    }
                }
                catch
                {
                }
            }

            return "UnknownCharacter";
        }

        private bool TryGetBalanceForTarget(ChatCommandTarget target, bool isKarma, out int value, out string message)
        {
            value = 0;
            message = "Unable to resolve active character.";
            if (target == null || target.ActiveCareerSlot == null)
            {
                return false;
            }

            value = isKarma ? target.ActiveCareerSlot.Karma : target.ActiveCareerSlot.Nuyen;
            var label = isKarma ? "Karma" : "Nuyen";
            message = label + " for " + FormatChatCommandTarget(target) + ": " + value.ToString() + ".";
            return true;
        }

        private bool TrySetBalanceForTarget(ChatCommandTarget target, int value, bool isKarma, out string message)
        {
            message = "Unable to resolve active character.";
            if (target == null || target.ActiveCareerSlot == null || _userStore == null || IsNullOrEmpty(target.IdentityHash))
            {
                return false;
            }

            if (isKarma)
            {
                target.ActiveCareerSlot.Karma = value;
            }
            else
            {
                target.ActiveCareerSlot.Nuyen = value;
            }

            _userStore.UpsertCareer(target.IdentityHash, target.ActiveCareerSlot);

            if (_characterStatePushBroker != null)
            {
                _characterStatePushBroker.Enqueue(
                    target.AccountId,
                    CharacterStatePushPaths.Wallet | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries);
            }

            var label = isKarma ? "karma" : "nuyen";
            message = "Set " + label + " to " + value.ToString() + " for " + FormatChatCommandTarget(target)
                + ", career slot " + target.ActiveCareerIndex.ToString() + ".";
            return true;
        }

        private bool TryAddItemForTarget(ChatCommandTarget target, string itemCode, int? variantValue, out int appliedVariant, out int quality, out string message)
        {
            appliedVariant = -1;
            quality = 0;
            message = "Unable to resolve active character.";

            if (target == null || target.ActiveCareerSlot == null || _userStore == null || IsNullOrEmpty(target.IdentityHash))
            {
                return false;
            }

            if (IsNullOrEmpty(itemCode))
            {
                message = "Item code is required.";
                return false;
            }

            var isValidItemCode = false;
            var itemCategory = 0;
            var canValidate = TryResolveItemValidationInfo(itemCode, out isValidItemCode, out itemCategory);
            if (!canValidate)
            {
                message = "Unable to validate item data from static-data.";
                return false;
            }

            if (!isValidItemCode)
            {
                message = "Unknown item code '" + itemCode + "'.";
                return false;
            }

            if (variantValue.HasValue)
            {
                appliedVariant = variantValue.Value;
                if (appliedVariant < 2 || appliedVariant > 405)
                {
                    message = "Variant must be between 2 and 405.";
                    return false;
                }

                quality = appliedVariant <= 319 ? 1 : 2;
                if (itemCategory <= 0)
                {
                    message = "Item '" + itemCode + "' does not expose an item category for variant compatibility checks.";
                    return false;
                }

                var variantExists = false;
                var isCompatible = false;
                var canResolveCompatibility = TryResolveVariantCompatibility(appliedVariant, itemCategory, out variantExists, out isCompatible);
                if (!canResolveCompatibility)
                {
                    message = "Unable to validate variant compatibility from static-data.";
                    return false;
                }

                if (!variantExists)
                {
                    message = "Unknown variant id '" + appliedVariant.ToString() + "'.";
                    return false;
                }

                if (!isCompatible)
                {
                    message = "Variant " + appliedVariant.ToString() + " is not compatible with item category " + itemCategory.ToString() + ".";
                    return false;
                }
            }

            if (target.ActiveCareerSlot.ItemPossessions == null)
            {
                target.ActiveCareerSlot.ItemPossessions = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            var possessionKey = itemCode + "|" + quality.ToString() + "|" + appliedVariant.ToString();
            int existing;
            if (!target.ActiveCareerSlot.ItemPossessions.TryGetValue(possessionKey, out existing) || existing < 0)
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

            target.ActiveCareerSlot.ItemPossessions[possessionKey] = next;
            _userStore.UpsertCareer(target.IdentityHash, target.ActiveCareerSlot);

            if (_characterStatePushBroker != null)
            {
                _characterStatePushBroker.Enqueue(
                    target.AccountId,
                    CharacterStatePushPaths.Inventory | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries);
            }

            if (appliedVariant >= 0)
            {
                message = "Added 1x " + itemCode + " (variant " + appliedVariant.ToString() + ", quality " + quality.ToString() + ") to "
                    + FormatChatCommandTarget(target) + ", career slot " + target.ActiveCareerIndex.ToString() + ".";
                return true;
            }

            message = "Added 1x " + itemCode + " to " + FormatChatCommandTarget(target)
                + ", career slot " + target.ActiveCareerIndex.ToString() + ".";
            return true;
        }

        private ChatCommandPlayerSummary[] BuildConnectedPlayerSummaries(Guid requesterAccountId)
        {
            if (_chatAndFriends == null)
            {
                return new ChatCommandPlayerSummary[0];
            }

            var onlineAccountIds = _chatAndFriends.GetOnlineAccountIds() ?? new Guid[0];
            if (onlineAccountIds.Length == 0)
            {
                return new ChatCommandPlayerSummary[0];
            }

            var participants = _hubPresenceRegistry != null
                ? _hubPresenceRegistry.SnapshotParticipants()
                : new HubPresenceRegistry.Participant[0];
            var participantByAccountId = new Dictionary<Guid, HubPresenceRegistry.Participant>();
            for (var i = 0; i < participants.Length; i++)
            {
                var participant = participants[i];
                if (participant == null || participant.AccountId == Guid.Empty)
                {
                    continue;
                }

                HubPresenceRegistry.Participant existing;
                if (!participantByAccountId.TryGetValue(participant.AccountId, out existing)
                    || CompareParticipantRichness(participant, existing) > 0)
                {
                    participantByAccountId[participant.AccountId] = participant;
                }
            }

            var requesterParty = new HashSet<Guid>(_chatAndFriends.GetGroupMemberAccountIds(requesterAccountId) ?? new Guid[0]);
            var requesterHubId = string.Empty;
            HubPresenceRegistry.Participant requesterParticipant;
            if (participantByAccountId.TryGetValue(requesterAccountId, out requesterParticipant)
                && requesterParticipant != null
                && !IsNullOrEmpty(requesterParticipant.HubId))
            {
                requesterHubId = requesterParticipant.HubId;
            }

            var seen = new HashSet<Guid>();
            var results = new List<ChatCommandPlayerSummary>(onlineAccountIds.Length);
            for (var i = 0; i < onlineAccountIds.Length; i++)
            {
                var accountId = onlineAccountIds[i];
                if (accountId == Guid.Empty || !seen.Add(accountId))
                {
                    continue;
                }

                HubPresenceRegistry.Participant participant;
                participantByAccountId.TryGetValue(accountId, out participant);

                CareerSlot slot;
                slot = null;
                if (_userStore != null)
                {
                    try
                    {
                        var identityHash = accountId.ToString("D");
                        var careerIndex = participant != null ? participant.CareerIndex : _userStore.GetLastCareerIndex(identityHash);
                        slot = _userStore.GetOrCreateCareer(identityHash, careerIndex, false);
                    }
                    catch
                    {
                        slot = null;
                    }
                }

                var hubId = participant != null && !IsNullOrEmpty(participant.HubId)
                    ? participant.HubId
                    : (slot != null ? (slot.HubId ?? string.Empty) : string.Empty);

                var isPartyMember = requesterParty.Contains(accountId);
                var isInSameHub = !IsNullOrEmpty(requesterHubId)
                    && !IsNullOrEmpty(hubId)
                    && string.Equals(requesterHubId, hubId, StringComparison.OrdinalIgnoreCase);
                var bucket = isPartyMember ? 0 : (isInSameHub ? 1 : 2);

                results.Add(new ChatCommandPlayerSummary
                {
                    AccountId = accountId,
                    CharacterName = ResolveChatCommandCharacterName(accountId, participant, slot),
                    Bucket = bucket,
                    IsRequester = accountId == requesterAccountId,
                });
            }

            return results
                .OrderBy(summary => summary.Bucket)
                .ThenBy(summary => summary.IsRequester ? 0 : 1)
                .ThenBy(summary => summary.CharacterName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(summary => summary.AccountId)
                .ToArray();
        }

        private static int CompareParticipantRichness(HubPresenceRegistry.Participant left, HubPresenceRegistry.Participant right)
        {
            if (left == null && right == null)
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            return ComputeParticipantRichness(left).CompareTo(ComputeParticipantRichness(right));
        }

        private static int ComputeParticipantRichness(HubPresenceRegistry.Participant participant)
        {
            if (participant == null)
            {
                return -1;
            }

            var score = 0;
            if (!IsNullOrEmpty(participant.CharacterName)) score += 4;
            if (!IsNullOrEmpty(participant.HubId)) score += 2;
            if (!IsNullOrEmpty(participant.CharacterId)) score += 1;
            return score;
        }

        private static string FormatChatCommandTarget(ChatCommandTarget target)
        {
            if (target == null)
            {
                return "UnknownCharacter";
            }

            var name = !IsNullOrEmpty(target.CharacterName) ? target.CharacterName : "UnknownCharacter";
            var account = target.AccountId != Guid.Empty ? target.AccountId.ToString("D") : string.Empty;
            return account.Length > 0 ? (name + " (" + account + ")") : name;
        }

        private static string[] BuildPagedFeedbackMessages(string title, IList<string> lines)
        {
            if (lines == null || lines.Count == 0)
            {
                return IsNullOrEmpty(title) ? new string[0] : new[] { title };
            }

            var pages = new List<List<string>>();
            var currentPage = new List<string>();
            var currentChars = 0;
            var baseChars = IsNullOrEmpty(title) ? 0 : title.Length + 8;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i] ?? string.Empty;
                var nextChars = currentChars + line.Length + 1;
                if (currentPage.Count > 0
                    && (currentPage.Count >= SlashCommandFeedbackMaxLinesPerPage
                        || baseChars + nextChars > SlashCommandFeedbackMaxCharsPerPage))
                {
                    pages.Add(currentPage);
                    currentPage = new List<string>();
                    currentChars = 0;
                }

                currentPage.Add(line);
                currentChars += line.Length + 1;
            }

            if (currentPage.Count > 0)
            {
                pages.Add(currentPage);
            }

            var messages = new string[pages.Count];
            for (var i = 0; i < pages.Count; i++)
            {
                var header = title ?? string.Empty;
                if (pages.Count > 1)
                {
                    header = header + " (" + (i + 1).ToString() + "/" + pages.Count.ToString() + ")";
                }

                messages[i] = header + "\n" + string.Join("\n", pages[i].ToArray());
            }

            return messages;
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
            public string ConnectionHash;
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

        private sealed class ChatCommandTarget
        {
            public Guid AccountId;
            public string IdentityHash;
            public int ActiveCareerIndex;
            public CareerSlot ActiveCareerSlot;
            public string CharacterId;
            public string CharacterName;
            public string HubId;
        }

        private sealed class ChatCommandPlayerSummary
        {
            public Guid AccountId;
            public string CharacterName;
            public int Bucket;
            public bool IsRequester;
        }

        private enum ChatCommandTargetMode
        {
            Self = 0,
            OtherByAccountId = 1,
        }

        private sealed class ChatCommandResult
        {
            public bool Success;
            public string FeedbackMessage;
            public string[] FeedbackMessages;

            public static ChatCommandResult Ok(string message)
            {
                return OkMany(message);
            }

            public static ChatCommandResult OkMany(params string[] messages)
            {
                var normalized = NormalizeFeedbackMessages(messages);
                return new ChatCommandResult
                {
                    Success = true,
                    FeedbackMessage = normalized.Length > 0 ? normalized[0] : string.Empty,
                    FeedbackMessages = normalized,
                };
            }

            public static ChatCommandResult Fail(string message)
            {
                return FailMany(message);
            }

            public static ChatCommandResult FailMany(params string[] messages)
            {
                var normalized = NormalizeFeedbackMessages(messages);
                return new ChatCommandResult
                {
                    Success = false,
                    FeedbackMessage = normalized.Length > 0 ? normalized[0] : string.Empty,
                    FeedbackMessages = normalized,
                };
            }

            private static string[] NormalizeFeedbackMessages(string[] messages)
            {
                if (messages == null || messages.Length == 0)
                {
                    return new string[0];
                }

                var normalized = new List<string>(messages.Length);
                for (var i = 0; i < messages.Length; i++)
                {
                    var message = messages[i];
                    if (!IsNullOrEmpty(message))
                    {
                        normalized.Add(message);
                    }
                }

                return normalized.ToArray();
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
                    var lines = new List<string>
                    {
                        "/help",
                        "/bug {message}",
                        "/announce {message}",
                        "/activemissions",
                        "/onlineplayers",
                        "/listplayers",
                        "/setavailablemission {MissionId} [Available]",
                        "/getkarma",
                        "/getnuyen",
                        "/setkarma {X}",
                        "/setnuyen {X}",
                        "/additem {ItemCode} [Variant]",
                        "/othergetkarma {AccountId}",
                        "/othergetnuyen {AccountId}",
                        "/othersetkarma {AccountId} {X}",
                        "/othersetnuyen {AccountId} {X}",
                        "/otheradditem {AccountId} {ItemCode} [Variant]",
                    };
                    return ChatCommandResult.OkMany(BuildPagedFeedbackMessages("Admin commands:", lines));
                }

                return ChatCommandResult.Ok("Commands: /help, /bug {message}");
            }
        }

        private sealed class BugReportChatCommand : IChatCommand
        {
            public string Name { get { return "bug"; } }
            public bool RequiresAdmin { get { return false; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                var message = args != null && args.Length > 0
                    ? string.Join(" ", args).Trim()
                    : string.Empty;
                if (IsNullOrEmpty(message))
                {
                    return ChatCommandResult.Fail("Usage: /bug {message}");
                }

                ChatCommandTarget target;
                string targetError;
                var resolvedTarget = owner.TryResolveSenderCommandTarget(context, out target, out targetError) && target != null;

                var characterName = resolvedTarget && !IsNullOrEmpty(target.CharacterName)
                    ? target.CharacterName
                    : owner.ResolveChatCommandCharacterName(context.SenderAccountId, null, context.ActiveCareerSlot);
                var playerId = context.SenderAccountId != Guid.Empty
                    ? context.SenderAccountId.ToString("D")
                    : (resolvedTarget && target.AccountId != Guid.Empty ? target.AccountId.ToString("D") : string.Empty);

                var payload = new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "player-bug-report",
                    action = "reported",
                    playerId = playerId,
                    accountId = playerId,
                    characterName = !IsNullOrEmpty(characterName) ? characterName : "UnknownCharacter",
                    channel = context.ChannelName ?? string.Empty,
                    message = message,
                };

                if (owner._logger != null)
                {
                    owner._logger.Log(payload);
                    owner._logger.LogLow(payload);
                    owner._logger.LogPlayerBug(payload);
                }

                return ChatCommandResult.Ok("Thanks. Your bug report has been logged.");
            }
        }

        private sealed class AnnounceChatCommand : IChatCommand
        {
            public string Name { get { return "announce"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (args == null || args.Length == 0)
                {
                    return ChatCommandResult.Fail("Usage: /announce {message}");
                }

                var message = string.Join(" ", args).Trim();
                if (IsNullOrEmpty(message))
                {
                    return ChatCommandResult.Fail("Usage: /announce {message}");
                }

                if (owner._chatAndFriends == null)
                {
                    return ChatCommandResult.Fail("Chat service is unavailable.");
                }

                var formatted = "[ANNOUNCEMENT] " + message;
                var channelCount = owner._chatAndFriends.BroadcastAnnouncement(context.SenderAccountId, formatted);

                owner.LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-command",
                    action = "announce-broadcast",
                    senderAccountId = context.SenderAccountId,
                    channels = channelCount,
                    text = message,
                });

                return ChatCommandResult.Ok("Announcement sent to " + channelCount.ToString() + " channel(s).");
            }
        }

        private sealed class ActiveMissionsChatCommand : IChatCommand
        {
            public string Name { get { return "activemissions"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                var count = MissionRuntimeRegistry.GetActiveMissionCount();
                return ChatCommandResult.Ok("Active missions in progress: " + count.ToString());
            }
        }

        private sealed class OnlinePlayersChatCommand : IChatCommand
        {
            public string Name { get { return "onlineplayers"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || owner._chatAndFriends == null)
                {
                    return ChatCommandResult.Fail("Chat service is unavailable.");
                }

                var count = owner._chatAndFriends.GetOnlineAccountCount();
                return ChatCommandResult.Ok("Players currently logged in: " + count.ToString());
            }
        }

        private sealed class ListPlayersChatCommand : IChatCommand
        {
            public string Name { get { return "listplayers"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (args != null && args.Length > 0)
                {
                    return ChatCommandResult.Fail("Usage: /listplayers");
                }

                var players = owner.BuildConnectedPlayerSummaries(context.SenderAccountId);
                if (players.Length == 0)
                {
                    return ChatCommandResult.Ok("No connected players found.");
                }

                var lines = new List<string>(players.Length);
                for (var i = 0; i < players.Length; i++)
                {
                    var bucket = players[i].Bucket == 0 ? "party" : (players[i].Bucket == 1 ? "hub" : "online");
                    var selfSuffix = players[i].IsRequester ? " [you]" : string.Empty;
                    lines.Add("[" + bucket + "] " + players[i].CharacterName + " | " + players[i].AccountId.ToString("D") + selfSuffix);
                }

                return ChatCommandResult.OkMany(BuildPagedFeedbackMessages("Connected players: " + players.Length.ToString(), lines));
            }
        }

        private sealed class SetAvailableMissionCommand : IChatCommand
        {
            public string Name { get { return "setavailablemission"; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (args == null || args.Length < 1 || args.Length > 2)
                {
                    return ChatCommandResult.Fail("Usage: /setavailablemission {MissionId} [Available]");
                }

                var missionId = args[0] != null ? args[0].Trim() : string.Empty;
                if (IsNullOrEmpty(missionId))
                {
                    return ChatCommandResult.Fail("Mission id is required.");
                }

                var desiredState = StoryMissionstate.ReadyToPlay;
                if (args.Length == 2)
                {
                    var stateText = args[1] != null ? args[1].Trim() : string.Empty;
                    if (IsNullOrEmpty(stateText))
                    {
                        return ChatCommandResult.Fail("Usage: /setavailablemission {MissionId} [Available]");
                    }

                    if (string.Equals(stateText, "Available", StringComparison.OrdinalIgnoreCase))
                    {
                        desiredState = StoryMissionstate.Available;
                    }
                    else if (string.Equals(stateText, "ReadyToPlay", StringComparison.OrdinalIgnoreCase))
                    {
                        desiredState = StoryMissionstate.ReadyToPlay;
                    }
                    else
                    {
                        return ChatCommandResult.Fail("Optional state must be 'Available' (or omit to default to ReadyToPlay).");
                    }
                }

                string resultMessage;
                if (!owner.TrySetAvailableMissionForCareer(context, missionId, desiredState, out resultMessage))
                {
                    return ChatCommandResult.Fail(resultMessage);
                }

                owner.LogAdminEvent(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "chat-command",
                    action = "set-available-mission",
                    senderAccountId = context.SenderAccountId,
                    senderIdentity = context.SenderIdentityHash ?? string.Empty,
                    careerIndex = context.ActiveCareerIndex,
                    mission = missionId,
                    state = desiredState.ToString(),
                });

                return ChatCommandResult.Ok(resultMessage);
            }
        }

        private sealed class GetBalanceCommand : IChatCommand
        {
            private readonly string _name;
            private readonly bool _isKarma;
            private readonly ChatCommandTargetMode _targetMode;

            public GetBalanceCommand(string name, bool isKarma, ChatCommandTargetMode targetMode)
            {
                _name = name;
                _isKarma = isKarma;
                _targetMode = targetMode;
            }

            public string Name { get { return _name; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (args != null && args.Length != 0)
                    {
                        return ChatCommandResult.Fail("Usage: /" + _name);
                    }
                }
                else if (args == null || args.Length != 1)
                {
                    return ChatCommandResult.Fail("Usage: /" + _name + " {AccountId}");
                }

                ChatCommandTarget target;
                string error;
                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (!owner.TryResolveSenderCommandTarget(context, out target, out error))
                    {
                        return ChatCommandResult.Fail(error);
                    }
                }
                else if (!owner.TryResolveConnectedCommandTarget(args[0], out target, out error))
                {
                    return ChatCommandResult.Fail(error);
                }

                int value;
                string message;
                var success = owner.TryGetBalanceForTarget(target, _isKarma, out value, out message);
                if (_targetMode == ChatCommandTargetMode.OtherByAccountId)
                {
                    owner.LogAdminEvent(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-command",
                        action = "target-balance-read",
                        command = _name,
                        senderAccountId = context.SenderAccountId,
                        senderIdentity = context.SenderIdentityHash ?? string.Empty,
                        senderCareerIndex = context.ActiveCareerIndex,
                        targetAccountId = target != null && target.AccountId != Guid.Empty ? target.AccountId.ToString("D") : string.Empty,
                        targetIdentityHash = target != null ? (target.IdentityHash ?? string.Empty) : string.Empty,
                        targetCareerIndex = target != null ? target.ActiveCareerIndex : 0,
                        targetCharacterId = target != null ? (target.CharacterId ?? string.Empty) : string.Empty,
                        targetCharacterName = target != null ? (target.CharacterName ?? string.Empty) : string.Empty,
                        targetHubId = target != null ? (target.HubId ?? string.Empty) : string.Empty,
                        balanceType = _isKarma ? "karma" : "nuyen",
                        value = value,
                        success = success,
                    });
                }

                return success ? ChatCommandResult.Ok(message) : ChatCommandResult.Fail(message);
            }
        }

        private sealed class SetBalanceCommand : IChatCommand
        {
            private readonly string _name;
            private readonly bool _isKarma;
            private readonly ChatCommandTargetMode _targetMode;

            public SetBalanceCommand(string name, bool isKarma, ChatCommandTargetMode targetMode)
            {
                _name = name;
                _isKarma = isKarma;
                _targetMode = targetMode;
            }

            public string Name { get { return _name; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (args == null || args.Length != 1)
                    {
                        return ChatCommandResult.Fail("Usage: /" + _name + " {X}");
                    }
                }
                else if (args == null || args.Length != 2)
                {
                    return ChatCommandResult.Fail("Usage: /" + _name + " {AccountId} {X}");
                }

                var valueArgIndex = _targetMode == ChatCommandTargetMode.Self ? 0 : 1;
                int value;
                if (!int.TryParse(args[valueArgIndex], out value) || value < 0)
                {
                    return ChatCommandResult.Fail("Value must be a non-negative integer.");
                }

                ChatCommandTarget target;
                string error;
                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (!owner.TryResolveSenderCommandTarget(context, out target, out error))
                    {
                        return ChatCommandResult.Fail(error);
                    }
                }
                else if (!owner.TryResolveConnectedCommandTarget(args[0], out target, out error))
                {
                    return ChatCommandResult.Fail(error);
                }

                string message;
                var success = owner.TrySetBalanceForTarget(target, value, _isKarma, out message);
                if (_targetMode == ChatCommandTargetMode.OtherByAccountId)
                {
                    owner.LogAdminEvent(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-command",
                        action = "target-balance-write",
                        command = _name,
                        senderAccountId = context.SenderAccountId,
                        senderIdentity = context.SenderIdentityHash ?? string.Empty,
                        senderCareerIndex = context.ActiveCareerIndex,
                        targetAccountId = target != null && target.AccountId != Guid.Empty ? target.AccountId.ToString("D") : string.Empty,
                        targetIdentityHash = target != null ? (target.IdentityHash ?? string.Empty) : string.Empty,
                        targetCareerIndex = target != null ? target.ActiveCareerIndex : 0,
                        targetCharacterId = target != null ? (target.CharacterId ?? string.Empty) : string.Empty,
                        targetCharacterName = target != null ? (target.CharacterName ?? string.Empty) : string.Empty,
                        targetHubId = target != null ? (target.HubId ?? string.Empty) : string.Empty,
                        balanceType = _isKarma ? "karma" : "nuyen",
                        value = value,
                        success = success,
                    });
                }

                return success ? ChatCommandResult.Ok(message) : ChatCommandResult.Fail(message);
            }
        }

        private sealed class AddItemCommand : IChatCommand
        {
            private readonly string _name;
            private readonly ChatCommandTargetMode _targetMode;

            public AddItemCommand(string name, ChatCommandTargetMode targetMode)
            {
                _name = name;
                _targetMode = targetMode;
            }

            public string Name { get { return _name; } }
            public bool RequiresAdmin { get { return true; } }

            public ChatCommandResult Execute(PhotonProxyTcpStub owner, ChatCommandContext context, string[] args)
            {
                if (owner == null || context == null)
                {
                    return ChatCommandResult.Fail("Invalid command context.");
                }

                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (args == null || args.Length < 1 || args.Length > 2)
                    {
                        return ChatCommandResult.Fail("Usage: /" + _name + " {ItemCode} [Variant]");
                    }
                }
                else if (args == null || args.Length < 2 || args.Length > 3)
                {
                    return ChatCommandResult.Fail("Usage: /" + _name + " {AccountId} {ItemCode} [Variant]");
                }

                ChatCommandTarget target;
                string error;
                var itemCodeArgIndex = 0;
                var variantArgIndex = 1;
                if (_targetMode == ChatCommandTargetMode.Self)
                {
                    if (!owner.TryResolveSenderCommandTarget(context, out target, out error))
                    {
                        return ChatCommandResult.Fail(error);
                    }
                }
                else
                {
                    if (!owner.TryResolveConnectedCommandTarget(args[0], out target, out error))
                    {
                        return ChatCommandResult.Fail(error);
                    }

                    itemCodeArgIndex = 1;
                    variantArgIndex = 2;
                }

                var itemCode = args[itemCodeArgIndex] != null ? args[itemCodeArgIndex].Trim() : string.Empty;
                if (IsNullOrEmpty(itemCode))
                {
                    return ChatCommandResult.Fail("Item code is required.");
                }

                int? variantValue = null;
                if (args.Length > variantArgIndex)
                {
                    int parsedVariant;
                    if (!int.TryParse(args[variantArgIndex], out parsedVariant))
                    {
                        return ChatCommandResult.Fail("Variant must be an integer between 2 and 405.");
                    }

                    variantValue = parsedVariant;
                }

                int appliedVariant;
                int quality;
                string message;
                var success = owner.TryAddItemForTarget(target, itemCode, variantValue, out appliedVariant, out quality, out message);
                if (_targetMode == ChatCommandTargetMode.OtherByAccountId)
                {
                    owner.LogAdminEvent(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "chat-command",
                        action = "target-item-write",
                        command = _name,
                        senderAccountId = context.SenderAccountId,
                        senderIdentity = context.SenderIdentityHash ?? string.Empty,
                        senderCareerIndex = context.ActiveCareerIndex,
                        targetAccountId = target != null && target.AccountId != Guid.Empty ? target.AccountId.ToString("D") : string.Empty,
                        targetIdentityHash = target != null ? (target.IdentityHash ?? string.Empty) : string.Empty,
                        targetCareerIndex = target != null ? target.ActiveCareerIndex : 0,
                        targetCharacterId = target != null ? (target.CharacterId ?? string.Empty) : string.Empty,
                        targetCharacterName = target != null ? (target.CharacterName ?? string.Empty) : string.Empty,
                        targetHubId = target != null ? (target.HubId ?? string.Empty) : string.Empty,
                        itemCode = itemCode,
                        variant = appliedVariant,
                        quality = quality,
                        success = success,
                    });
                }

                return success ? ChatCommandResult.Ok(message) : ChatCommandResult.Fail(message);
            }
        }

        private sealed class ItemValidationData
        {
            public readonly HashSet<string> ValidItemCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ItemCategoryByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<int, HashSet<int>> VariantCompatibleCategories = new Dictionary<int, HashSet<int>>();
        }

        private sealed class MainCampaignStoryline
        {
            public readonly List<MainCampaignChapter> Chapters = new List<MainCampaignChapter>();
            public readonly List<string> OrderedMissionIds = new List<string>();
            public readonly Dictionary<string, int> OrderedIndexByMissionId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> ChapterIndexByMissionId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MainCampaignChapter
        {
            public int Index;
            public string Hub;
            public readonly List<string> RequiredMissionIds = new List<string>();
        }

        private sealed class MainCampaignMissionPlan
        {
            public string TargetMissionId;
            public string TargetMissionState;
            public int TargetChapterIndex;
            public string TargetHub;
            public int CompletedBeforeCount;
            public int ClearedAfterCount;
            public Dictionary<string, string> MissionStates;
        }
    }
}
