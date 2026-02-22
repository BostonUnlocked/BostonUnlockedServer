using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly object _lock = new object();

        private readonly string _accountPath;
        private readonly string _sessionsPath;
        private readonly string _playerInfoPath;

        public LocalUserStore(LocalServiceOptions options, RequestLogger logger)
        {
            _options = options;
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
                // Best-effort only.
            }

            _accountPath = Path.Combine(dataDir, "account.json");
            _sessionsPath = Path.Combine(dataDir, "sessions.json");
            _playerInfoPath = Path.Combine(dataDir, "playerinfo.json");
        }

        public string GetOrCreateIdentityHash()
        {
            lock (_lock)
            {
                var account = LoadAccountNoThrow();
                var identity = GetString(account, "IdentityHash");
                if (IsGuidish(identity))
                {
                    return NormalizeGuidish(identity);
                }

                var created = Guid.NewGuid().ToString();
                account["IdentityHash"] = created;
                if (IsNullOrWhiteSpace(GetString(account, "DisplayName")))
                {
                    account["DisplayName"] = "OfflineRunner";
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
            if (steamId64 == 0)
            {
                return GetOrCreateIdentityHash();
            }

            lock (_lock)
            {
                var steamKey = steamId64.ToString(CultureInfo.InvariantCulture);
                var steamDisplayName = "Steam:" + steamKey;

                // Store mapping at the account-store root so multiple identities can coexist.
                var store = LoadAccountStoreNoThrow(true);
                var steamIdentities = GetOrCreateDict(store, "SteamIdentities");

                var mapped = GetString(steamIdentities, steamKey);
                if (IsGuidish(mapped))
                {
                    var normalized = NormalizeGuidish(mapped);
                    store["SteamId64"] = steamKey;

                    // Ensure the account exists in this same store instance.
                    var accounts = GetOrCreateDict(store, "Accounts");
                    var existingAccount = GetDict(accounts, normalized);
                    if (existingAccount == null)
                    {
                        var createdAccount = BuildFreshAccountForIdentity(normalized);
                        createdAccount["DisplayName"] = steamDisplayName;
                        createdAccount["Careers"] = BuildDefaultCareers(normalized);
                        accounts[normalized] = createdAccount;
                    }
                    else
                    {
                        // If the account is still on the old default name, upgrade to Steam:{id}.
                        var existingDisplayName = GetString(existingAccount, "DisplayName");
                        if (IsNullOrWhiteSpace(existingDisplayName) || string.Equals(existingDisplayName, "OfflineRunner", StringComparison.OrdinalIgnoreCase))
                        {
                            existingAccount["DisplayName"] = steamDisplayName;
                        }
                    }

                    SaveAccountStoreNoThrow(store);
                    return normalized;
                }

                // Migration: if we have exactly one existing account and no mapping, bind it to this steam id.
                // (Prevents accidentally remapping a multi-account store.)
                try
                {
                    var accounts = GetOrCreateDict(store, "Accounts");
                    var count = accounts is ICollection ? ((ICollection)accounts).Count : 0;
                    var hasAnyMapping = steamIdentities is ICollection && ((ICollection)steamIdentities).Count > 0;
                    if (!hasAnyMapping && count == 1)
                    {
                        foreach (DictionaryEntry entry in accounts)
                        {
                            var key = entry.Key as string;
                            if (IsGuidish(key))
                            {
                                var normalized = NormalizeGuidish(key);
                                steamIdentities[steamKey] = normalized;
                                store["SteamId64"] = steamKey;

                                // If the single legacy account has no meaningful name, seed Steam:{id}.
                                var acct = GetDict(accounts, normalized);
                                if (acct != null)
                                {
                                    var existingDisplayName = GetString(acct, "DisplayName");
                                    if (IsNullOrWhiteSpace(existingDisplayName) || string.Equals(existingDisplayName, "OfflineRunner", StringComparison.OrdinalIgnoreCase))
                                    {
                                        acct["DisplayName"] = steamDisplayName;
                                    }
                                }

                                SaveAccountStoreNoThrow(store);
                                return normalized;
                            }
                        }
                    }
                }
                catch
                {
                }

                // First-time identity for this Steam user.
                var created = NormalizeGuidish(Guid.NewGuid().ToString());
                steamIdentities[steamKey] = created;
                store["SteamId64"] = steamKey;

                // Create account directly under Accounts.
                var accountsForCreate = GetOrCreateDict(store, "Accounts");
                var createdAccountForStore = BuildFreshAccountForIdentity(created);
                createdAccountForStore["DisplayName"] = steamDisplayName;
                createdAccountForStore["Careers"] = BuildDefaultCareers(created);
                accountsForCreate[created] = createdAccountForStore;

                SaveAccountStoreNoThrow(store);
                return created;
            }
        }

        public string GetDisplayName()
        {
            return GetDisplayName(GetOrCreateIdentityHash());
        }

        public string GetDisplayName(string identityHash)
        {
            lock (_lock)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                var displayName = GetString(account, "DisplayName");
                if (IsNullOrWhiteSpace(displayName))
                {
                    displayName = "OfflineRunner";
                    account["DisplayName"] = displayName;
                    SaveAccountNoThrow(account);
                }
                return displayName;
            }
        }

        public int GetLastCareerIndex()
        {
            return GetLastCareerIndex(GetOrCreateIdentityHash());
        }

        public int GetLastCareerIndex(string identityHash)
        {
            lock (_lock)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                return GetInt(account, "LastCareerIndex", 0);
            }
        }

        public void SetLastCareerIndex(int index)
        {
            SetLastCareerIndex(GetOrCreateIdentityHash(), index);
        }

        public void SetLastCareerIndex(string identityHash, int index)
        {
            if (index < 0)
            {
                index = 0;
            }

            lock (_lock)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                account["LastCareerIndex"] = index;
                SaveAccountNoThrow(account);
            }
        }

        public Guid CreateSessionForCurrentIdentity()
        {
            lock (_lock)
            {
                var identity = GetOrCreateIdentityHash();
                var session = Guid.NewGuid();

                var sessions = LoadSessionsNoThrow();
                sessions[NormalizeGuidish(session.ToString())] = identity;
                SaveSessionsNoThrow(sessions);
                return session;
            }
        }

        public bool TryGetIdentityForSession(string sessionHash, out string identityHash)
        {
            identityHash = null;
            if (IsNullOrWhiteSpace(sessionHash))
            {
                return false;
            }

            lock (_lock)
            {
                var sessions = LoadSessionsNoThrow();
                string mapped;
                if (sessions.TryGetValue(NormalizeGuidish(sessionHash), out mapped) && IsGuidish(mapped))
                {
                    identityHash = NormalizeGuidish(mapped);
                    return true;
                }
                return false;
            }
        }

        public void SetIdentityForSession(string sessionHash, string identityHash)
        {
            if (IsNullOrWhiteSpace(sessionHash) || IsNullOrWhiteSpace(identityHash))
            {
                return;
            }

            lock (_lock)
            {
                var sessions = LoadSessionsNoThrow();
                sessions[NormalizeGuidish(sessionHash)] = NormalizeGuidish(identityHash);
                SaveSessionsNoThrow(sessions);
            }
        }

        public Dictionary<string, string> GetPlayerInfo(string identityHash, string gameName)
        {
            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName))
            {
                return results;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                var byIdentity = GetDict(root, NormalizeGuidish(identityHash));
                var byGame = byIdentity != null ? GetDict(byIdentity, gameName.Trim()) : null;
                if (byGame == null)
                {
                    return results;
                }

                foreach (DictionaryEntry entry in byGame)
                {
                    var k = entry.Key as string;
                    if (IsNullOrWhiteSpace(k))
                    {
                        continue;
                    }
                    var v = entry.Value as string;
                    results[k] = v;
                }

                return results;
            }
        }

        public PlayerInfoChanges SetPlayerInfo(string identityHash, string gameName, Dictionary<string, string> updates)
        {
            var changes = new PlayerInfoChanges();
            if (IsNullOrWhiteSpace(identityHash) || IsNullOrWhiteSpace(gameName) || updates == null)
            {
                return changes;
            }

            lock (_lock)
            {
                var root = LoadPlayerInfoNoThrow();
                var identityKey = NormalizeGuidish(identityHash);
                var gameKey = gameName.Trim();

                var byIdentity = GetOrCreateDict(root, identityKey);
                var byGame = GetOrCreateDict(byIdentity, gameKey);

                foreach (var kvp in updates)
                {
                    var key = kvp.Key;
                    if (IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    var value = kvp.Value;
                    var hadExisting = byGame.Contains(key);

                    if (value == null)
                    {
                        if (hadExisting)
                        {
                            byGame.Remove(key);
                            changes.Deleted.Add(key);
                        }
                        continue;
                    }

                    if (!hadExisting)
                    {
                        byGame[key] = value;
                        changes.Added[key] = value;
                        continue;
                    }

                    var existing = byGame[key] as string;
                    if (!string.Equals(existing, value, StringComparison.Ordinal))
                    {
                        byGame[key] = value;
                        changes.Updated[key] = value;
                    }
                }

                SavePlayerInfoNoThrow(root);
                return changes;
            }
        }

        public List<CareerSlot> GetCareers()
        {
            return GetCareers(GetOrCreateIdentityHash());
        }

        public List<CareerSlot> GetCareers(string identityHash)
        {
            lock (_lock)
            {
                var account = LoadAccountForIdentityNoThrow(identityHash, true) ?? LoadAccountNoThrow();
                var careersObj = account["Careers"];
                var careersList = CoerceToArrayList(careersObj);
                if (careersList == null)
                {
                    var identity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetOrCreateIdentityHash();
                    careersList = BuildDefaultCareers(identity);
                    account["Careers"] = careersList;
                    SaveAccountNoThrow(account);
                }
                else if (!(careersObj is ArrayList))
                {
                    // Normalize representation so subsequent saves stay stable.
                    account["Careers"] = careersList;
                    SaveAccountNoThrow(account);
                }

                var results = new List<CareerSlot>();
                for (var i = 0; i < careersList.Count; i++)
                {
                    var dict = careersList[i] as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }

                    var slot = CareerSlot.FromDictionary(dict);
                    if (slot != null)
                    {
                        results.Add(slot);
                    }
                }

                results.Sort(delegate (CareerSlot a, CareerSlot b) { return a.Index.CompareTo(b.Index); });
                return results;
            }
        }

        public CareerSlot GetOrCreateCareer(int index, bool markOccupied)
        {
            return GetOrCreateCareer(GetOrCreateIdentityHash(), index, markOccupied);
        }

        public CareerSlot GetOrCreateCareer(string identityHash, int index, bool markOccupied)
        {
            lock (_lock)
            {
                var identity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetOrCreateIdentityHash();
                var account = LoadAccountForIdentityNoThrow(identity, true) ?? LoadAccountNoThrow();
                var careersObj = account["Careers"];
                var careersList = CoerceToArrayList(careersObj);
                if (careersList == null)
                {
                    careersList = BuildDefaultCareers(identity);
                    account["Careers"] = careersList;
                }
                else if (!(careersObj is ArrayList))
                {
                    account["Careers"] = careersList;
                }

                IDictionary found = null;
                for (var i = 0; i < careersList.Count; i++)
                {
                    var dict = careersList[i] as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }
                    var idx = GetInt(dict, "Index", -1);
                    if (idx == index)
                    {
                        found = dict;
                        break;
                    }
                }

                if (found == null)
                {
                    var newSlot = new CareerSlot();
                    newSlot.Index = index;
                    newSlot.IsOccupied = false;
                    newSlot.CharacterName = string.Empty;
                    newSlot.Portrait = string.Empty;
                    newSlot.HubId = "Act01_HUB_02";
                    newSlot.PendingPersistenceCreation = false;
                    newSlot.CharacterIdentifier = NormalizeGuidish(identity) + ":" + index.ToString();
                    careersList.Add(newSlot.ToDictionary());
                    found = (IDictionary)careersList[careersList.Count - 1];
                }

                var slotObj = CareerSlot.FromDictionary(found);
                if (slotObj == null)
                {
                    slotObj = new CareerSlot();
                    slotObj.Index = index;
                    slotObj.CharacterIdentifier = NormalizeGuidish(identity) + ":" + index.ToString();
                }

                if (markOccupied)
                {
                    if (!slotObj.IsOccupied)
                    {
                        slotObj.IsOccupied = true;
                        if (IsNullOrWhiteSpace(slotObj.CharacterName))
                        {
                            slotObj.CharacterName = "NewRunner";
                        }

                        // Align defaults with the retail client. Index 0 is a valid skin selection, so do not
                        // use 0 as an "unset" sentinel.
                        slotObj.SkinTextureIndex = PlayerCharacterDefaultValues.SkinTextureIndex;
                        slotObj.BackgroundStory = PlayerCharacterDefaultValues.BackgroundStory;

                        // Ensure the career selection UI can resolve a portrait for newly created careers.
                        if (IsNullOrWhiteSpace(slotObj.Portrait) && IsNullOrWhiteSpace(slotObj.PortraitPath))
                        {
                            slotObj.PortraitPath = PlayerCharacterDefaultValues.PortraitPath;
                            slotObj.Portrait = slotObj.PortraitPath;
                        }
                        slotObj.PendingPersistenceCreation = true;

                        SeedStarterCosmetics(slotObj);

                        // Starting cash for newly created characters.
                        // (Retail starts with non-zero nuyen; we choose 20,000 for offline.)
                        slotObj.Nuyen = 20000;
                    }

                    // Ensure the character creator has enough cosmetic options (hair/beard) even if the career
                    // was created under an older LocalService version with a minimal starter inventory.
                    if (slotObj.IsOccupied)
                    {
                        SeedStarterHairAndBeardOptions(slotObj);
                    }
                }

                // Ensure identifier is always correct/stable.
                slotObj.CharacterIdentifier = NormalizeGuidish(identity) + ":" + index.ToString();

                // Write back.
                var updatedDict = slotObj.ToDictionary();
                foreach (DictionaryEntry entry in updatedDict)
                {
                    found[entry.Key] = entry.Value;
                }
                SaveAccountNoThrow(account);

                return slotObj;
            }
        }

        private static void SeedStarterCosmetics(CareerSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            if (slot.EquippedItems == null)
            {
                slot.EquippedItems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (slot.ItemPossessions == null)
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            // Slot ids match the client-side character editor mapping (see EditableCharacter.cs).
            // Only seed the requested cosmetic slots.
            SeedSlot(slot, 196913UL, PlayerCharacterDefaultValues.Boots);          // Boots
            SeedSlot(slot, 196910UL, PlayerCharacterDefaultValues.UpperBody);      // UpperBody
            SeedSlot(slot, 196911UL, PlayerCharacterDefaultValues.LowerBody);      // LowerBody
            SeedSlot(slot, 196914UL, PlayerCharacterDefaultValues.UpperUnderware); // UpperUnderwear
            SeedSlot(slot, 196916UL, PlayerCharacterDefaultValues.Hair);           // Hair
            SeedSlot(slot, 196912UL, PlayerCharacterDefaultValues.Gloves);         // Hands/Gloves

            // Character creator populates Hair/FacialHair selectors from the player's owned equipment items
            // (InventoryController.GetEquipmentItems for Itemtype_Hair=196816 and Itemtype_FacialHair=196817).
            // If we only seed the equipped default hair (undercut) and no facial hair items, the UI ends up
            // with only 2 hair options (None + Undercut) and no visible beard selector.
            //
            SeedStarterHairAndBeardOptions(slot);
        }

        private static void SeedStarterHairAndBeardOptions(CareerSlot slot)
        {
            // Grant a small starter set of hair + beard cosmetics as owned items (not equipped).
            OwnItem(slot, "Item_HairAfro");
            OwnItem(slot, "Item_HairBob");
            OwnItem(slot, "Item_HairBraids");
            OwnItem(slot, "Item_HairElvis");
            OwnItem(slot, "Item_HairLong");
            OwnItem(slot, "Item_HairMohawk");
            OwnItem(slot, "Item_HairPage");
            OwnItem(slot, "Item_HairPony");
            OwnItem(slot, "Item_HairQuiff");
            OwnItem(slot, "Item_HairSidebraid");
            OwnItem(slot, "Item_HairSidecut");
            OwnItem(slot, "Item_HairUndercut");
            OwnItem(slot, "Item_HairWarhawk");

            OwnItem(slot, "Item_BeardGoatee");
            OwnItem(slot, "Item_BeardBigMustache");
            OwnItem(slot, "Item_BeardZappa");
            OwnItem(slot, "Item_BeardKlingon");
        }

        private static void OwnItem(CareerSlot slot, string itemId)
        {
            if (slot == null)
            {
                return;
            }
            if (IsNullOrWhiteSpace(itemId))
            {
                return;
            }
            if (slot.ItemPossessions == null)
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            // Key format matches CareerInfoGenerator.BuildInventoryFromSlot(): "{ItemId}|{Quality}|{Flavour}".
            var possessionKey = itemId + "|0|-1";
            int amount;
            if (!slot.ItemPossessions.TryGetValue(possessionKey, out amount) || amount <= 0)
            {
                slot.ItemPossessions[possessionKey] = 1;
            }
        }

        private static void SeedSlot(CareerSlot slot, ulong slotId, string itemId)
        {
            if (slot == null || slotId == 0UL)
            {
                return;
            }
            if (IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            var slotKey = slotId.ToString(CultureInfo.InvariantCulture);
            string existing;
            if (!slot.EquippedItems.TryGetValue(slotKey, out existing) || IsNullOrWhiteSpace(existing))
            {
                slot.EquippedItems[slotKey] = itemId;
            }

            // Ensure the item is also owned (appears in inventory lists / shop UI).
            // Key format matches CareerInfoGenerator.BuildInventoryFromSlot(): "{ItemId}|{Quality}|{Flavour}".
            var possessionKey = itemId + "|0|-1";
            int amount;
            if (!slot.ItemPossessions.TryGetValue(possessionKey, out amount) || amount <= 0)
            {
                slot.ItemPossessions[possessionKey] = 1;
            }
        }

        public void UpsertCareer(CareerSlot slot)
        {
            UpsertCareer(GetOrCreateIdentityHash(), slot);
        }

        public void UpsertCareer(string identityHash, CareerSlot slot)
        {
            if (slot == null)
            {
                return;
            }

            lock (_lock)
            {
                var identity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetOrCreateIdentityHash();
                var account = LoadAccountForIdentityNoThrow(identity, true) ?? LoadAccountNoThrow();

                var careersObj = account["Careers"];
                var careersList = CoerceToArrayList(careersObj);
                if (careersList == null)
                {
                    careersList = BuildDefaultCareers(identity);
                    account["Careers"] = careersList;
                }
                else if (!(careersObj is ArrayList))
                {
                    account["Careers"] = careersList;
                }

                IDictionary found = null;
                for (var i = 0; i < careersList.Count; i++)
                {
                    var dict = careersList[i] as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }
                    var idx = GetInt(dict, "Index", -1);
                    if (idx == slot.Index)
                    {
                        found = dict;
                        break;
                    }
                }

                if (found == null)
                {
                    careersList.Add(slot.ToDictionary());
                    SaveAccountNoThrow(account);
                    return;
                }

                // Ensure identifier is always correct/stable.
                slot.CharacterIdentifier = NormalizeGuidish(identity) + ":" + slot.Index.ToString();

                var updated = slot.ToDictionary();
                foreach (DictionaryEntry entry in updated)
                {
                    found[entry.Key] = entry.Value;
                }
                SaveAccountNoThrow(account);
            }
        }

        public CareerSlot DeactivateCareerSlot(int index, string hubId)
        {
            return DeactivateCareerSlot(GetOrCreateIdentityHash(), index, hubId);
        }

        public CareerSlot DeactivateCareerSlot(string identityHash, int index, string hubId)
        {
            if (index < 0)
            {
                index = 0;
            }

            lock (_lock)
            {
                var identity = IsGuidish(identityHash) ? NormalizeGuidish(identityHash) : GetOrCreateIdentityHash();
                var account = LoadAccountForIdentityNoThrow(identity, true) ?? LoadAccountNoThrow();

                var careersObj = account["Careers"];
                var careersList = CoerceToArrayList(careersObj);
                if (careersList == null)
                {
                    careersList = BuildDefaultCareers(identity);
                    account["Careers"] = careersList;
                }
                else if (!(careersObj is ArrayList))
                {
                    account["Careers"] = careersList;
                }

                // Rebuild the careers list from strongly-typed slots and write back.
                // (We previously mutated nested dictionaries in-place, but the resulting JSON was unchanged in practice.)
                var byIndex = new Dictionary<int, CareerSlot>();
                for (var i = 0; i < careersList.Count; i++)
                {
                    var dict = careersList[i] as IDictionary;
                    if (dict == null)
                    {
                        continue;
                    }
                    var slot = CareerSlot.FromDictionary(dict);
                    if (slot == null)
                    {
                        continue;
                    }
                    byIndex[slot.Index] = slot;
                }

                CareerSlot target;
                if (!byIndex.TryGetValue(index, out target) || target == null)
                {
                    target = new CareerSlot();
                    target.Index = index;
                }

                target.IsOccupied = false;
                target.CharacterName = string.Empty;
                target.Portrait = string.Empty;
                target.PendingPersistenceCreation = false;
                target.HubId = IsNullOrWhiteSpace(hubId) ? "Act01_HUB_02" : hubId;
                target.CharacterIdentifier = NormalizeGuidish(identity) + ":" + index.ToString();
                byIndex[index] = target;

                var rebuilt = new ArrayList();
                foreach (var kvp in byIndex)
                {
                    if (kvp.Value == null)
                    {
                        continue;
                    }

                    // Keep identifiers stable.
                    kvp.Value.CharacterIdentifier = NormalizeGuidish(identity) + ":" + kvp.Value.Index.ToString();
                    rebuilt.Add(kvp.Value.ToDictionary());
                }

                account["Careers"] = rebuilt;
                SaveAccountNoThrow(account);

                return target;
            }
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

        // Minimal persistent inventory for hub shops (items bought/sold).
        // Key format: "{ItemId}|{Quality}|{Flavour}" (quality/flavour default to 0/-1).
        public Dictionary<string, int> ItemPossessions;

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
            dict["ItemPossessions"] = ItemPossessions ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
            return slot;
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
