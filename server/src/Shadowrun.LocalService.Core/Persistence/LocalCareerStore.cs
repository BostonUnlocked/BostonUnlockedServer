using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;

namespace Shadowrun.LocalService.Core.Persistence
{
    public sealed partial class LocalUserStore
    {
        private sealed class LocalCareerStore
        {
            private readonly LocalUserStore _owner;

            public LocalCareerStore(LocalUserStore owner)
            {
                _owner = owner;
            }

            public List<CareerSlot> GetCareers(string identityHash)
            {
                lock (_owner._lock)
                {
                    var identity = LocalUserStore.IsGuidish(identityHash) ? LocalUserStore.NormalizeGuidish(identityHash) : _owner.GetOrCreateIdentityHash();
                    var account = _owner.LoadAccountForIdentityNoThrow(identityHash, true) ?? _owner.LoadAccountNoThrow();
                    var careersList = GetOrCreateCareerListNoLock(account, identity, true);

                    var results = new List<CareerSlot>();
                    var anyChanged = false;
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

                        if (slot.IsOccupied && _owner.ApplyCouponItemPackagesToCareerNoLock(identity, slot))
                        {
                            var updated = slot.ToDictionary();
                            foreach (DictionaryEntry entry in updated)
                            {
                                dict[entry.Key] = entry.Value;
                            }
                            anyChanged = true;
                        }

                        results.Add(slot);
                    }

                    if (anyChanged)
                    {
                        _owner.SaveAccountNoThrow(account);
                    }

                    results.Sort(delegate (CareerSlot a, CareerSlot b) { return a.Index.CompareTo(b.Index); });
                    return results;
                }
            }

            public CareerSlot GetOrCreateCareer(string identityHash, int index, bool markOccupied)
            {
                lock (_owner._lock)
                {
                    var identity = LocalUserStore.IsGuidish(identityHash) ? LocalUserStore.NormalizeGuidish(identityHash) : _owner.GetOrCreateIdentityHash();
                    var account = _owner.LoadAccountForIdentityNoThrow(identity, true) ?? _owner.LoadAccountNoThrow();
                    var careersList = GetOrCreateCareerListNoLock(account, identity, false);

                    IDictionary found = null;
                    for (var i = 0; i < careersList.Count; i++)
                    {
                        var dict = careersList[i] as IDictionary;
                        if (dict == null)
                        {
                            continue;
                        }

                        var idx = LocalUserStore.GetInt(dict, "Index", -1);
                        if (idx == index)
                        {
                            found = dict;
                            break;
                        }
                    }

                    if (found == null)
                    {
                        var newSlot = CreateEmptySlot(identity, index);
                        careersList.Add(newSlot.ToDictionary());
                        found = (IDictionary)careersList[careersList.Count - 1];
                    }

                    var slotObj = CareerSlot.FromDictionary(found) ?? CreateEmptySlot(identity, index);

                    if (markOccupied)
                    {
                        var becameOccupied = !slotObj.IsOccupied;
                        if (!slotObj.IsOccupied)
                        {
                            slotObj.IsOccupied = true;
                            if (LocalUserStore.IsNullOrWhiteSpace(slotObj.CharacterName))
                            {
                                slotObj.CharacterName = "NewRunner";
                            }

                            slotObj.SkinTextureIndex = PlayerCharacterDefaultValues.SkinTextureIndex;
                            slotObj.BackgroundStory = PlayerCharacterDefaultValues.BackgroundStory;

                            if (LocalUserStore.IsNullOrWhiteSpace(slotObj.Portrait) && LocalUserStore.IsNullOrWhiteSpace(slotObj.PortraitPath))
                            {
                                slotObj.PortraitPath = PlayerCharacterDefaultValues.PortraitPath;
                                slotObj.Portrait = slotObj.PortraitPath;
                            }
                            slotObj.PendingPersistenceCreation = true;

                            if (_owner._careerSeedService != null)
                            {
                                _owner._careerSeedService.ApplyNewCareerSeeds(slotObj);
                            }

                            slotObj.Nuyen = 0;
                            slotObj.Karma = 0;
                            slotObj.SpentKarma = 0;
                        }

                        if (slotObj.IsOccupied && _owner._careerSeedService != null)
                        {
                            _owner._careerSeedService.ApplyOccupiedCareerBackfills(slotObj);
                        }

                        if (becameOccupied)
                        {
                            var entitled = _owner.GetCouponItemPackageEntitlementsForIdentityNoLock(identity);
                            _owner.EnsureAllCouponEntitlementItemsPresentNoLock(slotObj, entitled);
                        }
                    }

                    if (slotObj.IsOccupied && _owner._careerSeedService != null)
                    {
                        _owner._careerSeedService.ApplyOccupiedCareerBackfills(slotObj);
                    }

                    _owner.ApplyCouponItemPackagesToCareerNoLock(identity, slotObj);
                    slotObj.CharacterIdentifier = LocalUserStore.NormalizeGuidish(identity) + ":" + index.ToString();

                    var updatedDict = slotObj.ToDictionary();
                    foreach (DictionaryEntry entry in updatedDict)
                    {
                        found[entry.Key] = entry.Value;
                    }
                    _owner.SaveAccountNoThrow(account);

                    return slotObj;
                }
            }

            public void UpsertCareer(string identityHash, CareerSlot slot)
            {
                if (slot == null)
                {
                    return;
                }

                if (!LocalUserStore.IsGuidish(identityHash))
                {
                    try
                    {
                        if (_owner._logger != null)
                        {
                            _owner._logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "persistence",
                                op = "upsert-career-rejected",
                                reason = "invalid-identity-hash",
                                identityHash = identityHash,
                                careerIndex = slot.Index,
                                characterIdentifier = slot.CharacterIdentifier,
                                characterName = slot.CharacterName,
                            });
                        }
                    }
                    catch
                    {
                    }
                    return;
                }

                lock (_owner._lock)
                {
                    var identity = LocalUserStore.NormalizeGuidish(identityHash);

                    if (!LocalUserStore.IsNullOrWhiteSpace(slot.CharacterIdentifier))
                    {
                        try
                        {
                            var raw = slot.CharacterIdentifier.Trim();
                            var colon = raw.IndexOf(':');
                            if (colon > 0)
                            {
                                var guidPart = raw.Substring(0, colon);
                                if (LocalUserStore.IsGuidish(guidPart))
                                {
                                    var normalizedGuidPart = LocalUserStore.NormalizeGuidish(guidPart);
                                    if (!string.Equals(normalizedGuidPart, identity, StringComparison.OrdinalIgnoreCase))
                                    {
                                        try
                                        {
                                            if (_owner._logger != null)
                                            {
                                                _owner._logger.Log(new
                                                {
                                                    ts = RequestLogger.UtcNowIso(),
                                                    type = "persistence",
                                                    op = "upsert-career-rejected",
                                                    reason = "identity-mismatch",
                                                    identityHash = identity,
                                                    slotIdentity = normalizedGuidPart,
                                                    careerIndex = slot.Index,
                                                    characterIdentifier = slot.CharacterIdentifier,
                                                    characterName = slot.CharacterName,
                                                });
                                            }
                                        }
                                        catch
                                        {
                                        }
                                        return;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                    var account = _owner.LoadAccountForIdentityNoThrow(identity, true) ?? _owner.LoadAccountNoThrow();
                    var careersList = GetOrCreateCareerListNoLock(account, identity, false);

                    IDictionary found = null;
                    CareerSlot existingSlot = null;
                    for (var i = 0; i < careersList.Count; i++)
                    {
                        var dict = careersList[i] as IDictionary;
                        if (dict == null)
                        {
                            continue;
                        }

                        var idx = LocalUserStore.GetInt(dict, "Index", -1);
                        if (idx == slot.Index)
                        {
                            found = dict;
                            existingSlot = CareerSlot.FromDictionary(dict);
                            break;
                        }
                    }

                    slot.CharacterIdentifier = LocalUserStore.NormalizeGuidish(identity) + ":" + slot.Index.ToString(CultureInfo.InvariantCulture);

                    if (slot.IsOccupied)
                    {
                        _owner.ApplyCouponItemPackagesToCareerNoLock(identity, slot);

                        // One-time creation hardening: if the server just created this career,
                        // a stale client SetPlayerInfo can arrive without the initial coupon items.
                        var isNewlyOccupied = existingSlot == null || !existingSlot.IsOccupied;
                        var isPendingCreationSync = existingSlot != null && existingSlot.IsOccupied && existingSlot.PendingPersistenceCreation;
                        if (isNewlyOccupied || isPendingCreationSync)
                        {
                            var entitled = _owner.GetCouponItemPackageEntitlementsForIdentityNoLock(identity);
                            _owner.EnsureAllCouponEntitlementItemsPresentNoLock(slot, entitled);
                        }
                    }

                    if (found == null)
                    {
                        careersList.Add(slot.ToDictionary());
                        _owner.SaveAccountNoThrow(account);
                        return;
                    }

                    var updated = slot.ToDictionary();
                    foreach (DictionaryEntry entry in updated)
                    {
                        found[entry.Key] = entry.Value;
                    }
                    _owner.SaveAccountNoThrow(account);
                }
            }

            public CareerSlot DeactivateCareerSlot(string identityHash, int index, string hubId)
            {
                if (index < 0)
                {
                    index = 0;
                }

                lock (_owner._lock)
                {
                    var identity = LocalUserStore.IsGuidish(identityHash) ? LocalUserStore.NormalizeGuidish(identityHash) : _owner.GetOrCreateIdentityHash();
                    var account = _owner.LoadAccountForIdentityNoThrow(identity, true) ?? _owner.LoadAccountNoThrow();
                    var careersList = GetOrCreateCareerListNoLock(account, identity, false);

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

                    target.Voiceset = string.Empty;
                    target.WantsBackgroundChange = false;
                    target.PrimaryWeaponItemId = string.Empty;
                    target.PrimaryWeaponInventoryKey = 0;
                    target.PrimaryWeaponQuality = 0;
                    target.PrimaryWeaponFlavour = -1;
                    target.SecondaryWeaponItemId = string.Empty;
                    target.SecondaryWeaponInventoryKey = 1;
                    target.SecondaryWeaponQuality = 0;
                    target.SecondaryWeaponFlavour = -1;
                    target.ArmorItemId = string.Empty;
                    target.ArmorInventoryKey = 2;
                    target.ArmorQuality = 0;
                    target.ArmorFlavour = -1;
                    target.EquippedItems = new Dictionary<string, CareerSlot.EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
                    target.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    target.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                    target.Karma = 0;
                    target.SpentKarma = 0;
                    target.Nuyen = 0;
                    target.MainCampaignCurrentChapter = 0;
                    target.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    target.MainCampaignInteractedNpcs = new List<string>();
                    target.ActiveUnlocks = new List<string>();
                    target.Bodytype = 0UL;
                    target.SkinTextureIndex = 0;
                    target.BackgroundStory = 0UL;

                    target.IsOccupied = false;
                    target.CharacterName = string.Empty;
                    target.Portrait = string.Empty;
                    target.PortraitPath = string.Empty;
                    target.PendingPersistenceCreation = false;
                    target.HubId = LocalUserStore.IsNullOrWhiteSpace(hubId) ? "Act01_HUB_02" : hubId;
                    target.CharacterIdentifier = LocalUserStore.NormalizeGuidish(identity) + ":" + index.ToString();
                    byIndex[index] = target;

                    var rebuilt = new ArrayList();
                    foreach (var kvp in byIndex)
                    {
                        if (kvp.Value == null)
                        {
                            continue;
                        }

                        kvp.Value.CharacterIdentifier = LocalUserStore.NormalizeGuidish(identity) + ":" + kvp.Value.Index.ToString();
                        rebuilt.Add(kvp.Value.ToDictionary());
                    }

                    account["Careers"] = rebuilt;
                    _owner.SaveAccountNoThrow(account);

                    return target;
                }
            }

            private ArrayList GetOrCreateCareerListNoLock(IDictionary account, string identity, bool saveWhenCreated)
            {
                var careersObj = account != null ? account["Careers"] : null;
                var careersList = LocalUserStore.CoerceToArrayList(careersObj);
                var changed = false;
                if (careersList == null)
                {
                    careersList = LocalUserStore.BuildDefaultCareers(identity);
                    account["Careers"] = careersList;
                    changed = true;
                }
                else if (!(careersObj is ArrayList))
                {
                    account["Careers"] = careersList;
                    changed = true;
                }

                if (changed && saveWhenCreated)
                {
                    _owner.SaveAccountNoThrow(account);
                }

                return careersList;
            }

            private static CareerSlot CreateEmptySlot(string identity, int index)
            {
                var slot = new CareerSlot();
                slot.Index = index;
                slot.IsOccupied = false;
                slot.CharacterName = string.Empty;
                slot.Portrait = string.Empty;
                slot.PrimaryWeaponQuality = 0;
                slot.PrimaryWeaponFlavour = -1;
                slot.SecondaryWeaponQuality = 0;
                slot.SecondaryWeaponFlavour = -1;
                slot.ArmorQuality = 0;
                slot.ArmorFlavour = -1;
                slot.HubId = "Act01_HUB_02";
                slot.PendingPersistenceCreation = false;
                slot.CharacterIdentifier = LocalUserStore.NormalizeGuidish(identity) + ":" + index.ToString();
                return slot;
            }
        }
    }
}
