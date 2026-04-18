using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Protocols
{
    internal static class CheatAuthorizationPolicy
    {
        private static readonly int[] LiveModeProhibitedSkillIds = new[]
        {
            90007,
            90009,
            90038,
            90040,
            90047,
            90048,
            90051,
        };

        internal static bool IsAccountAuthorized(Guid accountId, HashSet<Guid> adminAccountIds, bool adminOpenMode)
        {
            if (adminOpenMode)
            {
                return accountId != Guid.Empty;
            }

            return accountId != Guid.Empty
                && adminAccountIds != null
                && adminAccountIds.Contains(accountId);
        }

            internal static bool ResolveAdminOpenMode(HashSet<Guid> adminAccountIds)
            {
                return adminAccountIds == null || adminAccountIds.Count == 0;
            }

        internal static bool IsLiveModeCheatSkill(int skillId)
        {
            for (var i = 0; i < LiveModeProhibitedSkillIds.Length; i++)
            {
                if (LiveModeProhibitedSkillIds[i] == skillId)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool LooksLikeBlockedCheatMessage(string rawMessage)
        {
            if (string.IsNullOrEmpty(rawMessage))
            {
                return false;
            }

            return rawMessage.IndexOf("ChangeCashMessage", StringComparison.Ordinal) >= 0
                || rawMessage.IndexOf("ItemChangeMessage", StringComparison.Ordinal) >= 0
                || rawMessage.IndexOf("SetStoryStateMessage", StringComparison.Ordinal) >= 0
                || rawMessage.IndexOf("ResetInteractedStoryNPCs", StringComparison.Ordinal) >= 0;
        }

        internal static HashSet<Guid> LoadChatAdminAccountIds(LocalServiceOptions options, Action<object> logAdminEvent)
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
                    TryLog(logAdminEvent, new
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
                    TryLog(logAdminEvent, new
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
                        if (adminsArray == null)
                        {
                            var adminsList = adminsRaw as ArrayList;
                            if (adminsList != null)
                            {
                                adminsArray = new object[adminsList.Count];
                                adminsList.CopyTo(adminsArray, 0);
                            }
                        }
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

                TryLog(logAdminEvent, new
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
                TryLog(logAdminEvent, new
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

        private static void TryLog(Action<object> logAdminEvent, object payload)
        {
            if (logAdminEvent == null)
            {
                return;
            }

            try
            {
                logAdminEvent(payload);
            }
            catch
            {
            }
        }
    }
}