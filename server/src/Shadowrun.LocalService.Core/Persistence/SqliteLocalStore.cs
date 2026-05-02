using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Mono.Data.Sqlite;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed class SqliteLocalStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();
        private static readonly object BootstrapLock = new object();
        private static readonly Dictionary<string, bool> BootstrappedPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private readonly RequestLogger _logger;
        private readonly string _databasePath;
        private readonly string _accountJsonPath;
        private readonly string _friendsJsonPath;
        private readonly string _playerInfoJsonPath;
        private readonly int _busyTimeoutMs;

        public SqliteLocalStore(LocalServiceOptions options, RequestLogger logger)
        {
            _logger = logger;
            IsEnabled = options != null && options.UseSqlite;
            if (!IsEnabled)
            {
                return;
            }

            var dataDir = options != null ? options.DataDir : null;
            if (IsNullOrWhiteSpace(dataDir))
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

            _databasePath = !IsNullOrWhiteSpace(options.SqliteDatabasePath)
                ? options.SqliteDatabasePath
                : Path.Combine(dataDir, "localservice.sqlite");
            _accountJsonPath = Path.Combine(dataDir, "account.json");
            _friendsJsonPath = Path.Combine(dataDir, "friends.json");
            _playerInfoJsonPath = Path.Combine(dataDir, "playerinfo.json");
            _busyTimeoutMs = options != null && options.SqliteBusyTimeoutMs > 0 ? options.SqliteBusyTimeoutMs : 15000;

            EnsureBootstrapped(options != null && options.MigrateJsonToSqlite);
        }

        public bool IsEnabled { get; private set; }

        public string DatabasePath
        {
            get { return _databasePath; }
        }

        public string GetOrCreateIdentityHash()
        {
            if (!IsEnabled)
            {
                return null;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                {
                    using (var command = CreateCommand(connection, null, "SELECT identity_hash FROM accounts ORDER BY identity_hash LIMIT 1;"))
                    {
                        var existing = command.ExecuteScalar() as string;
                        if (IsGuidish(existing))
                        {
                            LogOperation("get-or-create-identity", sw, new { created = false });
                            return NormalizeGuidish(existing);
                        }
                    }

                    var identity = NormalizeGuidish(Guid.NewGuid().ToString());
                    var account = CreateFreshAccountForIdentity(identity);
                    UpsertAccount(connection, null, account, identity, true);
                    LogOperation("get-or-create-identity", sw, new { created = true });
                    return identity;
                }
            }
            catch (Exception ex)
            {
                LogFailure("get-or-create-identity-failed", sw, ex, null);
                return null;
            }
        }

        public string GetOrCreateIdentityHashForSteamId(ulong steamId64)
        {
            if (!IsEnabled)
            {
                return null;
            }

            if (steamId64 == 0)
            {
                return GetOrCreateIdentityHash();
            }

            var sw = Stopwatch.StartNew();
            var steamKey = steamId64.ToString(CultureInfo.InvariantCulture);
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var mapped = ExecuteScalarString(connection, transaction,
                        "SELECT identity_hash FROM steam_identities WHERE steam_id64 = @steamId64;",
                        "@steamId64", steamKey);
                    if (IsGuidish(mapped))
                    {
                        EnsureAccountExists(connection, transaction, mapped);
                        transaction.Commit();
                        LogOperation("account-steam-auth", sw, new { created = false, adopted = false });
                        return NormalizeGuidish(mapped);
                    }

                    var accountCount = ExecuteScalarInt(connection, transaction, "SELECT COUNT(*) FROM accounts;");
                    var steamCount = ExecuteScalarInt(connection, transaction, "SELECT COUNT(*) FROM steam_identities;");
                    if (accountCount == 1 && steamCount == 0)
                    {
                        var existingIdentity = ExecuteScalarString(connection, transaction, "SELECT identity_hash FROM accounts ORDER BY identity_hash LIMIT 1;");
                        if (IsGuidish(existingIdentity))
                        {
                            InsertSteamMapping(connection, transaction, steamKey, existingIdentity);
                            transaction.Commit();
                            LogOperation("account-steam-auth", sw, new { created = false, adopted = true });
                            return NormalizeGuidish(existingIdentity);
                        }
                    }

                    var identity = NormalizeGuidish(Guid.NewGuid().ToString());
                    var account = CreateFreshAccountForIdentity(identity);
                    UpsertAccount(connection, transaction, account, identity, true);
                    InsertSteamMapping(connection, transaction, steamKey, identity);
                    transaction.Commit();
                    LogOperation("account-steam-auth", sw, new { created = true, adopted = false });
                    return identity;
                }
            }
            catch (Exception ex)
            {
                LogFailure("account-steam-auth-failed", sw, ex, new { steamId64 = steamKey });
                return null;
            }
        }

        public bool TryRegisterCliffhangerCredentials(string email, string password, out string identityHash, out string message)
        {
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

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var mapped = ExecuteScalarString(connection, transaction,
                        "SELECT identity_hash FROM credential_identities WHERE email = @email;",
                        "@email", normalizedEmail);
                    if (IsGuidish(mapped))
                    {
                        message = "EmailAlreadyRegistered";
                        transaction.Commit();
                        LogOperation("account-register", sw, new { created = false, reason = message });
                        return false;
                    }

                    string hash;
                    string salt;
                    var iterations = 100000;
                    CreatePasswordDigest(password, iterations, out hash, out salt);

                    identityHash = NormalizeGuidish(Guid.NewGuid().ToString());
                    var account = CreateFreshAccountForIdentity(identityHash);
                    account["CredentialEmail"] = normalizedEmail;
                    account["CredentialPasswordHash"] = hash;
                    account["CredentialPasswordSalt"] = salt;
                    account["CredentialPasswordIterations"] = iterations;
                    account["CredentialHashAlgorithm"] = "PBKDF2-SHA1";

                    UpsertAccount(connection, transaction, account, identityHash, true);
                    ReplaceCredentialMapping(connection, transaction, identityHash, normalizedEmail);
                    transaction.Commit();

                    message = "OK";
                    LogOperation("account-register", sw, new { created = true });
                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "RegisterFailed";
                identityHash = null;
                LogFailure("account-register-failed", sw, ex, new { email = normalizedEmail });
                return false;
            }
        }

        public bool TryAuthenticateCliffhangerCredentials(string email, string password, out string identityHash, out bool isVerified, out string message)
        {
            identityHash = null;
            isVerified = false;
            message = null;

            var normalizedEmail = NormalizeCredentialEmail(email);
            if (IsNullOrWhiteSpace(normalizedEmail) || IsNullOrWhiteSpace(password))
            {
                message = "InvalidCredentials";
                return false;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                {
                    var mapped = ExecuteScalarString(connection, null,
                        "SELECT identity_hash FROM credential_identities WHERE email = @email;",
                        "@email", normalizedEmail);
                    if (!IsGuidish(mapped))
                    {
                        message = "UnknownEmail";
                        LogOperation("account-authenticate", sw, new { authenticated = false, reason = message });
                        return false;
                    }

                    var account = LoadAccountByIdentity(connection, mapped);
                    if (account == null)
                    {
                        message = "UnknownEmail";
                        LogOperation("account-authenticate", sw, new { authenticated = false, reason = message });
                        return false;
                    }

                    var storedHash = GetString(account, "CredentialPasswordHash");
                    var storedSalt = GetString(account, "CredentialPasswordSalt");
                    var iterations = GetInt(account, "CredentialPasswordIterations", 100000);
                    if (IsNullOrWhiteSpace(storedHash) || IsNullOrWhiteSpace(storedSalt))
                    {
                        message = "InvalidCredentials";
                        LogOperation("account-authenticate", sw, new { authenticated = false, reason = message });
                        return false;
                    }

                    if (!VerifyPasswordDigest(password, iterations, storedHash, storedSalt))
                    {
                        message = "IncorrectPassword";
                        LogOperation("account-authenticate", sw, new { authenticated = false, reason = message });
                        return false;
                    }

                    identityHash = NormalizeGuidish(mapped);
                    isVerified = true;
                    message = "OK";
                    LogOperation("account-authenticate", sw, new { authenticated = true });
                    return true;
                }
            }
            catch (Exception ex)
            {
                message = "InvalidCredentials";
                LogFailure("account-authenticate-failed", sw, ex, new { email = normalizedEmail });
                return false;
            }
        }

        public string GetDisplayName(string identityHash)
        {
            if (!IsEnabled)
            {
                return null;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetOrCreateIdentityHash();
                using (var connection = OpenConnection())
                {
                    var account = LoadAccountForIdentityNoThrow(normalizedIdentity, true);
                    var displayName = account != null ? GetString(account, "DisplayName") : null;
                    if (IsNullOrWhiteSpace(displayName))
                    {
                        var expected = BuildAnonymizedDisplayName(normalizedIdentity);
                        if (account != null)
                        {
                            account["DisplayName"] = expected;
                            account["HasCustomDisplayName"] = false;
                            SaveAccountNoThrow(account);
                        }
                        displayName = expected;
                    }

                    LogOperation("account-display-name", sw, null);
                    return displayName;
                }
            }
            catch (Exception ex)
            {
                LogFailure("account-display-name-failed", sw, ex, new { identityHash = identityHash });
                return null;
            }
        }

        public bool TrySetAccountDisplayName(string identityHash, string requestedDisplayName, out string normalizedDisplayName, out string errorMessage)
        {
            normalizedDisplayName = null;
            errorMessage = null;

            if (!IsEnabled)
            {
                errorMessage = "Account store is unavailable.";
                return false;
            }

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

            var account = LoadAccountForIdentityNoThrow(normalizedIdentity, true);
            if (account == null)
            {
                errorMessage = "Unable to load account data.";
                return false;
            }

            account["DisplayName"] = normalizedRequested;
            account["HasCustomDisplayName"] = true;
            SaveAccountNoThrow(account);
            normalizedDisplayName = normalizedRequested;
            return true;
        }

        public int GetLastCareerIndex(string identityHash)
        {
            if (!IsEnabled)
            {
                return 0;
            }

            try
            {
                using (var connection = OpenConnection())
                {
                    return ExecuteScalarInt(connection, null,
                        "SELECT COALESCE(last_career_index, 0) FROM accounts WHERE identity_hash = @identityHash;",
                        "@identityHash", NormalizeGuidish(identityHash));
                }
            }
            catch
            {
                return 0;
            }
        }

        public void SetLastCareerIndex(string identityHash, int index)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (index < 0)
            {
                index = 0;
            }

            var account = LoadAccountForIdentityNoThrow(identityHash, true);
            if (account == null)
            {
                return;
            }

            account["LastCareerIndex"] = index;
            SaveAccountNoThrow(account);
        }

        public bool MigrateAccountDisplayNames(ref int updatedCount)
        {
            if (!IsEnabled)
            {
                return false;
            }

            var changed = false;
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                using (var command = CreateCommand(connection, transaction, "SELECT identity_hash, account_json FROM accounts;"))
                using (var reader = command.ExecuteReader())
                {
                    var pending = new List<IDictionary>();
                    while (reader.Read())
                    {
                        var identityHash = reader.IsDBNull(0) ? null : reader.GetString(0);
                        var accountJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var account = DeserializeDictionary(accountJson);
                        if (account == null)
                        {
                            continue;
                        }

                        if (EnsureDisplayNameIsAnonymized(account, identityHash))
                        {
                            updatedCount++;
                            changed = true;
                            pending.Add(account);
                        }
                    }

                    reader.Close();
                    for (var i = 0; i < pending.Count; i++)
                    {
                        var account = pending[i];
                        var identityHash = GetString(account, "IdentityHash");
                        UpsertAccount(connection, transaction, account, identityHash, true);
                    }

                    transaction.Commit();
                }
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
                            type = "sqlite",
                            op = "account-displayname-migration-failed",
                            databasePath = _databasePath,
                            message = ex.Message,
                        });
                    }
                }
                catch
                {
                }
            }

            return changed;
        }

        public IDictionary LoadAccountStoreNoThrow()
        {
            if (!IsEnabled)
            {
                return null;
            }

            try
            {
                using (var connection = OpenConnection())
                {
                    var store = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    store["SchemaVersion"] = 2;

                    var accounts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    using (var command = CreateCommand(connection, null, "SELECT identity_hash, account_json FROM accounts;"))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var identityHash = reader.IsDBNull(0) ? null : reader.GetString(0);
                            var accountJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                            var account = DeserializeDictionary(accountJson);
                            if (!IsGuidish(identityHash) || account == null)
                            {
                                continue;
                            }

                            accounts[NormalizeGuidish(identityHash)] = account;
                        }
                    }
                    store["Accounts"] = accounts;

                    var steamIdentities = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    using (var command = CreateCommand(connection, null, "SELECT steam_id64, identity_hash FROM steam_identities;"))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                            {
                                continue;
                            }

                            steamIdentities[reader.GetString(0)] = NormalizeGuidish(reader.GetString(1));
                        }
                    }
                    store["SteamIdentities"] = steamIdentities;

                    var credentialIdentities = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    using (var command = CreateCommand(connection, null, "SELECT email, identity_hash FROM credential_identities;"))
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                            {
                                continue;
                            }

                            credentialIdentities[reader.GetString(0)] = NormalizeGuidish(reader.GetString(1));
                        }
                    }
                    store["CredentialIdentities"] = credentialIdentities;
                    return store;
                }
            }
            catch
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void SaveAccountStoreNoThrow(IDictionary store)
        {
            if (!IsEnabled)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    ExecuteNonQuery(connection, transaction, "DELETE FROM steam_identities;");
                    ExecuteNonQuery(connection, transaction, "DELETE FROM credential_identities;");
                    ExecuteNonQuery(connection, transaction, "DELETE FROM accounts;");

                    var accounts = GetDict(store, "Accounts");
                    if (accounts != null)
                    {
                        foreach (DictionaryEntry entry in accounts)
                        {
                            var identityHash = entry.Key as string;
                            var account = entry.Value as IDictionary;
                            if (!IsGuidish(identityHash) || account == null)
                            {
                                continue;
                            }

                            UpsertAccount(connection, transaction, account, identityHash, true);
                        }
                    }

                    var steamIdentities = GetDict(store, "SteamIdentities");
                    if (steamIdentities != null)
                    {
                        foreach (DictionaryEntry entry in steamIdentities)
                        {
                            var steamId64 = entry.Key as string;
                            var identityHash = entry.Value as string;
                            if (IsNullOrWhiteSpace(steamId64) || !IsGuidish(identityHash))
                            {
                                continue;
                            }

                            InsertSteamMapping(connection, transaction, steamId64, identityHash);
                        }
                    }

                    var credentialIdentities = GetDict(store, "CredentialIdentities");
                    if (credentialIdentities != null)
                    {
                        foreach (DictionaryEntry entry in credentialIdentities)
                        {
                            var email = NormalizeCredentialEmail(entry.Key as string);
                            var identityHash = entry.Value as string;
                            if (IsNullOrWhiteSpace(email) || !IsGuidish(identityHash))
                            {
                                continue;
                            }

                            InsertCredentialMapping(connection, transaction, email, identityHash);
                        }
                    }

                    transaction.Commit();
                }

                LogOperation("save-account-store", sw, null);
            }
            catch (Exception ex)
            {
                LogFailure("save-account-store-failed", sw, ex, null, "persistence");
            }
        }

        public IDictionary LoadAccountForIdentityNoThrow(string identityHash, bool createIfMissing)
        {
            if (!IsEnabled)
            {
                return null;
            }

            var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : null;
            try
            {
                using (var connection = OpenConnection())
                {
                    var account = LoadAccountByIdentity(connection, normalizedIdentity);
                    if (account != null)
                    {
                        return account;
                    }

                    if (!createIfMissing)
                    {
                        return null;
                    }

                    if (!IsGuidish(normalizedIdentity))
                    {
                        normalizedIdentity = NormalizeGuidish(Guid.NewGuid().ToString());
                    }

                    account = CreateFreshAccountForIdentity(normalizedIdentity);
                    UpsertAccount(connection, null, account, normalizedIdentity, true);
                    return account;
                }
            }
            catch
            {
                return null;
            }
        }

        public IDictionary LoadAccountNoThrow()
        {
            if (!IsEnabled)
            {
                return null;
            }

            try
            {
                using (var connection = OpenConnection())
                {
                    var identityHash = ExecuteScalarString(connection, null, "SELECT identity_hash FROM accounts ORDER BY identity_hash LIMIT 1;");
                    if (IsGuidish(identityHash))
                    {
                        return LoadAccountByIdentity(connection, identityHash);
                    }
                }
            }
            catch
            {
            }

            return CreateFreshAccountForIdentity(null);
        }

        public void SaveAccountNoThrow(IDictionary account)
        {
            if (!IsEnabled || account == null)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var identityHash = GetString(account, "IdentityHash");
                    if (!IsGuidish(identityHash))
                    {
                        identityHash = NormalizeGuidish(Guid.NewGuid().ToString());
                        account["IdentityHash"] = identityHash;
                    }

                    UpsertAccount(connection, transaction, account, identityHash, true);
                    var email = NormalizeCredentialEmail(GetString(account, "CredentialEmail"));
                    if (!IsNullOrWhiteSpace(email))
                    {
                        ReplaceCredentialMapping(connection, transaction, identityHash, email);
                    }

                    transaction.Commit();
                }

                LogOperation("save-account", sw, new { identityHash = GetString(account, "IdentityHash") });
            }
            catch (Exception ex)
            {
                LogFailure("save-account-failed", sw, ex, new { identityHash = GetString(account, "IdentityHash") }, "persistence");
            }
        }

        public Dictionary<string, string> GetPlayerInfo(string identityHash, string gameName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!IsEnabled || IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName))
            {
                return result;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var command = CreateCommand(connection, null,
                    "SELECT info_key, info_value FROM player_info WHERE identity_hash = @identityHash AND game_name = @gameName;"))
                {
                    AddParameter(command, "@identityHash", NormalizeGuidish(identityHash));
                    AddParameter(command, "@gameName", gameName.Trim());
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0))
                            {
                                continue;
                            }

                            var key = reader.GetString(0);
                            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
                            result[key] = value;
                        }
                    }
                }

                LogOperation("playerinfo-get", sw, new { identityHash = NormalizeGuidish(identityHash), gameName = gameName.Trim(), keyCount = result.Count });
            }
            catch (Exception ex)
            {
                LogFailure("playerinfo-get-failed", sw, ex, new { identityHash = identityHash, gameName = gameName });
            }

            return result;
        }

        public PlayerInfoChanges SetPlayerInfo(string identityHash, string gameName, Dictionary<string, string> updates)
        {
            var changes = new PlayerInfoChanges();
            if (!IsEnabled || IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName) || updates == null)
            {
                return changes;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var normalizedIdentity = NormalizeGuidish(identityHash);
                var trimmedGameName = gameName.Trim();
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    var existing = LoadPlayerInfoMap(connection, transaction, normalizedIdentity, trimmedGameName);
                    foreach (var kvp in updates)
                    {
                        var key = kvp.Key;
                        if (IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        string oldValue;
                        var hadExisting = existing.TryGetValue(key, out oldValue);
                        if (kvp.Value == null)
                        {
                            if (hadExisting)
                            {
                                ExecuteNonQuery(connection, transaction,
                                    "DELETE FROM player_info WHERE identity_hash = @identityHash AND game_name = @gameName AND info_key = @infoKey;",
                                    "@identityHash", normalizedIdentity,
                                    "@gameName", trimmedGameName,
                                    "@infoKey", key);
                                changes.Deleted.Add(key);
                                existing.Remove(key);
                            }
                            continue;
                        }

                        if (!hadExisting)
                        {
                            ExecuteNonQuery(connection, transaction,
                                "INSERT INTO player_info (identity_hash, game_name, info_key, info_value, updated_utc) VALUES (@identityHash, @gameName, @infoKey, @infoValue, @updatedUtc);",
                                "@identityHash", normalizedIdentity,
                                "@gameName", trimmedGameName,
                                "@infoKey", key,
                                "@infoValue", kvp.Value,
                                "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                            changes.Added[key] = kvp.Value;
                            existing[key] = kvp.Value;
                            continue;
                        }

                        if (!string.Equals(oldValue, kvp.Value, StringComparison.Ordinal))
                        {
                            ExecuteNonQuery(connection, transaction,
                                "UPDATE player_info SET info_value = @infoValue, updated_utc = @updatedUtc WHERE identity_hash = @identityHash AND game_name = @gameName AND info_key = @infoKey;",
                                "@infoValue", kvp.Value,
                                "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                                "@identityHash", normalizedIdentity,
                                "@gameName", trimmedGameName,
                                "@infoKey", key);
                            changes.Updated[key] = kvp.Value;
                            existing[key] = kvp.Value;
                        }
                    }

                    transaction.Commit();
                }

                LogOperation("playerinfo-set", sw, new { identityHash = NormalizeGuidish(identityHash), gameName = gameName.Trim(), added = changes.Added.Count, updated = changes.Updated.Count, deleted = changes.Deleted.Count });
            }
            catch (Exception ex)
            {
                LogFailure("playerinfo-set-failed", sw, ex, new { identityHash = identityHash, gameName = gameName }, "persistence");
            }

            return changes;
        }

        public List<KeyValuePair<string, Dictionary<string, string>>> SearchPlayerInfo(string gameName, string searchString)
        {
            var results = new List<KeyValuePair<string, Dictionary<string, string>>>();
            if (!IsEnabled || IsNullOrWhiteSpace(gameName) || IsNullOrWhiteSpace(searchString))
            {
                return results;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var trimmedGameName = gameName.Trim();
                var needle = searchString.Trim();
                using (var connection = OpenConnection())
                using (var command = CreateCommand(connection, null,
                    "SELECT identity_hash, MIN(CASE " +
                    "WHEN lower(info_value) = lower(@needle) THEN 0 " +
                    "WHEN lower(info_value) LIKE lower(@prefix) THEN 1 " +
                    "WHEN lower(info_value) LIKE lower(@contains) THEN 2 " +
                    "ELSE 99 END) AS best_score " +
                    "FROM player_info " +
                    "WHERE game_name = @gameName " +
                    "AND info_key IN ('LauncherDisplayName', 'DisplayName', 'CharacterName', 'AccountName') " +
                    "AND lower(info_value) LIKE lower(@contains) " +
                    "GROUP BY identity_hash " +
                    "ORDER BY best_score ASC, identity_hash ASC;"))
                {
                    AddParameter(command, "@needle", needle);
                    AddParameter(command, "@prefix", needle + "%");
                    AddParameter(command, "@contains", "%" + needle + "%");
                    AddParameter(command, "@gameName", trimmedGameName);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0))
                            {
                                continue;
                            }

                            var identityHash = reader.GetString(0);
                            var info = GetPlayerInfo(identityHash, trimmedGameName);
                            if (info.Count == 0)
                            {
                                continue;
                            }

                            results.Add(new KeyValuePair<string, Dictionary<string, string>>(NormalizeGuidish(identityHash), info));
                        }
                    }
                }

                results.Sort(delegate (KeyValuePair<string, Dictionary<string, string>> left, KeyValuePair<string, Dictionary<string, string>> right)
                {
                    var leftScore = GetSearchScore(left.Value, searchString);
                    var rightScore = GetSearchScore(right.Value, searchString);
                    var scoreCompare = leftScore.CompareTo(rightScore);
                    if (scoreCompare != 0)
                    {
                        return scoreCompare;
                    }

                    var leftText = GetPrimaryDisplayText(left.Value);
                    var rightText = GetPrimaryDisplayText(right.Value);
                    var textCompare = string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
                    if (textCompare != 0)
                    {
                        return textCompare;
                    }

                    return string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
                });

                LogOperation("playerinfo-search", sw, new { gameName = trimmedGameName, returned = results.Count });
            }
            catch (Exception ex)
            {
                LogFailure("playerinfo-search-failed", sw, ex, new { gameName = gameName, search = searchString });
            }

            return results;
        }

        public bool MutatePlayerInfoRootNoThrow(Func<IDictionary, bool> mutator)
        {
            if (!IsEnabled || mutator == null)
            {
                return false;
            }

            try
            {
                var root = LoadPlayerInfoRootNoThrow();
                if (root == null)
                {
                    return false;
                }

                var changed = false;
                try
                {
                    changed = mutator(root);
                }
                catch
                {
                    changed = false;
                }

                if (changed)
                {
                    SavePlayerInfoRootNoThrow(root);
                }

                return changed;
            }
            catch
            {
                return false;
            }
        }

        public List<Guid> GetFriends(Guid accountId)
        {
            var result = new List<Guid>();
            if (!IsEnabled || accountId == Guid.Empty)
            {
                return result;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var command = CreateCommand(connection, null,
                    "SELECT friend_account_id FROM friends WHERE account_id = @accountId ORDER BY friend_account_id;"))
                {
                    AddParameter(command, "@accountId", NormalizeGuidish(accountId.ToString()));
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0))
                            {
                                continue;
                            }

                            Guid friendId;
                            try
                            {
                                friendId = new Guid(reader.GetString(0));
                            }
                            catch
                            {
                                friendId = Guid.Empty;
                            }

                            if (friendId != Guid.Empty)
                            {
                                result.Add(friendId);
                            }
                        }
                    }
                }

                LogOperation("friends-get", sw, new { accountId = accountId.ToString(), returned = result.Count });
            }
            catch (Exception ex)
            {
                LogFailure("friends-get-failed", sw, ex, new { accountId = accountId.ToString() }, "friends-store");
            }

            return result;
        }

        public void AddFriendship(Guid a, Guid b)
        {
            if (!IsEnabled || a == Guid.Empty || b == Guid.Empty || a == b)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    InsertFriendEdge(connection, transaction, a, b);
                    InsertFriendEdge(connection, transaction, b, a);
                    transaction.Commit();
                }

                LogOperation("friends-add", sw, new { accountId = a.ToString(), friendAccountId = b.ToString() });
            }
            catch (Exception ex)
            {
                LogFailure("friends-add-failed", sw, ex, new { accountId = a.ToString(), friendAccountId = b.ToString() }, "friends-store");
            }
        }

        public void RemoveFriendship(Guid a, Guid b)
        {
            if (!IsEnabled || a == Guid.Empty || b == Guid.Empty)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    DeleteFriendEdge(connection, transaction, a, b);
                    DeleteFriendEdge(connection, transaction, b, a);
                    transaction.Commit();
                }

                LogOperation("friends-remove", sw, new { accountId = a.ToString(), friendAccountId = b.ToString() });
            }
            catch (Exception ex)
            {
                LogFailure("friends-remove-failed", sw, ex, new { accountId = a.ToString(), friendAccountId = b.ToString() }, "friends-store");
            }
        }

        public bool TryGetAccountAuthenticationStats(out int totalAccounts, out int steamAccounts, out int nonSteamAccounts)
        {
            totalAccounts = 0;
            steamAccounts = 0;
            nonSteamAccounts = 0;

            if (!IsEnabled)
            {
                return false;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                {
                    totalAccounts = ExecuteScalarInt(connection, null, "SELECT COUNT(*) FROM accounts;");
                    steamAccounts = ExecuteScalarInt(
                        connection,
                        null,
                        "SELECT COUNT(*) FROM (" +
                        "SELECT DISTINCT s.identity_hash " +
                        "FROM steam_identities s " +
                        "INNER JOIN accounts a ON a.identity_hash = s.identity_hash);");
                }

                if (steamAccounts < 0)
                {
                    steamAccounts = 0;
                }

                if (steamAccounts > totalAccounts)
                {
                    steamAccounts = totalAccounts;
                }

                nonSteamAccounts = totalAccounts - steamAccounts;

                LogOperation("account-auth-stats", sw, new
                {
                    total = totalAccounts,
                    steam = steamAccounts,
                    nonSteam = nonSteamAccounts,
                });
                return true;
            }
            catch (Exception ex)
            {
                LogFailure("account-auth-stats-failed", sw, ex, null);
                totalAccounts = 0;
                steamAccounts = 0;
                nonSteamAccounts = 0;
                return false;
            }
        }

        public AccountDeletionResult DeleteAccountAndRelated(string identityHash)
        {
            var result = new AccountDeletionResult();
            if (!IsEnabled || !IsGuidish(identityHash))
            {
                return result;
            }

            var normalizedIdentity = NormalizeGuidish(identityHash);
            var sw = Stopwatch.StartNew();
            try
            {
                using (var connection = OpenConnection())
                using (var transaction = connection.BeginTransaction())
                {
                    result.SteamIdentityRowsDeleted = ExecuteNonQueryCount(connection, transaction,
                        "DELETE FROM steam_identities WHERE identity_hash = @identityHash;",
                        "@identityHash", normalizedIdentity);
                    result.CredentialIdentityRowsDeleted = ExecuteNonQueryCount(connection, transaction,
                        "DELETE FROM credential_identities WHERE identity_hash = @identityHash;",
                        "@identityHash", normalizedIdentity);
                    result.PlayerInfoRowsDeleted = ExecuteNonQueryCount(connection, transaction,
                        "DELETE FROM player_info WHERE identity_hash = @identityHash;",
                        "@identityHash", normalizedIdentity);
                    result.FriendshipRowsDeleted = ExecuteNonQueryCount(connection, transaction,
                        "DELETE FROM friends WHERE account_id = @identityHash OR friend_account_id = @identityHash;",
                        "@identityHash", normalizedIdentity);
                    result.AccountRowsDeleted = ExecuteNonQueryCount(connection, transaction,
                        "DELETE FROM accounts WHERE identity_hash = @identityHash;",
                        "@identityHash", normalizedIdentity);

                    transaction.Commit();
                }

                LogOperation("account-delete", sw, new
                {
                    identityHash = normalizedIdentity,
                    accounts = result.AccountRowsDeleted,
                    steamIdentities = result.SteamIdentityRowsDeleted,
                    credentialIdentities = result.CredentialIdentityRowsDeleted,
                    playerInfo = result.PlayerInfoRowsDeleted,
                    friendships = result.FriendshipRowsDeleted,
                });
            }
            catch (Exception ex)
            {
                result.Failed = true;
                result.ErrorMessage = ex.Message;
                LogFailure("account-delete-failed", sw, ex, new { identityHash = normalizedIdentity });
            }

            return result;
        }

        public int DeletePlayerInfoForIdentity(string identityHash)
        {
            if (!IsEnabled || !IsGuidish(identityHash))
            {
                return 0;
            }

            try
            {
                using (var connection = OpenConnection())
                {
                    return ExecuteNonQueryCount(connection, null,
                        "DELETE FROM player_info WHERE identity_hash = @identityHash;",
                        "@identityHash", NormalizeGuidish(identityHash));
                }
            }
            catch
            {
                return 0;
            }
        }

        public int DeleteFriendshipsForAccount(Guid accountId)
        {
            if (!IsEnabled || accountId == Guid.Empty)
            {
                return 0;
            }

            try
            {
                using (var connection = OpenConnection())
                {
                    return ExecuteNonQueryCount(connection, null,
                        "DELETE FROM friends WHERE account_id = @identityHash OR friend_account_id = @identityHash;",
                        "@identityHash", NormalizeGuidish(accountId.ToString()));
                }
            }
            catch
            {
                return 0;
            }
        }

        private void EnsureBootstrapped(bool migrateJsonToSqlite)
        {
            if (!IsEnabled || IsNullOrWhiteSpace(_databasePath))
            {
                return;
            }

            lock (BootstrapLock)
            {
                if (BootstrappedPaths.ContainsKey(_databasePath))
                {
                    return;
                }

                var sw = Stopwatch.StartNew();
                var importedDocumentCount = 0;
                var autoMigrated = false;
                try
                {
                    var parent = Path.GetDirectoryName(_databasePath);
                    if (!IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    using (var connection = OpenConnection())
                    {
                        ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
                        ExecuteNonQuery(connection, null, "PRAGMA synchronous=NORMAL;");
                        ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON;");
                        CreateSchema(connection);
                        if (migrateJsonToSqlite && IsDatabaseEmpty(connection))
                        {
                            importedDocumentCount = ImportLegacyJson(connection);
                            autoMigrated = importedDocumentCount > 0;
                        }
                    }

                    BootstrappedPaths[_databasePath] = true;
                    LogOperation("bootstrap-complete", sw, new { databasePath = _databasePath, autoMigrated = autoMigrated, importedDocumentCount = importedDocumentCount });
                }
                catch (Exception ex)
                {
                    LogFailure("bootstrap-failed", sw, ex, new { databasePath = _databasePath });
                    throw;
                }
            }
        }

        private void CreateSchema(SqliteConnection connection)
        {
            ExecuteNonQuery(connection, null,
                "CREATE TABLE IF NOT EXISTS accounts (" +
                "identity_hash TEXT NOT NULL PRIMARY KEY, " +
                "display_name TEXT NULL, " +
                "last_career_index INTEGER NOT NULL DEFAULT 0, " +
                "account_json TEXT NOT NULL, " +
                "updated_utc TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS steam_identities (" +
                "steam_id64 TEXT NOT NULL PRIMARY KEY, " +
                "identity_hash TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS credential_identities (" +
                "email TEXT NOT NULL PRIMARY KEY, " +
                "identity_hash TEXT NOT NULL);" +
                "CREATE TABLE IF NOT EXISTS player_info (" +
                "identity_hash TEXT NOT NULL, " +
                "game_name TEXT NOT NULL, " +
                "info_key TEXT NOT NULL, " +
                "info_value TEXT NULL, " +
                "updated_utc TEXT NOT NULL, " +
                "PRIMARY KEY(identity_hash, game_name, info_key));" +
                "CREATE TABLE IF NOT EXISTS friends (" +
                "account_id TEXT NOT NULL, " +
                "friend_account_id TEXT NOT NULL, " +
                "updated_utc TEXT NOT NULL, " +
                "PRIMARY KEY(account_id, friend_account_id));" +
                "CREATE INDEX IF NOT EXISTS idx_accounts_display_name ON accounts(display_name);" +
                "CREATE INDEX IF NOT EXISTS idx_player_info_game_name_key_value ON player_info(game_name, info_key, info_value);" +
                "CREATE INDEX IF NOT EXISTS idx_player_info_identity_game ON player_info(identity_hash, game_name);" +
                "CREATE INDEX IF NOT EXISTS idx_friends_account_id ON friends(account_id);" +
                "CREATE INDEX IF NOT EXISTS idx_credential_identities_identity_hash ON credential_identities(identity_hash);" +
                "CREATE INDEX IF NOT EXISTS idx_steam_identities_identity_hash ON steam_identities(identity_hash);");
        }

        private bool IsDatabaseEmpty(SqliteConnection connection)
        {
            return ExecuteScalarInt(connection, null, "SELECT COUNT(*) FROM accounts;") == 0
                && ExecuteScalarInt(connection, null, "SELECT COUNT(*) FROM player_info;") == 0
                && ExecuteScalarInt(connection, null, "SELECT COUNT(*) FROM friends;") == 0;
        }

        private int ImportLegacyJson(SqliteConnection connection)
        {
            var importedCount = 0;
            using (var transaction = connection.BeginTransaction())
            {
                if (File.Exists(_accountJsonPath))
                {
                    var accountJson = File.ReadAllText(_accountJsonPath, Encoding.UTF8);
                    var store = Json.DeserializeObject(accountJson) as IDictionary;
                    if (store != null)
                    {
                        ImportAccountStore(connection, transaction, store);
                        importedCount++;
                    }
                }

                if (File.Exists(_playerInfoJsonPath))
                {
                    var playerInfoJson = File.ReadAllText(_playerInfoJsonPath, Encoding.UTF8);
                    var root = Json.DeserializeObject(playerInfoJson) as IDictionary;
                    if (root != null)
                    {
                        ImportPlayerInfoRoot(connection, transaction, root);
                        importedCount++;
                    }
                }

                if (File.Exists(_friendsJsonPath))
                {
                    var friendsJson = File.ReadAllText(_friendsJsonPath, Encoding.UTF8);
                    var root = Json.DeserializeObject(friendsJson) as IDictionary;
                    if (root != null)
                    {
                        ImportFriendsRoot(connection, transaction, root);
                        importedCount++;
                    }
                }

                transaction.Commit();
            }

            return importedCount;
        }

        private void ImportAccountStore(SqliteConnection connection, SqliteTransaction transaction, IDictionary loaded)
        {
            if (loaded == null)
            {
                return;
            }

            IDictionary store = loaded;
            if (!(loaded.Contains("Accounts") && loaded["Accounts"] is IDictionary))
            {
                var legacyAccount = loaded;
                var identity = GetString(legacyAccount, "IdentityHash");
                if (!IsGuidish(identity))
                {
                    identity = NormalizeGuidish(Guid.NewGuid().ToString());
                    legacyAccount["IdentityHash"] = identity;
                }

                var migratedStore = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                migratedStore["SchemaVersion"] = 2;
                var accounts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                accounts[identity] = legacyAccount;
                migratedStore["Accounts"] = accounts;

                var steamIdentities = GetDict(legacyAccount, "SteamIdentities");
                if (steamIdentities != null)
                {
                    migratedStore["SteamIdentities"] = steamIdentities;
                }

                var steamId64 = GetString(legacyAccount, "SteamId64");
                if (!IsNullOrWhiteSpace(steamId64))
                {
                    var rootSteam = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    rootSteam[steamId64] = identity;
                    migratedStore["SteamIdentities"] = rootSteam;
                }

                store = migratedStore;
            }

            var accountsMap = GetDict(store, "Accounts");
            if (accountsMap != null)
            {
                foreach (DictionaryEntry entry in accountsMap)
                {
                    var identityHash = entry.Key as string;
                    var account = entry.Value as IDictionary;
                    if (!IsGuidish(identityHash) || account == null)
                    {
                        continue;
                    }

                    UpsertAccount(connection, transaction, account, identityHash, true);
                }
            }

            var steamMap = GetDict(store, "SteamIdentities");
            if (steamMap != null)
            {
                foreach (DictionaryEntry entry in steamMap)
                {
                    var steamId64 = entry.Key as string;
                    var identityHash = entry.Value as string;
                    if (!IsNullOrWhiteSpace(steamId64) && IsGuidish(identityHash))
                    {
                        InsertSteamMapping(connection, transaction, steamId64, identityHash);
                    }
                }
            }

            var credentialMap = GetDict(store, "CredentialIdentities");
            if (credentialMap != null)
            {
                foreach (DictionaryEntry entry in credentialMap)
                {
                    var email = NormalizeCredentialEmail(entry.Key as string);
                    var identityHash = entry.Value as string;
                    if (!IsNullOrWhiteSpace(email) && IsGuidish(identityHash))
                    {
                        InsertCredentialMapping(connection, transaction, email, identityHash);
                    }
                }
            }
        }

        private void ImportPlayerInfoRoot(SqliteConnection connection, SqliteTransaction transaction, IDictionary root)
        {
            if (root == null)
            {
                return;
            }

            foreach (DictionaryEntry identityEntry in root)
            {
                var identityHash = identityEntry.Key as string;
                var byIdentity = identityEntry.Value as IDictionary;
                if (!IsGuidish(identityHash) || byIdentity == null)
                {
                    continue;
                }

                foreach (DictionaryEntry gameEntry in byIdentity)
                {
                    var gameName = gameEntry.Key as string;
                    var byGame = gameEntry.Value as IDictionary;
                    if (IsNullOrWhiteSpace(gameName) || byGame == null)
                    {
                        continue;
                    }

                    foreach (DictionaryEntry itemEntry in byGame)
                    {
                        var key = itemEntry.Key as string;
                        if (IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        var value = itemEntry.Value != null ? itemEntry.Value.ToString() : null;
                        ExecuteNonQuery(connection, transaction,
                            "INSERT OR REPLACE INTO player_info (identity_hash, game_name, info_key, info_value, updated_utc) VALUES (@identityHash, @gameName, @infoKey, @infoValue, @updatedUtc);",
                            "@identityHash", NormalizeGuidish(identityHash),
                            "@gameName", gameName.Trim(),
                            "@infoKey", key,
                            "@infoValue", value,
                            "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private void ImportFriendsRoot(SqliteConnection connection, SqliteTransaction transaction, IDictionary root)
        {
            var friends = GetDict(root, "Friends");
            if (friends == null)
            {
                return;
            }

            foreach (DictionaryEntry entry in friends)
            {
                var accountId = entry.Key as string;
                if (!IsGuidish(accountId) || entry.Value == null)
                {
                    continue;
                }

                var values = ToStringList(entry.Value);
                for (var i = 0; i < values.Count; i++)
                {
                    if (!IsGuidish(values[i]))
                    {
                        continue;
                    }

                    ExecuteNonQuery(connection, transaction,
                        "INSERT OR IGNORE INTO friends (account_id, friend_account_id, updated_utc) VALUES (@accountId, @friendAccountId, @updatedUtc);",
                        "@accountId", NormalizeGuidish(accountId),
                        "@friendAccountId", NormalizeGuidish(values[i]),
                        "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                }
            }
        }

        private IDictionary LoadPlayerInfoRootNoThrow()
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            using (var connection = OpenConnection())
            using (var command = CreateCommand(connection, null,
                "SELECT identity_hash, game_name, info_key, info_value FROM player_info ORDER BY identity_hash, game_name, info_key;"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                    {
                        continue;
                    }

                    var identityHash = reader.GetString(0);
                    var gameName = reader.GetString(1);
                    var infoKey = reader.GetString(2);
                    var infoValue = reader.IsDBNull(3) ? null : reader.GetString(3);

                    var byIdentity = GetOrCreateDict(root, NormalizeGuidish(identityHash));
                    var byGame = GetOrCreateDict(byIdentity, gameName);
                    byGame[infoKey] = infoValue;
                }
            }

            return root;
        }

        private void SavePlayerInfoRootNoThrow(IDictionary root)
        {
            using (var connection = OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                ExecuteNonQuery(connection, transaction, "DELETE FROM player_info;");
                ImportPlayerInfoRoot(connection, transaction, root);
                transaction.Commit();
            }
        }

        private Dictionary<string, string> LoadPlayerInfoMap(SqliteConnection connection, SqliteTransaction transaction, string identityHash, string gameName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var command = CreateCommand(connection, transaction,
                "SELECT info_key, info_value FROM player_info WHERE identity_hash = @identityHash AND game_name = @gameName;"))
            {
                AddParameter(command, "@identityHash", identityHash);
                AddParameter(command, "@gameName", gameName);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(0))
                        {
                            continue;
                        }

                        result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }
            }

            return result;
        }

        private IDictionary LoadAccountByIdentity(SqliteConnection connection, string identityHash)
        {
            if (!IsGuidish(identityHash))
            {
                return null;
            }

            using (var command = CreateCommand(connection, null,
                "SELECT account_json FROM accounts WHERE identity_hash = @identityHash;"))
            {
                AddParameter(command, "@identityHash", NormalizeGuidish(identityHash));
                var accountJson = command.ExecuteScalar() as string;
                return DeserializeDictionary(accountJson);
            }
        }

        private void EnsureAccountExists(SqliteConnection connection, SqliteTransaction transaction, string identityHash)
        {
            if (!IsGuidish(identityHash))
            {
                return;
            }

            var existing = ExecuteScalarString(connection, transaction,
                "SELECT identity_hash FROM accounts WHERE identity_hash = @identityHash;",
                "@identityHash", NormalizeGuidish(identityHash));
            if (IsGuidish(existing))
            {
                return;
            }

            var account = CreateFreshAccountForIdentity(identityHash);
            UpsertAccount(connection, transaction, account, identityHash, true);
        }

        private void UpsertAccount(SqliteConnection connection, SqliteTransaction transaction, IDictionary account, string identityHash, bool ensureDisplayName)
        {
            if (account == null)
            {
                return;
            }

            var normalizedIdentity = IsGuidish(identityHash)
                ? NormalizeGuidish(identityHash)
                : NormalizeGuidish(GetString(account, "IdentityHash"));
            if (!IsGuidish(normalizedIdentity))
            {
                normalizedIdentity = NormalizeGuidish(Guid.NewGuid().ToString());
            }

            account["IdentityHash"] = normalizedIdentity;
            if (ensureDisplayName)
            {
                EnsureDisplayNameIsAnonymized(account, normalizedIdentity);
            }

            var displayName = GetString(account, "DisplayName");
            var lastCareerIndex = GetInt(account, "LastCareerIndex", 0);
            var accountJson = Json.Serialize(account);
            ExecuteNonQuery(connection, transaction,
                "INSERT INTO accounts (identity_hash, display_name, last_career_index, account_json, updated_utc) VALUES (@identityHash, @displayName, @lastCareerIndex, @accountJson, @updatedUtc) " +
                "ON CONFLICT(identity_hash) DO UPDATE SET display_name = excluded.display_name, last_career_index = excluded.last_career_index, account_json = excluded.account_json, updated_utc = excluded.updated_utc;",
                "@identityHash", normalizedIdentity,
                "@displayName", displayName,
                "@lastCareerIndex", lastCareerIndex,
                "@accountJson", accountJson,
                "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        private void InsertSteamMapping(SqliteConnection connection, SqliteTransaction transaction, string steamId64, string identityHash)
        {
            ExecuteNonQuery(connection, transaction,
                "INSERT INTO steam_identities (steam_id64, identity_hash) VALUES (@steamId64, @identityHash) " +
                "ON CONFLICT(steam_id64) DO UPDATE SET identity_hash = excluded.identity_hash;",
                "@steamId64", steamId64,
                "@identityHash", NormalizeGuidish(identityHash));
        }

        private void InsertCredentialMapping(SqliteConnection connection, SqliteTransaction transaction, string email, string identityHash)
        {
            ExecuteNonQuery(connection, transaction,
                "INSERT INTO credential_identities (email, identity_hash) VALUES (@email, @identityHash) " +
                "ON CONFLICT(email) DO UPDATE SET identity_hash = excluded.identity_hash;",
                "@email", email,
                "@identityHash", NormalizeGuidish(identityHash));
        }

        private void ReplaceCredentialMapping(SqliteConnection connection, SqliteTransaction transaction, string identityHash, string email)
        {
            ExecuteNonQuery(connection, transaction,
                "DELETE FROM credential_identities WHERE identity_hash = @identityHash;",
                "@identityHash", NormalizeGuidish(identityHash));
            InsertCredentialMapping(connection, transaction, email, identityHash);
        }

        private void InsertFriendEdge(SqliteConnection connection, SqliteTransaction transaction, Guid a, Guid b)
        {
            ExecuteNonQuery(connection, transaction,
                "INSERT OR IGNORE INTO friends (account_id, friend_account_id, updated_utc) VALUES (@accountId, @friendAccountId, @updatedUtc);",
                "@accountId", NormalizeGuidish(a.ToString()),
                "@friendAccountId", NormalizeGuidish(b.ToString()),
                "@updatedUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }

        private void DeleteFriendEdge(SqliteConnection connection, SqliteTransaction transaction, Guid a, Guid b)
        {
            ExecuteNonQuery(connection, transaction,
                "DELETE FROM friends WHERE account_id = @accountId AND friend_account_id = @friendAccountId;",
                "@accountId", NormalizeGuidish(a.ToString()),
                "@friendAccountId", NormalizeGuidish(b.ToString()));
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection("Data Source=" + _databasePath + ";Version=3;");
            connection.Open();
            ExecuteNonQuery(connection, null, "PRAGMA busy_timeout = " + _busyTimeoutMs.ToString(CultureInfo.InvariantCulture) + ";");
            ExecuteNonQuery(connection, null, "PRAGMA foreign_keys=ON;");
            return connection;
        }

        private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            if (transaction != null)
            {
                command.Transaction = transaction;
            }
            return command;
        }

        private static void AddParameter(SqliteCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? (object)DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                command.ExecuteNonQuery();
            }
        }

        private static int ExecuteNonQueryCount(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                return command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0, string name1, object value1)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                AddParameter(command, name1, value1);
                command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0, string name1, object value1, string name2, object value2)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                AddParameter(command, name1, value1);
                AddParameter(command, name2, value2);
                command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0, string name1, object value1, string name2, object value2, string name3, object value3)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                AddParameter(command, name1, value1);
                AddParameter(command, name2, value2);
                AddParameter(command, name3, value3);
                command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0, string name1, object value1, string name2, object value2, string name3, object value3, string name4, object value4)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                AddParameter(command, name1, value1);
                AddParameter(command, name2, value2);
                AddParameter(command, name3, value3);
                AddParameter(command, name4, value4);
                command.ExecuteNonQuery();
            }
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0, string name1, object value1, string name2, object value2, string name3, object value3, string name4, object value4, string name5, object value5)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                AddParameter(command, name1, value1);
                AddParameter(command, name2, value2);
                AddParameter(command, name3, value3);
                AddParameter(command, name4, value4);
                AddParameter(command, name5, value5);
                command.ExecuteNonQuery();
            }
        }

        private static string ExecuteScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static string ExecuteScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        private static int ExecuteScalarInt(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private static int ExecuteScalarInt(SqliteConnection connection, SqliteTransaction transaction, string sql, string name0, object value0)
        {
            using (var command = CreateCommand(connection, transaction, sql))
            {
                AddParameter(command, name0, value0);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }

        private void LogOperation(string op, Stopwatch sw, object payload)
        {
            try
            {
                if (_logger == null)
                {
                    return;
                }

                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                dict["ts"] = RequestLogger.UtcNowIso();
                dict["type"] = "sqlite";
                dict["op"] = op;
                dict["databasePath"] = _databasePath;
                dict["durationMs"] = sw.ElapsedMilliseconds;
                MergePayload(dict, payload);
                _logger.Log(dict);
            }
            catch
            {
            }
        }

        private void LogFailure(string op, Stopwatch sw, Exception ex, object payload)
        {
            LogFailure(op, sw, ex, payload, "sqlite");
        }

        private void LogFailure(string op, Stopwatch sw, Exception ex, object payload, string type)
        {
            try
            {
                if (_logger == null)
                {
                    return;
                }

                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                dict["ts"] = RequestLogger.UtcNowIso();
                dict["type"] = type;
                dict["op"] = op;
                dict["path"] = _databasePath;
                dict["durationMs"] = sw.ElapsedMilliseconds;
                dict["message"] = ex != null ? ex.Message : null;
                MergePayload(dict, payload);
                _logger.Log(dict);
            }
            catch
            {
            }
        }

        private static void MergePayload(Dictionary<string, object> target, object payload)
        {
            if (target == null || payload == null)
            {
                return;
            }

            var dict = payload as IDictionary;
            if (dict != null)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    var key = entry.Key as string;
                    if (!IsNullOrWhiteSpace(key))
                    {
                        target[key] = entry.Value;
                    }
                }
                return;
            }

            var properties = payload.GetType().GetProperties();
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                if (!property.CanRead)
                {
                    continue;
                }

                target[property.Name] = property.GetValue(payload, null);
            }
        }

        private static IDictionary CreateFreshAccountForIdentity(string identityHash)
        {
            var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : NormalizeGuidish(Guid.NewGuid().ToString());
            var fresh = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            fresh["IdentityHash"] = normalizedIdentity;
            fresh["DisplayName"] = BuildAnonymizedDisplayName(normalizedIdentity);
            fresh["HasCustomDisplayName"] = false;
            fresh["Careers"] = BuildDefaultCareers(normalizedIdentity);
            fresh["LastCareerIndex"] = 0;
            return fresh;
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
                slot.CharacterIdentifier = NormalizeGuidish(identityHash) + ":" + i.ToString(CultureInfo.InvariantCulture);
                list.Add(slot.ToDictionary());
            }
            return list;
        }

        private static IDictionary DeserializeDictionary(string json)
        {
            if (IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return Json.DeserializeObject(json) as IDictionary;
            }
            catch
            {
                return null;
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 100;
            return serializer;
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
                return Convert.ToInt32(dict[key], CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
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

        private static bool EnsureDisplayNameIsAnonymized(IDictionary account, string identityHash)
        {
            if (account == null)
            {
                return false;
            }

            if (GetBool(account, "HasCustomDisplayName", false))
            {
                return false;
            }

            var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetString(account, "IdentityHash");
            if (IsGuidish(normalizedIdentity))
            {
                normalizedIdentity = NormalizeGuidish(normalizedIdentity);
            }

            var expected = BuildAnonymizedDisplayName(normalizedIdentity);
            var current = GetString(account, "DisplayName");
            if (!IsNullOrWhiteSpace(current) && !IsAnonymizedDisplayName(current))
            {
                account["HasCustomDisplayName"] = true;
                return false;
            }
            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return false;
            }

            account["DisplayName"] = expected;
            account["HasCustomDisplayName"] = false;
            return true;
        }

        private static string NormalizeRequestedDisplayName(string value)
        {
            return IsNullOrWhiteSpace(value) ? null : value.Trim();
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

        private static void CreatePasswordDigest(string password, int iterations, out string hashBase64, out string saltBase64)
        {
            var salt = new byte[16];
            var rng = new System.Security.Cryptography.RNGCryptoServiceProvider();
            rng.GetBytes(salt);

            var derive = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt, iterations);
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
                var derive = new System.Security.Cryptography.Rfc2898DeriveBytes(password, salt, iterations);
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

        private static List<string> ToStringList(object raw)
        {
            var result = new List<string>();
            if (raw == null)
            {
                return result;
            }

            var strings = raw as List<string>;
            if (strings != null)
            {
                result.AddRange(strings);
                return result;
            }

            var arrayList = raw as ArrayList;
            if (arrayList != null)
            {
                for (var i = 0; i < arrayList.Count; i++)
                {
                    if (arrayList[i] != null)
                    {
                        result.Add(arrayList[i].ToString());
                    }
                }
                return result;
            }

            var objects = raw as object[];
            if (objects != null)
            {
                for (var i = 0; i < objects.Length; i++)
                {
                    if (objects[i] != null)
                    {
                        result.Add(objects[i].ToString());
                    }
                }
            }

            return result;
        }

        private static int GetSearchScore(Dictionary<string, string> info, string searchString)
        {
            if (info == null || IsNullOrWhiteSpace(searchString))
            {
                return int.MaxValue;
            }

            var terms = BuildSearchTerms(info);
            if (terms.Count == 0)
            {
                return int.MaxValue;
            }

            var needle = searchString.Trim();
            var best = int.MaxValue;
            for (var i = 0; i < terms.Count; i++)
            {
                var term = terms[i];
                if (IsNullOrWhiteSpace(term))
                {
                    continue;
                }

                if (string.Equals(term, needle, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Min(best, 0);
                    continue;
                }

                if (term.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Min(best, 1);
                    continue;
                }

                if (term.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = Math.Min(best, 2);
                }
            }

            return best;
        }

        private static List<string> BuildSearchTerms(Dictionary<string, string> info)
        {
            var terms = new List<string>();
            AddSearchTerm(terms, TryGet(info, "LauncherDisplayName"));
            var displayName = TryGet(info, "DisplayName");
            AddSearchTerm(terms, displayName);
            AddSearchTerm(terms, ExtractCharacterName(displayName));
            AddSearchTerm(terms, TryGet(info, "CharacterName"));
            AddSearchTerm(terms, TryGet(info, "AccountName"));
            return terms;
        }

        private static void AddSearchTerm(List<string> terms, string value)
        {
            if (terms == null || IsNullOrWhiteSpace(value))
            {
                return;
            }

            var trimmed = value.Trim();
            for (var i = 0; i < terms.Count; i++)
            {
                if (string.Equals(terms[i], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            terms.Add(trimmed);
        }

        private static string GetPrimaryDisplayText(Dictionary<string, string> info)
        {
            if (info == null)
            {
                return string.Empty;
            }

            var launcherDisplayName = TryGet(info, "LauncherDisplayName");
            if (!IsNullOrWhiteSpace(launcherDisplayName))
            {
                return launcherDisplayName;
            }

            var displayName = TryGet(info, "DisplayName");
            if (!IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            return TryGet(info, "AccountName") ?? string.Empty;
        }

        private static string ExtractCharacterName(string displayName)
        {
            if (IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            var semi = displayName.IndexOf(';');
            if (semi < 0 || semi + 1 >= displayName.Length)
            {
                return null;
            }

            return displayName.Substring(semi + 1).Trim();
        }

        private static string TryGet(Dictionary<string, string> info, string key)
        {
            if (info == null || IsNullOrWhiteSpace(key))
            {
                return null;
            }

            string value;
            return info.TryGetValue(key, out value) ? value : null;
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
                return new Guid(value.Trim()).ToString();
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