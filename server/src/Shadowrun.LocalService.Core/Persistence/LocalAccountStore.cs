using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class LocalAccountStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private const int AccountStoreSchemaVersion = 2;

        private const string AccountStoreSchemaVersionKey = "SchemaVersion";
        private const string AccountStoreAccountsKey = "Accounts";
        private const string AccountStoreActiveIdentityHashKey = "ActiveIdentityHash";
        private const string AccountStoreSteamIdentitiesKey = "SteamIdentities";
        private const string AccountStoreSteamId64Key = "SteamId64";
        private const string AccountStoreLegacyIdentityHashKey = "IdentityHash";
        private const string AccountStoreCredentialIdentitiesKey = "CredentialIdentities";
        private const string AccountStoreHasCustomDisplayNameKey = "HasCustomDisplayName";

        private const string AccountCredentialEmailKey = "CredentialEmail";
        private const string AccountCredentialPasswordHashKey = "CredentialPasswordHash";
        private const string AccountCredentialPasswordSaltKey = "CredentialPasswordSalt";
        private const string AccountCredentialPasswordIterationsKey = "CredentialPasswordIterations";
        private const string AccountCredentialHashAlgorithmKey = "CredentialHashAlgorithm";

        private readonly RequestLogger _logger;
        private readonly object _syncRoot;
        private readonly string _accountPath;
        private readonly SqliteLocalStore _sqliteStore;

        public LocalAccountStore(LocalServiceOptions options, RequestLogger logger, object syncRoot, SqliteLocalStore sqliteStore)
        {
            _logger = logger;
            _syncRoot = syncRoot ?? new object();
            _sqliteStore = sqliteStore;

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

            _accountPath = Path.Combine(dataDir, "account.json");
        }

        public string GetOrCreateIdentityHash()
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.GetOrCreateIdentityHash();
            }

            lock (_syncRoot)
            {
                var account = LoadAccountNoThrow();
                var identity = GetString(account, AccountStoreLegacyIdentityHashKey);
                if (IsGuidish(identity))
                {
                    if (EnsureDisplayNameIsAnonymized(account, identity))
                    {
                        SaveAccountNoThrow(account);
                    }

                    return NormalizeGuidish(identity);
                }

                var created = Guid.NewGuid().ToString();
                account[AccountStoreLegacyIdentityHashKey] = created;
                if (IsNullOrWhiteSpace(GetString(account, "DisplayName")))
                {
                    account["DisplayName"] = BuildAnonymizedDisplayName(created);
                }
                if (account["Careers"] == null)
                {
                    account["Careers"] = BuildDefaultCareers(created);
                }

                SaveAccountNoThrow(account);
                return NormalizeGuidish(created);
            }
        }

        public string GetOrCreateIdentityHashForSteamId(ulong steamId64)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.GetOrCreateIdentityHashForSteamId(steamId64);
            }

            if (steamId64 == 0)
            {
                return GetOrCreateIdentityHash();
            }

            lock (_syncRoot)
            {
                var steamKey = steamId64.ToString(CultureInfo.InvariantCulture);

                var store = LoadAccountStoreNoThrow(true);
                var steamIdentities = GetOrCreateDict(store, AccountStoreSteamIdentitiesKey);

                var mapped = GetString(steamIdentities, steamKey);
                if (IsGuidish(mapped))
                {
                    var normalized = NormalizeGuidish(mapped);
                    store[AccountStoreSteamId64Key] = steamKey;

                    var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                    var existingAccount = GetDict(accounts, normalized);
                    if (existingAccount == null)
                    {
                        var createdAccount = BuildFreshAccountForIdentity(normalized);
                        createdAccount["Careers"] = BuildDefaultCareers(normalized);
                        accounts[normalized] = createdAccount;
                    }
                    else
                    {
                        EnsureDisplayNameIsAnonymized(existingAccount, normalized);
                    }

                    SaveAccountStoreNoThrow(store);
                    return normalized;
                }

                try
                {
                    var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                    var count = accounts is ICollection ? ((ICollection)accounts).Count : 0;
                    var hasAnyMapping = steamIdentities is ICollection && ((ICollection)steamIdentities).Count > 0;
                    if (!hasAnyMapping && count == 1)
                    {
                        foreach (DictionaryEntry entry in accounts)
                        {
                            var key = entry.Key as string;
                            if (!IsGuidish(key))
                            {
                                continue;
                            }

                            var normalized = NormalizeGuidish(key);
                            steamIdentities[steamKey] = normalized;
                            store[AccountStoreSteamId64Key] = steamKey;

                            var acct = GetDict(accounts, normalized);
                            if (acct != null)
                            {
                                EnsureDisplayNameIsAnonymized(acct, normalized);
                            }

                            SaveAccountStoreNoThrow(store);
                            return normalized;
                        }
                    }
                }
                catch
                {
                }

                var created = NormalizeGuidish(Guid.NewGuid().ToString());
                steamIdentities[steamKey] = created;
                store[AccountStoreSteamId64Key] = steamKey;

                var accountsForCreate = GetOrCreateDict(store, AccountStoreAccountsKey);
                var createdAccountForStore = BuildFreshAccountForIdentity(created);
                createdAccountForStore["Careers"] = BuildDefaultCareers(created);
                accountsForCreate[created] = createdAccountForStore;

                SaveAccountStoreNoThrow(store);
                return created;
            }
        }

        public bool TryRegisterCliffhangerCredentials(string email, string password, string tag, out string identityHash, out string message)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.TryRegisterCliffhangerCredentials(email, password, out identityHash, out message);
            }

            identityHash = null;
            message = null;

            var normalizedEmail = NormalizeCredentialEmail(email);
            if (IsNullOrWhiteSpace(normalizedEmail))
            {
                message = "InvalidEmail";
                return false;
            }

            if (IsNullOrWhiteSpace(password))
            {
                message = "InvalidPassword";
                return false;
            }

            lock (_syncRoot)
            {
                var store = LoadAccountStoreNoThrow(true);
                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                var credentialIdentities = GetOrCreateDict(store, AccountStoreCredentialIdentitiesKey);

                var mapped = GetString(credentialIdentities, normalizedEmail);
                if (IsGuidish(mapped))
                {
                    var normalizedMapped = NormalizeGuidish(mapped);
                    var existingAccount = GetDict(accounts, normalizedMapped);
                    if (existingAccount != null)
                    {
                        message = "EmailAlreadyRegistered";
                        return false;
                    }
                }

                foreach (DictionaryEntry entry in accounts)
                {
                    var existingIdentity = entry.Key as string;
                    var existingAccount = entry.Value as IDictionary;
                    if (!IsGuidish(existingIdentity) || existingAccount == null)
                    {
                        continue;
                    }

                    var existingEmail = NormalizeCredentialEmail(GetString(existingAccount, AccountCredentialEmailKey));
                    if (IsNullOrWhiteSpace(existingEmail) || !string.Equals(existingEmail, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    credentialIdentities[normalizedEmail] = NormalizeGuidish(existingIdentity);
                    SaveAccountStoreNoThrow(store);
                    message = "EmailAlreadyRegistered";
                    return false;
                }

                var createdIdentity = NormalizeGuidish(Guid.NewGuid().ToString());
                var createdAccount = BuildFreshAccountForIdentity(createdIdentity);
                createdAccount["Careers"] = BuildDefaultCareers(createdIdentity);

                string hash;
                string salt;
                var iterations = 100000;
                CreatePasswordDigest(password, iterations, out hash, out salt);

                createdAccount[AccountCredentialEmailKey] = normalizedEmail;
                createdAccount[AccountCredentialPasswordHashKey] = hash;
                createdAccount[AccountCredentialPasswordSaltKey] = salt;
                createdAccount[AccountCredentialPasswordIterationsKey] = iterations;
                createdAccount[AccountCredentialHashAlgorithmKey] = "PBKDF2-SHA1";

                accounts[createdIdentity] = createdAccount;
                credentialIdentities[normalizedEmail] = createdIdentity;

                SaveAccountStoreNoThrow(store);

                identityHash = createdIdentity;
                message = "OK";
                return true;
            }
        }

        public bool TryAuthenticateCliffhangerCredentials(string email, string password, out string identityHash, out bool isVerified, out string message)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.TryAuthenticateCliffhangerCredentials(email, password, out identityHash, out isVerified, out message);
            }

            identityHash = null;
            isVerified = false;
            message = null;

            var normalizedEmail = NormalizeCredentialEmail(email);
            if (IsNullOrWhiteSpace(normalizedEmail) || IsNullOrWhiteSpace(password))
            {
                message = "InvalidCredentials";
                return false;
            }

            lock (_syncRoot)
            {
                var store = LoadAccountStoreNoThrow(true);
                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                var credentialIdentities = GetOrCreateDict(store, AccountStoreCredentialIdentitiesKey);

                string identity = null;
                var mapped = GetString(credentialIdentities, normalizedEmail);
                if (IsGuidish(mapped))
                {
                    identity = NormalizeGuidish(mapped);
                }

                IDictionary account = null;
                if (IsGuidish(identity))
                {
                    account = GetDict(accounts, identity);
                }

                if (account == null)
                {
                    foreach (DictionaryEntry entry in accounts)
                    {
                        var candidateIdentity = entry.Key as string;
                        var candidateAccount = entry.Value as IDictionary;
                        if (!IsGuidish(candidateIdentity) || candidateAccount == null)
                        {
                            continue;
                        }

                        var candidateEmail = NormalizeCredentialEmail(GetString(candidateAccount, AccountCredentialEmailKey));
                        if (IsNullOrWhiteSpace(candidateEmail) || !string.Equals(candidateEmail, normalizedEmail, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        identity = NormalizeGuidish(candidateIdentity);
                        account = candidateAccount;
                        credentialIdentities[normalizedEmail] = identity;
                        break;
                    }
                }

                if (account == null)
                {
                    message = "UnknownEmail";
                    return false;
                }

                var storedHash = GetString(account, AccountCredentialPasswordHashKey);
                var storedSalt = GetString(account, AccountCredentialPasswordSaltKey);
                var iterations = GetInt(account, AccountCredentialPasswordIterationsKey, 100000);
                if (IsNullOrWhiteSpace(storedHash) || IsNullOrWhiteSpace(storedSalt))
                {
                    message = "InvalidCredentials";
                    return false;
                }

                if (!VerifyPasswordDigest(password, iterations, storedHash, storedSalt))
                {
                    message = "IncorrectPassword";
                    return false;
                }

                identityHash = identity;
                isVerified = true;
                message = "OK";
                SaveAccountStoreNoThrow(store);
                return true;
            }
        }

        public string GetDisplayName(string identityHash)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.GetDisplayName(identityHash);
            }

            lock (_syncRoot)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                var changed = EnsureDisplayNameIsAnonymized(account, identityHash);
                var displayName = GetString(account, "DisplayName");
                if (changed)
                {
                    SaveAccountNoThrow(account);
                }

                return displayName;
            }
        }

        public bool TrySetDisplayName(string identityHash, string requestedDisplayName, out string normalizedDisplayName, out string errorMessage)
        {
            normalizedDisplayName = null;
            errorMessage = null;

            if (!IsGuidish(identityHash))
            {
                errorMessage = "Unable to resolve account identity.";
                return false;
            }

            var normalizedIdentity = NormalizeGuidish(identityHash);
            var normalizedRequested = NormalizeRequestedDisplayName(requestedDisplayName);
            if (IsNullOrWhiteSpace(normalizedRequested))
            {
                errorMessage = "Usage: /setaccountname <name>";
                return false;
            }

            if (normalizedRequested.Length > 32)
            {
                errorMessage = "Account display name must be 32 characters or fewer.";
                return false;
            }

            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.TrySetAccountDisplayName(normalizedIdentity, normalizedRequested, out normalizedDisplayName, out errorMessage);
            }

            lock (_syncRoot)
            {
                var account = LoadAccountForIdentityNoThrow(normalizedIdentity, true) ?? LoadAccountNoThrow();
                if (account == null)
                {
                    errorMessage = "Unable to load account data.";
                    return false;
                }

                account["DisplayName"] = normalizedRequested;
                account[AccountStoreHasCustomDisplayNameKey] = true;
                SaveAccountNoThrow(account);
                normalizedDisplayName = normalizedRequested;
                return true;
            }
        }

        public int GetLastCareerIndex(string identityHash)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.GetLastCareerIndex(identityHash);
            }

            lock (_syncRoot)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                return GetInt(account, "LastCareerIndex", 0);
            }
        }

        public void SetLastCareerIndex(string identityHash, int index)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                _sqliteStore.SetLastCareerIndex(identityHash, index);
                return;
            }

            if (index < 0)
            {
                index = 0;
            }

            lock (_syncRoot)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                account["LastCareerIndex"] = index;
                SaveAccountNoThrow(account);
            }
        }

        public bool MigrateDisplayNames(ref int updatedCount)
        {
            if (_sqliteStore != null && _sqliteStore.IsEnabled)
            {
                return _sqliteStore.MigrateAccountDisplayNames(ref updatedCount);
            }

            lock (_syncRoot)
            {
                var changed = false;
                var store = LoadAccountStoreNoThrow(true);
                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);

                foreach (DictionaryEntry entry in accounts)
                {
                    var identityHash = entry.Key as string;
                    var account = entry.Value as IDictionary;
                    if (account == null)
                    {
                        continue;
                    }

                    if (EnsureDisplayNameIsAnonymized(account, identityHash))
                    {
                        updatedCount++;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveAccountStoreNoThrow(store);
                }

                return changed;
            }
        }

        private static string NormalizeCredentialEmail(string email)
        {
            if (IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim();
            var atIndex = trimmed.IndexOf('@');
            if (atIndex <= 0 || atIndex >= trimmed.Length - 1)
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }

        private static string NormalizeDisplayNameTag(string tag)
        {
            return IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        }

        private static string BuildAnonymizedDisplayName(string source)
        {
            var normalizedIdentity = IsGuidish(source) ? NormalizeGuidish(source) : null;
            if (IsNullOrWhiteSpace(normalizedIdentity))
            {
                return "OfflineRunner";
            }

            var dash = normalizedIdentity.IndexOf('-');
            return dash > 0 ? normalizedIdentity.Substring(0, dash) : normalizedIdentity;
        }

        private static bool IsAnonymizedDisplayName(string value)
        {
            if (IsNullOrWhiteSpace(value) || value.Length != 8)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                var isAlphaNum = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
                if (!isAlphaNum && ch != '+' && ch != '/')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool EnsureDisplayNameIsAnonymized(IDictionary account, string identityHash)
        {
            if (account == null)
            {
                return false;
            }

            if (GetBool(account, AccountStoreHasCustomDisplayNameKey, false))
            {
                return false;
            }

            var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetString(account, AccountStoreLegacyIdentityHashKey);
            if (IsGuidish(normalizedIdentity))
            {
                normalizedIdentity = NormalizeGuidish(normalizedIdentity);
            }

            var expected = BuildAnonymizedDisplayName(normalizedIdentity);
            var current = GetString(account, "DisplayName");
            if (!IsNullOrWhiteSpace(current) && !IsAnonymizedDisplayName(current))
            {
                account[AccountStoreHasCustomDisplayNameKey] = true;
                return false;
            }
            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return false;
            }

            account["DisplayName"] = expected;
            account[AccountStoreHasCustomDisplayNameKey] = false;
            return true;
        }

        private static string NormalizeRequestedDisplayName(string value)
        {
            return IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool GetBool(IDictionary dict, string key, bool fallback)
        {
            if (dict == null || IsNullOrWhiteSpace(key) || !dict.Contains(key) || dict[key] == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static void CreatePasswordDigest(string password, int iterations, out string hashBase64, out string saltBase64)
        {
            var salt = new byte[16];
            var rng = new RNGCryptoServiceProvider();
            rng.GetBytes(salt);

            var derive = new Rfc2898DeriveBytes(password, salt, iterations);
            var hash = derive.GetBytes(32);

            hashBase64 = Convert.ToBase64String(hash);
            saltBase64 = Convert.ToBase64String(salt);
        }

        private static bool VerifyPasswordDigest(string password, int iterations, string expectedHashBase64, string saltBase64)
        {
            try
            {
                var salt = Convert.FromBase64String(saltBase64);
                var expected = Convert.FromBase64String(expectedHashBase64);
                var derive = new Rfc2898DeriveBytes(password, salt, iterations);
                var actual = derive.GetBytes(expected.Length);
                return ConstantTimeEquals(expected, actual);
            }
            catch
            {
                return false;
            }
        }

        private static bool ConstantTimeEquals(byte[] expected, byte[] actual)
        {
            if (expected == null || actual == null || expected.Length != actual.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                diff |= expected[i] ^ actual[i];
            }

            return diff == 0;
        }

        private static void PruneAccountStoreRootIdentityKeysNoThrow(IDictionary store)
        {
            if (store == null)
            {
                return;
            }

            try { store.Remove(AccountStoreActiveIdentityHashKey); } catch { }
            try { store.Remove(AccountStoreLegacyIdentityHashKey); } catch { }
        }

        private static IDictionary BuildFreshAccountForIdentity(string identityHash)
        {
            var fresh = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            fresh[AccountStoreLegacyIdentityHashKey] = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : null;
            fresh["DisplayName"] = BuildAnonymizedDisplayName(identityHash);
            fresh[AccountStoreHasCustomDisplayNameKey] = false;
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

            if (loaded.Contains(AccountStoreAccountsKey) && loaded[AccountStoreAccountsKey] is IDictionary)
            {
                if (!loaded.Contains(AccountStoreSchemaVersionKey))
                {
                    loaded[AccountStoreSchemaVersionKey] = AccountStoreSchemaVersion;
                }
                if (!loaded.Contains(AccountStoreAccountsKey) || !(loaded[AccountStoreAccountsKey] is IDictionary))
                {
                    loaded[AccountStoreAccountsKey] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                PruneAccountStoreRootIdentityKeysNoThrow(loaded);
                if (persistMigration)
                {
                    SaveAccountStoreNoThrow(loaded);
                }

                return loaded;
            }

            var legacyAccount = loaded;
            var identity = GetString(legacyAccount, AccountStoreLegacyIdentityHashKey);
            if (!IsGuidish(identity))
            {
                identity = Guid.NewGuid().ToString();
            }
            identity = NormalizeGuidish(identity);

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
            for (var i = 0; i < 6; i++)
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

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
        }
    }
}