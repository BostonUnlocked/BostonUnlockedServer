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
            _accountStore = new LocalAccountStore(options, logger, _lock);
            _couponService = new LocalCouponService(this);
            _careerSeedService = new LocalCareerSeedService(this);
            _careerStore = new LocalCareerStore(this);
            _sessionStore = new LocalSessionStore(options, logger);
            _playerInfoStore = new LocalPlayerInfoStore(options, logger);
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
            var basis = IsNullOrWhiteSpace(source) ? "OfflineRunner" : source.Trim();
            var bytes = Encoding.UTF8.GetBytes(basis);
            var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var base64 = Convert.ToBase64String(hash);
            return base64.Length <= 8 ? base64 : base64.Substring(0, 8);
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

        private static bool EnsureDisplayNameIsAnonymized(IDictionary account, string fallbackSource)
        {
            if (account == null)
            {
                return false;
            }

            var current = GetString(account, "DisplayName");
            if (IsAnonymizedDisplayName(current))
            {
                return false;
            }

            var source = !IsNullOrWhiteSpace(current) ? current : fallbackSource;
            account["DisplayName"] = BuildAnonymizedDisplayName(source);
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

                        if (MigratePlayerInfoDisplayNamesForGameNoLock(byGame, ref updatedCountLocal))
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

        private static bool MigratePlayerInfoDisplayNamesForGameNoLock(IDictionary byGame, ref int updatedCount)
        {
            if (byGame == null)
            {
                return false;
            }

            var changed = false;
            var launcherDisplayName = GetString(byGame, "LauncherDisplayName");
            if (!IsNullOrWhiteSpace(launcherDisplayName) && !IsAnonymizedDisplayName(launcherDisplayName))
            {
                byGame["LauncherDisplayName"] = BuildAnonymizedDisplayName(launcherDisplayName);
                updatedCount++;
                changed = true;
            }

            var displayName = GetString(byGame, "DisplayName");
            if (IsNullOrWhiteSpace(displayName))
            {
                return changed;
            }

            var semi = displayName.IndexOf(';');
            var accountPart = semi >= 0 ? displayName.Substring(0, semi) : displayName;
            if (IsAnonymizedDisplayName(accountPart))
            {
                return changed;
            }

            var anonymized = BuildAnonymizedDisplayName(accountPart);
            if (semi >= 0)
            {
                var suffix = semi + 1 < displayName.Length ? displayName.Substring(semi + 1) : string.Empty;
                byGame["DisplayName"] = anonymized + ";" + suffix;
            }
            else
            {
                byGame["DisplayName"] = anonymized;
            }

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

        public List<CareerSlot> GetCareers()
        {
            return _careerStore != null ? _careerStore.GetCareers(GetOrCreateIdentityHash()) : new List<CareerSlot>();
        }

        public List<CareerSlot> GetCareers(string identityHash)
        {
            return _careerStore != null ? _careerStore.GetCareers(identityHash) : new List<CareerSlot>();
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

    public sealed class CareerSlot
    {
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
        // Cosmetic/equipment item selections by itemslot id (serialized as string keys).
        public Dictionary<string, string> EquippedItems;
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
            dict["EquippedItems"] = EquippedItems ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

            slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                            var v = entry.Value as string;
                            if (!IsNullOrWhiteSpace(k) && v != null)
                            {
                                slot.EquippedItems[k] = v;
                            }
                        }
                    }
                }
            }
            catch
            {
                slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            slot.CharacterIdentifier = dict.Contains("CharacterIdentifier") ? (dict["CharacterIdentifier"] as string) : null;

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
            if (slot.EquippedItems == null) slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
