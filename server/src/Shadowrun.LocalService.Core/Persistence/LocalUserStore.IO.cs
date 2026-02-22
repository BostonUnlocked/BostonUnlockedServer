using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private const int AccountStoreSchemaVersion = 2;

        private const string AccountStoreSchemaVersionKey = "SchemaVersion";
        private const string AccountStoreAccountsKey = "Accounts";
        private const string AccountStoreActiveIdentityHashKey = "ActiveIdentityHash";
        private const string AccountStoreSteamIdentitiesKey = "SteamIdentities";
        private const string AccountStoreSteamId64Key = "SteamId64";
        private const string AccountStoreLegacyIdentityHashKey = "IdentityHash";

        private static void PruneAccountStoreRootIdentityKeysNoThrow(IDictionary store)
        {
            if (store == null)
            {
                return;
            }

            try { store.Remove(AccountStoreActiveIdentityHashKey); } catch { }
            try { store.Remove(AccountStoreLegacyIdentityHashKey); } catch { }
        }

        private static ArrayList CoerceToArrayList(object value)
        {
            if (value == null)
            {
                return null;
            }

            var list = value as ArrayList;
            if (list != null)
            {
                return list;
            }

            var objArray = value as object[];
            if (objArray != null)
            {
                var created = new ArrayList(objArray.Length);
                for (var i = 0; i < objArray.Length; i++)
                {
                    created.Add(objArray[i]);
                }
                return created;
            }

            var enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                var created = new ArrayList();
                foreach (var item in enumerable)
                {
                    created.Add(item);
                }
                return created;
            }

            return null;
        }

        private static IDictionary BuildFreshAccountForIdentity(string identityHash)
        {
            var fresh = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            fresh["IdentityHash"] = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : null;
            fresh["DisplayName"] = "OfflineRunner";
            fresh["Careers"] = null;
            fresh["LastCareerIndex"] = 0;
            return fresh;
        }

        private IDictionary LoadAccountStoreNoThrow(bool persistMigration)
        {
            IDictionary loaded = null;
            try
            {
                if (File.Exists(_accountPath))
                {
                    var json = File.ReadAllText(_accountPath, Encoding.UTF8);
                    loaded = Json.DeserializeObject(json) as IDictionary;
                }
            }
            catch
            {
                loaded = null;
            }

            if (loaded == null)
            {
                var created = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                created[AccountStoreSchemaVersionKey] = AccountStoreSchemaVersion;
                created[AccountStoreAccountsKey] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                return created;
            }

            // New multi-account store schema?
            if (loaded.Contains(AccountStoreAccountsKey) && loaded[AccountStoreAccountsKey] is IDictionary)
            {
                // Ensure required keys exist.
                if (!loaded.Contains(AccountStoreSchemaVersionKey))
                {
                    loaded[AccountStoreSchemaVersionKey] = AccountStoreSchemaVersion;
                }
                if (!loaded.Contains(AccountStoreAccountsKey) || !(loaded[AccountStoreAccountsKey] is IDictionary))
                {
                    loaded[AccountStoreAccountsKey] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                // Identity selection is per request/session now; don't keep a global "active" pointer.
                PruneAccountStoreRootIdentityKeysNoThrow(loaded);
                if (persistMigration)
                {
                    SaveAccountStoreNoThrow(loaded);
                }

                return loaded;
            }

            // Legacy single-account schema -> migrate into a store.
            var legacyAccount = loaded;
            var identity = GetString(legacyAccount, AccountStoreLegacyIdentityHashKey);
            if (!IsGuidish(identity))
            {
                identity = Guid.NewGuid().ToString();
            }
            identity = NormalizeGuidish(identity);

            // Move Steam mapping fields to the store root.
            var legacySteamIdentities = GetDict(legacyAccount, AccountStoreSteamIdentitiesKey);
            var legacySteamId64 = GetString(legacyAccount, AccountStoreSteamId64Key);
            try { legacyAccount.Remove(AccountStoreSteamIdentitiesKey); } catch { }
            try { legacyAccount.Remove(AccountStoreSteamId64Key); } catch { }

            legacyAccount[AccountStoreLegacyIdentityHashKey] = identity;

            var store = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            store[AccountStoreSchemaVersionKey] = AccountStoreSchemaVersion;

            var accounts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            accounts[identity] = legacyAccount;
            store[AccountStoreAccountsKey] = accounts;

            if (legacySteamIdentities != null)
            {
                store[AccountStoreSteamIdentitiesKey] = legacySteamIdentities;
            }
            if (!IsNullOrWhiteSpace(legacySteamId64))
            {
                store[AccountStoreSteamId64Key] = legacySteamId64;
            }

            if (persistMigration)
            {
                PruneAccountStoreRootIdentityKeysNoThrow(store);
                SaveAccountStoreNoThrow(store);
            }

            return store;
        }

        private void SaveAccountStoreNoThrow(IDictionary store)
        {
            try
            {
                // Do not persist any global "active" identity pointers at the root.
                PruneAccountStoreRootIdentityKeysNoThrow(store);
                var json = Json.Serialize(store);
                File.WriteAllText(_accountPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "persistence",
                            op = "save-account-store-failed",
                            path = _accountPath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private IDictionary LoadAccountForIdentityNoThrow(string identityHash, bool createIfMissing)
        {
            if (!IsGuidish(identityHash))
            {
                identityHash = null;
            }
            identityHash = NormalizeGuidish(identityHash);

            var store = LoadAccountStoreNoThrow(true);
            var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
            var existing = !IsNullOrWhiteSpace(identityHash) ? GetDict(accounts, identityHash) : null;
            if (existing != null)
            {
                return existing;
            }

            if (!createIfMissing)
            {
                return null;
            }

            if (!IsGuidish(identityHash))
            {
                identityHash = NormalizeGuidish(Guid.NewGuid().ToString());
            }

            var created = BuildFreshAccountForIdentity(identityHash);
            created["Careers"] = BuildDefaultCareers(identityHash);
            accounts[identityHash] = created;
            SaveAccountStoreNoThrow(store);
            return created;
        }

        private IDictionary LoadAccountNoThrow()
        {
            try
            {
                var store = LoadAccountStoreNoThrow(true);

                string active = null;

                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                if (!IsGuidish(active))
                {
                    // Best-effort: pick the first existing key.
                    foreach (DictionaryEntry entry in accounts)
                    {
                        var key = entry.Key as string;
                        if (IsGuidish(key))
                        {
                            active = NormalizeGuidish(key);
                            break;
                        }
                    }
                }

                if (IsGuidish(active))
                {
                    return LoadAccountForIdentityNoThrow(active, true) ?? BuildFreshAccountForIdentity(active);
                }
            }
            catch
            {
            }

            return BuildFreshAccountForIdentity(null);
        }

        private void SaveAccountNoThrow(IDictionary account)
        {
            try
            {
                if (account == null)
                {
                    return;
                }

                var identity = GetString(account, AccountStoreLegacyIdentityHashKey);
                if (!IsGuidish(identity))
                {
                    identity = Guid.NewGuid().ToString();
                    account[AccountStoreLegacyIdentityHashKey] = identity;
                }
                identity = NormalizeGuidish(identity);

                var store = LoadAccountStoreNoThrow(true);
                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);

                // Persist only per-account fields into the Accounts map.
                var pruned = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in account)
                {
                    var key = entry.Key as string;
                    if (IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (string.Equals(key, AccountStoreSchemaVersionKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, AccountStoreAccountsKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, AccountStoreActiveIdentityHashKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, AccountStoreSteamIdentitiesKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(key, AccountStoreSteamId64Key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pruned[key] = entry.Value;
                }

                pruned[AccountStoreLegacyIdentityHashKey] = identity;
                accounts[identity] = pruned;

                SaveAccountStoreNoThrow(store);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "persistence",
                            op = "save-account-failed",
                            path = _accountPath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private Dictionary<string, string> LoadSessionsNoThrow()
        {
            try
            {
                if (File.Exists(_sessionsPath))
                {
                    var json = File.ReadAllText(_sessionsPath, Encoding.UTF8);
                    var obj = Json.DeserializeObject(json) as IDictionary;
                    if (obj != null)
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DictionaryEntry entry in obj)
                        {
                            var key = entry.Key as string;
                            var value = entry.Value as string;
                            if (!IsNullOrWhiteSpace(key) && !IsNullOrWhiteSpace(value))
                            {
                                dict[NormalizeGuidish(key)] = NormalizeGuidish(value);
                            }
                        }
                        return dict;
                    }
                }
            }
            catch
            {
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveSessionsNoThrow(Dictionary<string, string> sessions)
        {
            try
            {
                var obj = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in sessions)
                {
                    obj[kvp.Key] = kvp.Value;
                }
                var json = Json.Serialize(obj);
                File.WriteAllText(_sessionsPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "persistence",
                            op = "save-sessions-failed",
                            path = _sessionsPath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private IDictionary LoadPlayerInfoNoThrow()
        {
            try
            {
                if (File.Exists(_playerInfoPath))
                {
                    var json = File.ReadAllText(_playerInfoPath, Encoding.UTF8);
                    var obj = Json.DeserializeObject(json) as IDictionary;
                    if (obj != null)
                    {
                        return obj;
                    }
                }
            }
            catch
            {
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private void SavePlayerInfoNoThrow(IDictionary root)
        {
            try
            {
                var json = Json.Serialize(root);
                File.WriteAllText(_playerInfoPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "persistence",
                            op = "save-playerinfo-failed",
                            path = _playerInfoPath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }

        private static string GetString(IDictionary dict, string key)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return null;
            }
            return dict[key] as string;
        }

        private static int GetInt(IDictionary dict, string key, int fallback)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return fallback;
            }
            try
            {
                return Convert.ToInt32(dict[key]);
            }
            catch
            {
                return fallback;
            }
        }

        private static IDictionary GetDict(IDictionary root, string key)
        {
            if (root == null || IsNullOrWhiteSpace(key) || !root.Contains(key))
            {
                return null;
            }
            return root[key] as IDictionary;
        }

        private static IDictionary GetOrCreateDict(IDictionary root, string key)
        {
            var existing = GetDict(root, key);
            if (existing != null)
            {
                return existing;
            }

            var created = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            root[key] = created;
            return created;
        }

        private static ArrayList BuildDefaultCareers(string identityHash)
        {
            var list = new ArrayList();
            for (var i = 0; i < 3; i++)
            {
                var slot = new CareerSlot();
                slot.Index = i;
                slot.IsOccupied = false;
                slot.CharacterName = string.Empty;
                slot.Portrait = string.Empty;
                slot.HubId = "Act01_HUB_02";
                slot.PendingPersistenceCreation = false;
                slot.CharacterIdentifier = NormalizeGuidish(identityHash) + ":" + i.ToString();
                list.Add(slot.ToDictionary());
            }
            return list;
        }

        private static bool IsGuidish(string value)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }
            try
            {
                new Guid(value.Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeGuidish(string value)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return null;
            }
            try
            {
                var g = new Guid(value.Trim());
                return g.ToString();
            }
            catch
            {
                return value.Trim();
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
