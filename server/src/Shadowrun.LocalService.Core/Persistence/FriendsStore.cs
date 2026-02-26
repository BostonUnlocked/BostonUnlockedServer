using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class FriendsStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly RequestLogger _logger;
        private readonly object _lock = new object();
        private readonly string _friendsPath;

        public FriendsStore(LocalServiceOptions options, RequestLogger logger)
        {
            _logger = logger;

            var dataDir = options != null ? options.DataDir : null;
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
            }

            try
            {
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }
            }
            catch
            {
            }

            _friendsPath = Path.Combine(dataDir, "friends.json");
        }

        public List<Guid> GetFriends(Guid accountId)
        {
            if (accountId == Guid.Empty)
            {
                return new List<Guid>();
            }

            lock (_lock)
            {
                var root = LoadNoThrow();
                var friends = GetOrCreateDict(root, "Friends");
                var key = NormalizeGuidish(accountId.ToString());
                object raw;
                if (!friends.TryGetValue(key, out raw) || raw == null)
                {
                    return new List<Guid>();
                }

                var list = raw as object[];
                if (list == null)
                {
                    var stringList = raw as List<object>;
                    if (stringList != null)
                    {
                        list = stringList.ToArray();
                    }
                }

                var result = new List<Guid>();
                if (list == null)
                {
                    return result;
                }

                for (var i = 0; i < list.Length; i++)
                {
                    var s = list[i] != null ? list[i].ToString() : null;
                    Guid g;
                    if (TryParseGuidish(s, out g) && g != Guid.Empty)
                    {
                        result.Add(g);
                    }
                }

                return result;
            }
        }

        public void AddFriendship(Guid a, Guid b)
        {
            if (a == Guid.Empty || b == Guid.Empty || a == b)
            {
                return;
            }

            lock (_lock)
            {
                var root = LoadNoThrow();
                var friends = GetOrCreateDict(root, "Friends");
                AddOneWay(friends, a, b);
                AddOneWay(friends, b, a);
                SaveNoThrow(root);
            }
        }

        public void RemoveFriendship(Guid a, Guid b)
        {
            if (a == Guid.Empty || b == Guid.Empty)
            {
                return;
            }

            lock (_lock)
            {
                var root = LoadNoThrow();
                var friends = GetOrCreateDict(root, "Friends");
                RemoveOneWay(friends, a, b);
                RemoveOneWay(friends, b, a);
                SaveNoThrow(root);
            }
        }

        private static void AddOneWay(Dictionary<string, object> friends, Guid from, Guid to)
        {
            var fromKey = NormalizeGuidish(from.ToString());
            var toKey = NormalizeGuidish(to.ToString());

            object raw;
            if (!friends.TryGetValue(fromKey, out raw) || raw == null)
            {
                friends[fromKey] = new List<string> { toKey };
                return;
            }

            var list = raw as List<string>;
            if (list == null)
            {
                var objList = raw as List<object>;
                if (objList != null)
                {
                    list = new List<string>();
                    for (var i = 0; i < objList.Count; i++)
                    {
                        if (objList[i] != null)
                        {
                            list.Add(objList[i].ToString());
                        }
                    }
                }
            }

            if (list == null)
            {
                list = new List<string>();
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(NormalizeGuidish(list[i]), toKey, StringComparison.OrdinalIgnoreCase))
                {
                    friends[fromKey] = list;
                    return;
                }
            }

            list.Add(toKey);
            friends[fromKey] = list;
        }

        private static void RemoveOneWay(Dictionary<string, object> friends, Guid from, Guid to)
        {
            var fromKey = NormalizeGuidish(from.ToString());
            var toKey = NormalizeGuidish(to.ToString());

            object raw;
            if (!friends.TryGetValue(fromKey, out raw) || raw == null)
            {
                return;
            }

            var list = raw as List<object>;
            if (list != null)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var s = list[i] != null ? list[i].ToString() : null;
                    if (string.Equals(NormalizeGuidish(s), toKey, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                    }
                }
                friends[fromKey] = list;
                return;
            }

            var strList = raw as List<string>;
            if (strList != null)
            {
                for (var i = strList.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(NormalizeGuidish(strList[i]), toKey, StringComparison.OrdinalIgnoreCase))
                    {
                        strList.RemoveAt(i);
                    }
                }
                friends[fromKey] = strList;
            }
        }

        private Dictionary<string, object> LoadNoThrow()
        {
            try
            {
                if (!File.Exists(_friendsPath))
                {
                    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                var json = File.ReadAllText(_friendsPath);
                if (string.IsNullOrEmpty(json))
                {
                    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                var root = Json.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null)
                {
                    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                return root;
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "friends-store", note = "load-failed", message = ex.Message });
                    }
                }
                catch
                {
                }

                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveNoThrow(Dictionary<string, object> root)
        {
            try
            {
                if (root == null)
                {
                    root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                root["LastUpdatedUtc"] = DateTime.UtcNow.ToString("o");
                var json = Json.Serialize(root);
                File.WriteAllText(_friendsPath, json);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new { ts = RequestLogger.UtcNowIso(), type = "friends-store", note = "save-failed", message = ex.Message });
                    }
                }
                catch
                {
                }
            }
        }

        private static Dictionary<string, object> GetOrCreateDict(Dictionary<string, object> root, string key)
        {
            if (root == null)
            {
                root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            object value;
            if (root.TryGetValue(key, out value))
            {
                var dict = value as Dictionary<string, object>;
                if (dict != null)
                {
                    return dict;
                }
            }

            var created = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            root[key] = created;
            return created;
        }

        private static bool TryParseGuidish(string value, out Guid guid)
        {
            guid = Guid.Empty;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            try
            {
                guid = new Guid(value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeGuidish(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Guid.Empty.ToString();
            }

            try
            {
                return new Guid(value).ToString();
            }
            catch
            {
                return value.Trim();
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var ser = new JavaScriptSerializer();
            ser.RecursionLimit = 64;
            ser.MaxJsonLength = int.MaxValue;
            return ser;
        }
    }
}
