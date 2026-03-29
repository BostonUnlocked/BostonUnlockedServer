using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private static int LoggedStaticDataDir;

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly object _lock = new object();
        private readonly SqliteLocalStore _sqliteStore;

        private readonly string _accountPath;
        private readonly LocalAccountStore _accountStore;
        private readonly LocalCouponService _couponService;
        private readonly LocalCareerSeedService _careerSeedService;
        private readonly LocalCareerStore _careerStore;
        private readonly LocalSessionStore _sessionStore;
        private readonly LocalPlayerInfoStore _playerInfoStore;

        public LocalUserStore(LocalServiceOptions options, RequestLogger logger)
        {
            _options = options;
            _logger = logger;

            try
            {
                if (System.Threading.Interlocked.Exchange(ref LoggedStaticDataDir, 1) == 0)
                {
                    var dir = _options != null ? _options.StaticDataDir : null;
                    var idsPath = !IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "ids.json") : null;
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "static-data-dir",
                            staticDataDir = dir,
                            staticDataDirExists = !IsNullOrWhiteSpace(dir) && Directory.Exists(dir),
                            idsJsonExists = !IsNullOrWhiteSpace(idsPath) && File.Exists(idsPath),
                        });
                    }
                }
            }
            catch
            {
            }

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
            _sqliteStore = new SqliteLocalStore(options, logger);
            _accountStore = new LocalAccountStore(options, logger, _lock, _sqliteStore);
            _couponService = new LocalCouponService(this);
            _careerSeedService = new LocalCareerSeedService(this);
            _careerStore = new LocalCareerStore(this);
            _sessionStore = new LocalSessionStore(options, logger);
            _playerInfoStore = new LocalPlayerInfoStore(options, logger, _sqliteStore);
        }

        public string GetOrCreateIdentityHash()
        {
            return _accountStore != null ? _accountStore.GetOrCreateIdentityHash() : null;
        }

        public string GetOrCreateIdentityHashForSteamId(ulong steamId64)
        {
            return _accountStore != null ? _accountStore.GetOrCreateIdentityHashForSteamId(steamId64) : null;
        }

        public bool TryRegisterCliffhangerCredentials(string email, string password, string tag, out string identityHash, out string message)
        {
            if (_accountStore == null)
            {
                identityHash = null;
                message = null;
                return false;
            }

            return _accountStore.TryRegisterCliffhangerCredentials(email, password, tag, out identityHash, out message);
        }

        public bool TryAuthenticateCliffhangerCredentials(string email, string password, out string identityHash, out bool isVerified, out string message)
        {
            if (_accountStore == null)
            {
                identityHash = null;
                isVerified = false;
                message = null;
                return false;
            }

            return _accountStore.TryAuthenticateCliffhangerCredentials(email, password, out identityHash, out isVerified, out message);
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
            if (IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            return tag.Trim();
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

            var normalizedIdentity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetString(account, AccountStoreLegacyIdentityHashKey);
            if (IsGuidish(normalizedIdentity))
            {
                normalizedIdentity = NormalizeGuidish(normalizedIdentity);
            }

            var expected = BuildAnonymizedDisplayName(normalizedIdentity);
            var current = GetString(account, "DisplayName");
            if (string.Equals(current, expected, StringComparison.Ordinal))
            {
                return false;
            }

            account["DisplayName"] = expected;
            return true;
        }

        public void RunDisplayNameFormatMigrationOnStartup()
        {
            lock (_lock)
            {
                var accountsUpdated = 0;
                var playerInfoUpdated = 0;

                var accountsChanged = MigrateAccountDisplayNamesNoLock(ref accountsUpdated);
                var playerInfoChanged = MigratePlayerInfoDisplayNamesNoLock(ref playerInfoUpdated);

                try
                {
                    if (_logger != null)
                    {
                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "startup-migration",
                            migration = "display-name-format",
                            accountEntriesUpdated = accountsUpdated,
                            playerInfoEntriesUpdated = playerInfoUpdated,
                            changed = accountsChanged || playerInfoChanged,
                        });
                    }
                }
                catch
                {
                }
            }
        }

        private bool MigrateAccountDisplayNamesNoLock(ref int updatedCount)
        {
            return _accountStore != null && _accountStore.MigrateDisplayNames(ref updatedCount);
        }

        private bool MigratePlayerInfoDisplayNamesNoLock(ref int updatedCount)
        {
            if (_playerInfoStore == null)
            {
                return false;
            }

            var updatedCountLocal = updatedCount;
            var changed = _playerInfoStore.MutateRootNoThrow(delegate (IDictionary root)
            {
                var rootChanged = false;
                foreach (DictionaryEntry identityEntry in root)
                {
                    var identityHash = identityEntry.Key as string;
                    var byIdentity = identityEntry.Value as IDictionary;
                    if (byIdentity == null)
                    {
                        continue;
                    }

                    foreach (DictionaryEntry gameEntry in byIdentity)
                    {
                        var byGame = gameEntry.Value as IDictionary;
                        if (byGame == null)
                        {
                            continue;
                        }

                        if (MigratePlayerInfoDisplayNamesForGameNoLock(identityHash, byGame, ref updatedCountLocal))
                        {
                            rootChanged = true;
                        }
                    }
                }

                return rootChanged;
            });

            updatedCount = updatedCountLocal;
            return changed;
        }

        private static bool MigratePlayerInfoDisplayNamesForGameNoLock(string identityHash, IDictionary byGame, ref int updatedCount)
        {
            if (byGame == null)
            {
                return false;
            }

            var changed = false;
            var expected = BuildAnonymizedDisplayName(identityHash);
            var launcherDisplayName = GetString(byGame, "LauncherDisplayName");
            if (!string.Equals(launcherDisplayName, expected, StringComparison.Ordinal))
            {
                byGame["LauncherDisplayName"] = expected;
                updatedCount++;
                changed = true;
            }

            var displayName = GetString(byGame, "DisplayName");
            if (IsNullOrWhiteSpace(displayName))
            {
                return changed;
            }

            var semi = displayName.IndexOf(';');
            var rewrittenDisplayName = expected;
            if (semi >= 0)
            {
                var suffix = semi + 1 < displayName.Length ? displayName.Substring(semi + 1) : string.Empty;
                rewrittenDisplayName = expected + ";" + suffix;
            }

            if (string.Equals(displayName, rewrittenDisplayName, StringComparison.Ordinal))
            {
                return changed;
            }

            byGame["DisplayName"] = rewrittenDisplayName;
            updatedCount++;
            return true;
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

        public string GetDisplayName()
        {
            return GetDisplayName(GetOrCreateIdentityHash());
        }

        public string GetDisplayName(string identityHash)
        {
            return _accountStore != null ? _accountStore.GetDisplayName(identityHash) : null;
        }

        public bool TrySetDisplayName(string identityHash, string requestedDisplayName, out string normalizedDisplayName, out string errorMessage)
        {
            normalizedDisplayName = null;
            errorMessage = null;

            if (_accountStore == null)
            {
                errorMessage = "Account store is unavailable.";
                return false;
            }

            return _accountStore.TrySetDisplayName(identityHash, requestedDisplayName, out normalizedDisplayName, out errorMessage);
        }

        public int GetLastCareerIndex()
        {
            return GetLastCareerIndex(GetOrCreateIdentityHash());
        }

        public int GetLastCareerIndex(string identityHash)
        {
            return _accountStore != null ? _accountStore.GetLastCareerIndex(identityHash) : 0;
        }

        public void SetLastCareerIndex(int index)
        {
            SetLastCareerIndex(GetOrCreateIdentityHash(), index);
        }

        public void SetLastCareerIndex(string identityHash, int index)
        {
            if (_accountStore == null)
            {
                return;
            }

            _accountStore.SetLastCareerIndex(identityHash, index);
        }

        public Guid CreateSessionForCurrentIdentity()
        {
            var identity = GetOrCreateIdentityHash();
            return _sessionStore != null ? _sessionStore.CreateSessionForIdentity(identity) : Guid.Empty;
        }

        public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
        {
            identityHash = null;
            return _sessionStore != null && _sessionStore.TryGetIdentityForSession(sessionHash, out identityHash);
        }

        public void SetIdentityForSession(string sessionHash, string identityHash)
        {
            if (_sessionStore != null)
            {
                _sessionStore.SetIdentityForSession(sessionHash, identityHash);
            }
        }

        public Dictionary<string, string> GetPlayerInfo(string gameName)
        {
            return GetPlayerInfo(GetOrCreateIdentityHash(), gameName);
        }

        public Dictionary<string, string> GetPlayerInfo(string identityHash, string gameName)
        {
            if (_playerInfoStore == null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return _playerInfoStore.Get(identityHash, gameName);
        }

        public PlayerInfoChanges SetPlayerInfo(string identityHash, string gameName, Dictionary<string, string> updates)
        {
            if (_playerInfoStore == null)
            {
                return new PlayerInfoChanges();
            }

            return _playerInfoStore.Set(identityHash, gameName, updates);
        }

        public List<KeyValuePair<string, Dictionary<string, string>>> SearchPlayerInfo(string gameName, string searchString)
        {
            if (_playerInfoStore == null)
            {
                return new List<KeyValuePair<string, Dictionary<string, string>>>();
            }

            return _playerInfoStore.Search(gameName, searchString);
        }

        public List<CareerSlot> GetCareers()
        {
            return _careerStore != null ? _careerStore.GetCareers(GetOrCreateIdentityHash()) : new List<CareerSlot>();
        }

        public List<CareerSlot> GetCareers(string identityHash)
        {
            return _careerStore != null ? _careerStore.GetCareers(identityHash) : new List<CareerSlot>();
        }

        public List<OccupiedCareerReference> GetRandomOccupiedCareerReferences(string excludedIdentityHash, int excludedCareerIndex)
        {
            lock (_lock)
            {
                var store = LoadAccountStoreNoThrow(true);
                var accounts = GetOrCreateDict(store, AccountStoreAccountsKey);
                var candidates = new List<OccupiedCareerReference>();
                var normalizedExcludedIdentity = IsGuidish(excludedIdentityHash) ? NormalizeGuidish(excludedIdentityHash) : null;

                foreach (DictionaryEntry accountEntry in accounts)
                {
                    var identityHash = accountEntry.Key as string;
                    if (!IsGuidish(identityHash))
                    {
                        continue;
                    }

                    var normalizedIdentityHash = NormalizeGuidish(identityHash);
                    var careers = _careerStore != null ? _careerStore.GetCareers(normalizedIdentityHash) : null;
                    if (careers == null || careers.Count == 0)
                    {
                        continue;
                    }

                    for (var i = 0; i < careers.Count; i++)
                    {
                        var slot = careers[i];
                        if (slot == null || !slot.IsOccupied)
                        {
                            continue;
                        }

                        if (!IsNullOrWhiteSpace(normalizedExcludedIdentity)
                            && string.Equals(normalizedIdentityHash, normalizedExcludedIdentity, StringComparison.OrdinalIgnoreCase)
                            && slot.Index == excludedCareerIndex)
                        {
                            continue;
                        }

                        var slotCopy = CareerSlot.FromDictionary(slot.ToDictionary());
                        if (slotCopy == null)
                        {
                            continue;
                        }

                        if (IsNullOrWhiteSpace(slotCopy.CharacterIdentifier))
                        {
                            slotCopy.CharacterIdentifier = normalizedIdentityHash + ":" + slotCopy.Index.ToString(CultureInfo.InvariantCulture);
                        }

                        candidates.Add(new OccupiedCareerReference
                        {
                            IdentityHash = normalizedIdentityHash,
                            CareerIndex = slotCopy.Index,
                            Slot = slotCopy,
                        });
                    }
                }

                ShuffleOccupiedCareerReferences(candidates);
                return candidates;
            }
        }

        public CareerSlot GetOrCreateCareer(int index, bool markOccupied)
        {
            return _careerStore != null ? _careerStore.GetOrCreateCareer(GetOrCreateIdentityHash(), index, markOccupied) : null;
        }

        public CareerSlot GetOrCreateCareer(string identityHash, int index, bool markOccupied)
        {
            return _careerStore != null ? _careerStore.GetOrCreateCareer(identityHash, index, markOccupied) : null;
        }

        public void UpsertCareer(CareerSlot slot)
        {
            if (_careerStore == null)
            {
                return;
            }

            _careerStore.UpsertCareer(GetOrCreateIdentityHash(), slot);
        }

        public void UpsertCareer(string identityHash, CareerSlot slot)
        {
            if (_careerStore == null)
            {
                return;
            }

            _careerStore.UpsertCareer(identityHash, slot);
        }

        public bool TryResolveCouponItemPackageCode(string code, out string packageTechnicalName)
        {
            if (_couponService == null)
            {
                packageTechnicalName = null;
                return false;
            }

            return _couponService.TryResolveCouponItemPackageCode(code, out packageTechnicalName);
        }

        public bool ApplyCouponItemPackageToAllCareers(string identityHash, string packageTechnicalName)
        {
            return _couponService != null && _couponService.ApplyCouponItemPackageToAllCareers(identityHash, packageTechnicalName);
        }

        private static void ShuffleOccupiedCareerReferences(List<OccupiedCareerReference> values)
        {
            if (values == null || values.Count < 2)
            {
                return;
            }

            values.Sort(
                delegate(OccupiedCareerReference a, OccupiedCareerReference b)
                {
                    if (ReferenceEquals(a, b))
                    {
                        return 0;
                    }

                    if (a == null)
                    {
                        return 1;
                    }

                    if (b == null)
                    {
                        return -1;
                    }

                    var identityCompare = string.CompareOrdinal(a.IdentityHash ?? string.Empty, b.IdentityHash ?? string.Empty);
                    if (identityCompare != 0)
                    {
                        return identityCompare;
                    }

                    var indexCompare = a.CareerIndex.CompareTo(b.CareerIndex);
                    if (indexCompare != 0)
                    {
                        return indexCompare;
                    }

                    var nameCompare = string.CompareOrdinal(
                        a.Slot != null ? a.Slot.CharacterName ?? string.Empty : string.Empty,
                        b.Slot != null ? b.Slot.CharacterName ?? string.Empty : string.Empty);
                    if (nameCompare != 0)
                    {
                        return nameCompare;
                    }

                    return string.CompareOrdinal(
                        a.Slot != null ? a.Slot.CharacterIdentifier ?? string.Empty : string.Empty,
                        b.Slot != null ? b.Slot.CharacterIdentifier ?? string.Empty : string.Empty);
                });

            // Stable, deterministic daily shuffle so the same day yields the same roster order.
            var utcDay = DateTime.UtcNow.Date;
            var daySeed = unchecked((int)((utcDay.Ticks / TimeSpan.TicksPerDay) & 0x7FFFFFFF)) ^ 0x3A5F2D17;
            var random = new Random(daySeed);
            for (var i = values.Count - 1; i > 0; i--)
            {
                var swapIndex = random.Next(i + 1);
                var temp = values[i];
                values[i] = values[swapIndex];
                values[swapIndex] = temp;
            }
        }

        private bool ApplyCouponItemPackagesToCareerNoLock(string identityHash, CareerSlot slot)
        {
            return _couponService != null && _couponService.ApplyCouponItemPackagesToCareerNoLock(identityHash, slot);
        }

        private bool EnsureAllCouponEntitlementItemsPresentNoLock(CareerSlot slot, List<string> entitledPackages)
        {
            return _couponService != null && _couponService.EnsureAllCouponEntitlementItemsPresentNoLock(slot, entitledPackages);
        }

        private List<string> GetCouponItemPackageEntitlementsForIdentityNoLock(string identityHash)
        {
            return _couponService != null
                ? _couponService.GetCouponItemPackageEntitlementsForIdentityNoLock(identityHash)
                : new List<string>();
        }

        public CareerSlot DeactivateCareerSlot(int index, string hubId)
        {
            return _careerStore != null
                ? _careerStore.DeactivateCareerSlot(GetOrCreateIdentityHash(), index, hubId)
                : null;
        }

        public CareerSlot DeactivateCareerSlot(string identityHash, int index, string hubId)
        {
            return _careerStore != null
                ? _careerStore.DeactivateCareerSlot(identityHash, index, hubId)
                : null;
        }

    }

    public sealed class PlayerInfoChanges
    {
        public PlayerInfoChanges()
        {
            Added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Updated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Deleted = new List<string>();
        }

        public Dictionary<string, string> Added { get; private set; }
        public Dictionary<string, string> Updated { get; private set; }
        public List<string> Deleted { get; private set; }
    }

    public sealed class OccupiedCareerReference
    {
        public string IdentityHash;
        public int CareerIndex;
        public CareerSlot Slot;
    }

    public sealed class CareerSlot
    {
        public sealed class EquippedSlotState
        {
            public string ItemId;
            public int InventoryKey;
            public int Quality;
            public int Flavour;

            public IDictionary ToDictionary()
            {
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                dict["ItemId"] = ItemId ?? string.Empty;
                dict["InventoryKey"] = InventoryKey;
                dict["Quality"] = Quality;
                dict["Flavour"] = Flavour;
                return dict;
            }

            public static EquippedSlotState FromDictionary(IDictionary dict)
            {
                if (dict == null)
                {
                    return null;
                }

                var state = new EquippedSlotState();
                state.ItemId = dict.Contains("ItemId") ? (dict["ItemId"] as string) : null;
                if (IsNullOrWhiteSpace(state.ItemId))
                {
                    return null;
                }

                try { if (dict.Contains("InventoryKey") && dict["InventoryKey"] != null) state.InventoryKey = Convert.ToInt32(dict["InventoryKey"], CultureInfo.InvariantCulture); } catch { state.InventoryKey = -1; }
                try { if (dict.Contains("Quality") && dict["Quality"] != null) state.Quality = Convert.ToInt32(dict["Quality"], CultureInfo.InvariantCulture); } catch { state.Quality = 0; }
                try { if (dict.Contains("Flavour") && dict["Flavour"] != null) state.Flavour = Convert.ToInt32(dict["Flavour"], CultureInfo.InvariantCulture); } catch { state.Flavour = -1; }

                if (state.Quality < 0)
                {
                    state.Quality = 0;
                }
                if (state.Quality > byte.MaxValue)
                {
                    state.Quality = byte.MaxValue;
                }
                if (state.Flavour < short.MinValue)
                {
                    state.Flavour = short.MinValue;
                }
                if (state.Flavour > short.MaxValue)
                {
                    state.Flavour = short.MaxValue;
                }

                return state;
            }

            public static EquippedSlotState Create(string itemId, int inventoryKey, int quality, int flavour)
            {
                if (IsNullOrWhiteSpace(itemId))
                {
                    return null;
                }

                var state = new EquippedSlotState();
                state.ItemId = itemId;
                state.InventoryKey = inventoryKey;
                state.Quality = quality;
                state.Flavour = flavour;
                return state;
            }
        }

        public int Index;
        public bool IsOccupied;
        public string CharacterName;
        public string Portrait;
        public string PortraitPath;
        public string Voiceset;
        public bool WantsBackgroundChange;
        // Equipped loadout items (weapons/armor) selected in the character editor.
        // ItemId values are Cliffhanger item-definition IDs; InventoryKey matches the client-provided Item.InventoryKey.
        public string PrimaryWeaponItemId;
        public int PrimaryWeaponInventoryKey;
        public string SecondaryWeaponItemId;
        public int SecondaryWeaponInventoryKey;
        public string ArmorItemId;
        public int ArmorInventoryKey;
        // Cosmetic/equipment selections by itemslot id. Mirrors SRO.Server behavior by retaining
        // inventory references for equipped slots instead of only item id strings.
        public Dictionary<string, EquippedSlotState> EquippedItems;
        // Spendable karma (skill currency). This is what the hub UI displays.
        public int Karma;
        // Cumulative spent karma used for progression reference (Karma + SpentKarma).
        public int SpentKarma;
        // Spendable nuyen (cash). This is what the hub UI displays.
        public int Nuyen;
        public string CharacterIdentifier;
        public bool PendingPersistenceCreation;
        public string HubId;
        public ulong Bodytype;
        public int SkinTextureIndex;
        public ulong BackgroundStory;

        // Purchased skills/talents, grouped by skill-tree technical name.
        // This is serialized into account.json and fed back into PlayerCharacterSnapshot.SkillTreeDefinitions
        // during CareerInfo generation.
        public Dictionary<string, string[]> SkillTreeDefinitions;

        // Minimal persistent story progress to prevent mandatory missions (e.g., prologue) from restarting on relaunch.
        // Serialized into account.json.
        public int MainCampaignCurrentChapter;
        public Dictionary<string, string> MainCampaignMissionStates;
        public List<string> MainCampaignInteractedNpcs;
        public List<string> ActiveUnlocks;
        public Dictionary<string, int> RepeatableUnlockSequencePositions;
        // UTC date ticks for last repeatable-day normalization.
        public long LastRepeatableMissionResetUtcTicks;
        // UTC date ticks for last player-derived henchman rotation.
        public long LastHenchmanRotationUtcTicks;

        // Minimal persistent inventory for hub shops (items bought/sold).
        // Key format: "{ItemId}|{Quality}|{Flavour}" (quality/flavour default to 0/-1).
        public Dictionary<string, int> ItemPossessions;

        // Account-level coupon item packs that have already been materialized into this career's inventory.
        public List<string> AppliedCouponItemPackages;

        public IDictionary ToDictionary()
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            dict["Index"] = Index;
            dict["IsOccupied"] = IsOccupied;
            dict["Name"] = CharacterName ?? string.Empty;
            dict["Portrait"] = Portrait ?? string.Empty;
            dict["PortraitPath"] = PortraitPath ?? string.Empty;
            dict["Voiceset"] = Voiceset ?? string.Empty;
            dict["WantsBackgroundChange"] = WantsBackgroundChange;
            dict["PrimaryWeaponItemId"] = PrimaryWeaponItemId ?? string.Empty;
            dict["PrimaryWeaponInventoryKey"] = PrimaryWeaponInventoryKey;
            dict["SecondaryWeaponItemId"] = SecondaryWeaponItemId ?? string.Empty;
            dict["SecondaryWeaponInventoryKey"] = SecondaryWeaponInventoryKey;
            dict["ArmorItemId"] = ArmorItemId ?? string.Empty;
            dict["ArmorInventoryKey"] = ArmorInventoryKey;
            var equipped = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (EquippedItems != null)
            {
                foreach (var kvp in EquippedItems)
                {
                    if (IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null || IsNullOrWhiteSpace(kvp.Value.ItemId))
                    {
                        continue;
                    }

                    equipped[kvp.Key] = kvp.Value.ToDictionary();
                }
            }
            dict["EquippedItems"] = equipped;
            dict["Karma"] = Karma;
            dict["SpentKarma"] = SpentKarma;
            dict["Nuyen"] = Nuyen;
            dict["CharacterIdentifier"] = CharacterIdentifier ?? string.Empty;
            dict["PendingPersistenceCreation"] = PendingPersistenceCreation;
            dict["HubId"] = HubId ?? string.Empty;
            dict["Bodytype"] = Bodytype;
            dict["SkinTextureIndex"] = SkinTextureIndex;
            dict["BackgroundStory"] = BackgroundStory;
            dict["SkillTreeDefinitions"] = SkillTreeDefinitions ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
            dict["MainCampaignCurrentChapter"] = MainCampaignCurrentChapter;
            dict["MainCampaignMissionStates"] = MainCampaignMissionStates ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            dict["MainCampaignInteractedNpcs"] = MainCampaignInteractedNpcs ?? new List<string>();
            dict["ActiveUnlocks"] = ActiveUnlocks ?? new List<string>();
            dict["RepeatableUnlockSequencePositions"] = RepeatableUnlockSequencePositions ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            dict["LastRepeatableMissionResetUtcTicks"] = LastRepeatableMissionResetUtcTicks;
            dict["LastHenchmanRotationUtcTicks"] = LastHenchmanRotationUtcTicks;
            dict["ItemPossessions"] = ItemPossessions ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            dict["AppliedCouponItemPackages"] = AppliedCouponItemPackages ?? new List<string>();
            return dict;
        }

        public static CareerSlot FromDictionary(IDictionary dict)
        {
            if (dict == null)
            {
                return null;
            }

            var slot = new CareerSlot();
            try
            {
                if (dict.Contains("Index")) slot.Index = Convert.ToInt32(dict["Index"]);
            }
            catch { slot.Index = 0; }

            try
            {
                if (dict.Contains("IsOccupied")) slot.IsOccupied = Convert.ToBoolean(dict["IsOccupied"]);
            }
            catch { slot.IsOccupied = false; }

            slot.CharacterName = dict.Contains("Name") ? (dict["Name"] as string) : null;
            slot.Portrait = dict.Contains("Portrait") ? (dict["Portrait"] as string) : null;
            slot.PortraitPath = dict.Contains("PortraitPath") ? (dict["PortraitPath"] as string) : null;
            slot.Voiceset = dict.Contains("Voiceset") ? (dict["Voiceset"] as string) : null;

            slot.PrimaryWeaponItemId = dict.Contains("PrimaryWeaponItemId") ? (dict["PrimaryWeaponItemId"] as string) : null;
            slot.SecondaryWeaponItemId = dict.Contains("SecondaryWeaponItemId") ? (dict["SecondaryWeaponItemId"] as string) : null;
            slot.ArmorItemId = dict.Contains("ArmorItemId") ? (dict["ArmorItemId"] as string) : null;
            try { if (dict.Contains("PrimaryWeaponInventoryKey")) slot.PrimaryWeaponInventoryKey = Convert.ToInt32(dict["PrimaryWeaponInventoryKey"]); } catch { slot.PrimaryWeaponInventoryKey = 0; }
            try { if (dict.Contains("SecondaryWeaponInventoryKey")) slot.SecondaryWeaponInventoryKey = Convert.ToInt32(dict["SecondaryWeaponInventoryKey"]); } catch { slot.SecondaryWeaponInventoryKey = 1; }
            try { if (dict.Contains("ArmorInventoryKey")) slot.ArmorInventoryKey = Convert.ToInt32(dict["ArmorInventoryKey"]); } catch { slot.ArmorInventoryKey = 2; }

            try
            {
                if (dict.Contains("Karma")) slot.Karma = Convert.ToInt32(dict["Karma"]);
            }
            catch { slot.Karma = 0; }

            try
            {
                if (dict.Contains("SpentKarma")) slot.SpentKarma = Convert.ToInt32(dict["SpentKarma"]);
            }
            catch { slot.SpentKarma = 0; }

            try
            {
                if (dict.Contains("Nuyen")) slot.Nuyen = Convert.ToInt32(dict["Nuyen"]);
            }
            catch { slot.Nuyen = 0; }

            try
            {
                if (dict.Contains("WantsBackgroundChange")) slot.WantsBackgroundChange = Convert.ToBoolean(dict["WantsBackgroundChange"]);
            }
            catch { slot.WantsBackgroundChange = false; }

            slot.EquippedItems = new Dictionary<string, EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (dict.Contains("EquippedItems") && dict["EquippedItems"] != null)
                {
                    var asDict = dict["EquippedItems"] as IDictionary;
                    if (asDict != null)
                    {
                        foreach (DictionaryEntry entry in asDict)
                        {
                            var k = entry.Key as string;
                            if (IsNullOrWhiteSpace(k) || entry.Value == null)
                            {
                                continue;
                            }

                            var asStateDict = entry.Value as IDictionary;
                            if (asStateDict != null)
                            {
                                var state = EquippedSlotState.FromDictionary(asStateDict);
                                if (state != null)
                                {
                                    slot.EquippedItems[k] = state;
                                }
                                continue;
                            }

                            // Backward compatibility for legacy account.json format where value was plain item id.
                            var legacyItemId = entry.Value as string;
                            if (!IsNullOrWhiteSpace(legacyItemId))
                            {
                                slot.EquippedItems[k] = EquippedSlotState.Create(legacyItemId, -1, 0, -1);
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.EquippedItems = new Dictionary<string, EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                if (dict.Contains("LastRepeatableMissionResetUtcTicks") && dict["LastRepeatableMissionResetUtcTicks"] != null)
                {
                    slot.LastRepeatableMissionResetUtcTicks = Convert.ToInt64(dict["LastRepeatableMissionResetUtcTicks"], CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                slot.LastRepeatableMissionResetUtcTicks = 0L;
            }

            try
            {
                if (dict.Contains("LastHenchmanRotationUtcTicks") && dict["LastHenchmanRotationUtcTicks"] != null)
                {
                    slot.LastHenchmanRotationUtcTicks = Convert.ToInt64(dict["LastHenchmanRotationUtcTicks"], CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                slot.LastHenchmanRotationUtcTicks = 0L;
            }

            slot.CharacterIdentifier = dict.Contains("CharacterIdentifier") ? (dict["CharacterIdentifier"] as string) : null;

            if (slot.LastRepeatableMissionResetUtcTicks < 0L) slot.LastRepeatableMissionResetUtcTicks = 0L;
            if (slot.LastHenchmanRotationUtcTicks < 0L) slot.LastHenchmanRotationUtcTicks = 0L;
            try
            {
                if (dict.Contains("Karma") && dict["Karma"] != null) slot.Karma = Convert.ToInt32(dict["Karma"]);
            }
            catch { slot.Karma = 0; }

            try
            {
                if (dict.Contains("SpentKarma") && dict["SpentKarma"] != null) slot.SpentKarma = Convert.ToInt32(dict["SpentKarma"]);
            }
            catch { slot.SpentKarma = 0; }

            try
            {
                if (dict.Contains("PendingPersistenceCreation")) slot.PendingPersistenceCreation = Convert.ToBoolean(dict["PendingPersistenceCreation"]);
            }
            catch { slot.PendingPersistenceCreation = false; }

            slot.HubId = dict.Contains("HubId") ? (dict["HubId"] as string) : null;

            try
            {
                if (dict.Contains("Bodytype") && dict["Bodytype"] != null) slot.Bodytype = Convert.ToUInt64(dict["Bodytype"]);
            }
            catch { slot.Bodytype = 0UL; }

            try
            {
                if (dict.Contains("SkinTextureIndex") && dict["SkinTextureIndex"] != null) slot.SkinTextureIndex = Convert.ToInt32(dict["SkinTextureIndex"]);
            }
            catch { slot.SkinTextureIndex = PlayerCharacterDefaultValues.SkinTextureIndex; }

            try
            {
                if (dict.Contains("BackgroundStory") && dict["BackgroundStory"] != null) slot.BackgroundStory = Convert.ToUInt64(dict["BackgroundStory"]);
            }
            catch { slot.BackgroundStory = 0UL; }

            slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            try
            {
                if (dict.Contains("SkillTreeDefinitions") && dict["SkillTreeDefinitions"] != null)
                {
                    var trees = dict["SkillTreeDefinitions"] as IDictionary;
                    if (trees != null)
                    {
                        foreach (DictionaryEntry entry in trees)
                        {
                            var treeName = entry.Key as string;
                            if (IsNullOrWhiteSpace(treeName) || entry.Value == null)
                            {
                                continue;
                            }

                            var asStringArray = entry.Value as string[];
                            if (asStringArray != null)
                            {
                                slot.SkillTreeDefinitions[treeName] = asStringArray;
                                continue;
                            }

                            var asObjArray = entry.Value as object[];
                            if (asObjArray == null)
                            {
                                var asList = entry.Value as ArrayList;
                                if (asList != null)
                                {
                                    asObjArray = new object[asList.Count];
                                    asList.CopyTo(asObjArray);
                                }
                            }

                            if (asObjArray == null)
                            {
                                continue;
                            }

                            var skills = new List<string>();
                            for (var i = 0; i < asObjArray.Length; i++)
                            {
                                var s = asObjArray[i] as string;
                                if (!IsNullOrWhiteSpace(s) && !skills.Contains(s))
                                {
                                    skills.Add(s);
                                }
                            }

                            slot.SkillTreeDefinitions[treeName] = skills.ToArray();
                        }
                    }
                }
            }
            catch
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }

            try
            {
                if (dict.Contains("MainCampaignCurrentChapter") && dict["MainCampaignCurrentChapter"] != null)
                {
                    slot.MainCampaignCurrentChapter = Convert.ToInt32(dict["MainCampaignCurrentChapter"]);
                }
            }
            catch { slot.MainCampaignCurrentChapter = 0; }

            slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (dict.Contains("MainCampaignMissionStates") && dict["MainCampaignMissionStates"] != null)
                {
                    var asDict = dict["MainCampaignMissionStates"] as IDictionary;
                    if (asDict != null)
                    {
                        foreach (DictionaryEntry entry in asDict)
                        {
                            var k = entry.Key as string;
                            var v = entry.Value as string;
                            if (!IsNullOrWhiteSpace(k) && !IsNullOrWhiteSpace(v))
                            {
                                slot.MainCampaignMissionStates[k] = v;
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            slot.MainCampaignInteractedNpcs = new List<string>();
            try
            {
                if (dict.Contains("MainCampaignInteractedNpcs") && dict["MainCampaignInteractedNpcs"] != null)
                {
                    var asArray = dict["MainCampaignInteractedNpcs"] as object[];
                    if (asArray == null)
                    {
                        var asList = dict["MainCampaignInteractedNpcs"] as ArrayList;
                        if (asList != null)
                        {
                            asArray = new object[asList.Count];
                            asList.CopyTo(asArray);
                        }
                    }

                    if (asArray != null)
                    {
                        for (var i = 0; i < asArray.Length; i++)
                        {
                            var s = asArray[i] as string;
                            if (!IsNullOrWhiteSpace(s) && !slot.MainCampaignInteractedNpcs.Contains(s))
                            {
                                slot.MainCampaignInteractedNpcs.Add(s);
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.MainCampaignInteractedNpcs = new List<string>();
            }

            slot.ActiveUnlocks = new List<string>();
            try
            {
                if (dict.Contains("ActiveUnlocks") && dict["ActiveUnlocks"] != null)
                {
                    var asArray = dict["ActiveUnlocks"] as object[];
                    if (asArray == null)
                    {
                        var asList = dict["ActiveUnlocks"] as ArrayList;
                        if (asList != null)
                        {
                            asArray = new object[asList.Count];
                            asList.CopyTo(asArray);
                        }
                    }

                    if (asArray != null)
                    {
                        for (var i = 0; i < asArray.Length; i++)
                        {
                            var s = asArray[i] as string;
                            if (!IsNullOrWhiteSpace(s) && !slot.ActiveUnlocks.Contains(s))
                            {
                                slot.ActiveUnlocks.Add(s);
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.ActiveUnlocks = new List<string>();
            }

            slot.RepeatableUnlockSequencePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (dict.Contains("RepeatableUnlockSequencePositions") && dict["RepeatableUnlockSequencePositions"] != null)
                {
                    var asDict = dict["RepeatableUnlockSequencePositions"] as IDictionary;
                    if (asDict != null)
                    {
                        foreach (DictionaryEntry entry in asDict)
                        {
                            var key = entry.Key as string;
                            if (IsNullOrWhiteSpace(key) || entry.Value == null)
                            {
                                continue;
                            }

                            try
                            {
                                slot.RepeatableUnlockSequencePositions[key] = Convert.ToInt32(entry.Value, CultureInfo.InvariantCulture);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.RepeatableUnlockSequencePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (slot.CharacterName == null) slot.CharacterName = string.Empty;
            if (slot.Portrait == null) slot.Portrait = string.Empty;
            if (slot.PortraitPath == null) slot.PortraitPath = string.Empty;
            if (slot.Voiceset == null) slot.Voiceset = string.Empty;

            // Backfill portraits for occupied slots created before PortraitPath was populated.
            if (slot.IsOccupied && IsNullOrWhiteSpace(slot.Portrait) && IsNullOrWhiteSpace(slot.PortraitPath))
            {
                slot.PortraitPath = PlayerCharacterDefaultValues.PortraitPath;
                slot.Portrait = slot.PortraitPath;
            }
            if (slot.EquippedItems == null) slot.EquippedItems = new Dictionary<string, EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
            if (slot.CharacterIdentifier == null) slot.CharacterIdentifier = string.Empty;
            if (slot.HubId == null) slot.HubId = string.Empty;
            if (slot.SkillTreeDefinitions == null) slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (slot.MainCampaignMissionStates == null) slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (slot.MainCampaignInteractedNpcs == null) slot.MainCampaignInteractedNpcs = new List<string>();
            if (slot.ActiveUnlocks == null) slot.ActiveUnlocks = new List<string>();
            if (slot.RepeatableUnlockSequencePositions == null) slot.RepeatableUnlockSequencePositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (dict.Contains("ItemPossessions") && dict["ItemPossessions"] != null)
                {
                    var asDict = dict["ItemPossessions"] as IDictionary;
                    if (asDict != null)
                    {
                        foreach (DictionaryEntry entry in asDict)
                        {
                            var k = entry.Key as string;
                            if (IsNullOrWhiteSpace(k) || entry.Value == null)
                            {
                                continue;
                            }
                            try
                            {
                                var v = Convert.ToInt32(entry.Value, CultureInfo.InvariantCulture);
                                if (v != 0)
                                {
                                    slot.ItemPossessions[k] = v;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            slot.AppliedCouponItemPackages = new List<string>();
            try
            {
                if (dict.Contains("AppliedCouponItemPackages") && dict["AppliedCouponItemPackages"] != null)
                {
                    var asObjArray = dict["AppliedCouponItemPackages"] as object[];
                    if (asObjArray == null)
                    {
                        var asList = dict["AppliedCouponItemPackages"] as ArrayList;
                        if (asList != null)
                        {
                            asObjArray = new object[asList.Count];
                            asList.CopyTo(asObjArray);
                        }
                    }

                    if (asObjArray != null)
                    {
                        for (var i = 0; i < asObjArray.Length; i++)
                        {
                            var s = asObjArray[i] as string;
                            if (!IsNullOrWhiteSpace(s) && !slot.AppliedCouponItemPackages.Contains(s))
                            {
                                slot.AppliedCouponItemPackages.Add(s);
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.AppliedCouponItemPackages = new List<string>();
            }
            return slot;
        }

        private static void ShuffleOccupiedCareerReferences(List<OccupiedCareerReference> values)
        {
            if (values == null || values.Count < 2)
            {
                return;
            }

            var seed = unchecked(Environment.TickCount * 397) ^ Guid.NewGuid().GetHashCode();
            var random = new Random(seed);
            for (var i = values.Count - 1; i > 0; i--)
            {
                var swapIndex = random.Next(i + 1);
                var tmp = values[i];
                values[i] = values[swapIndex];
                values[swapIndex] = tmp;
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
