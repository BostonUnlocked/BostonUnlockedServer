# Loot Drop Tables Reference

Generated from `server/static-data/serverData.json`, `server/static-data/metagameplay.json`, and `server/StreamingAssets/levels/*/mapdata.json`.

## Assumptions

- Each item appears once, in exactly one item-type table.
- `SourceLootTables` are mapdata-linked loot roots where that item is present after recursive flattening.
- `SoldInShop` and `ShopPrice` are derived from `ShopListDefinitions` in `metagameplay.json`.
- This reference is membership-based (drop availability), not weighted chance percentages.

## Coverage

- Levels with loot roots: 55
- Root loot tables used: 13
- Unique items in mission loot: 475
- Loot table definitions parsed: 40

## Items By Type

### Automatics

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_AresExecutiveProtector_Tier_04](assets/item-icons/icon_small_AresExecutiveProtector_Tier_04.png) | Automatics_AresExecutiveProtector_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22, 17800 |
| ![icon_small_AresExecutiveProtector_Tier_05](assets/item-icons/icon_small_AresExecutiveProtector_Tier_05.png) | Automatics_AresExecutiveProtector_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22, 19800 |
| ![icon_small_FnP93Praetor_Tier_01](assets/item-icons/icon_small_FnP93Praetor_Tier_01.png) | Automatics_FnP93Praetor_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9400 |
| ![icon_small_FnP93Praetor_Tier_02](assets/item-icons/icon_small_FnP93Praetor_Tier_02.png) | Automatics_FnP93Praetor_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 123, 16800 |
| ![icon_small_FnP93Praetor_Tier_03](assets/item-icons/icon_small_FnP93Praetor_Tier_03.png) | Automatics_FnP93Praetor_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 16400 |
| ![icon_small_FnP93Praetor_Tier_04](assets/item-icons/icon_small_FnP93Praetor_Tier_04.png) | Automatics_FnP93Praetor_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22200 |
| ![icon_small_FnP93Praetor_Tier_05](assets/item-icons/icon_small_FnP93Praetor_Tier_05.png) | Automatics_FnP93Praetor_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24700 |
| ![icon_small_FnP93Praetor_Tier_08](assets/item-icons/icon_small_FnP93Praetor_Tier_08.png) | Automatics_FnP93Praetor_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 49600 |
| ![icon_small_FnP93Praetor_Tier_09](assets/item-icons/icon_small_FnP93Praetor_Tier_09.png) | Automatics_FnP93Praetor_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_FnP93Praetor_Tier_10](assets/item-icons/icon_small_FnP93Praetor_Tier_10.png) | Automatics_FnP93Praetor_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 248000 |
| ![icon_small_HkG38_Tier_04](assets/item-icons/icon_small_HkG38_Tier_04.png) | Automatics_HkG38_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22200 |
| ![icon_small_HkG38_Tier_05](assets/item-icons/icon_small_HkG38_Tier_05.png) | Automatics_HkG38_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24700 |
| ![icon_small_IngramSmartgun_Tier_01](assets/item-icons/icon_small_IngramSmartgun_Tier_01.png) | Automatics_IngramSmartgun_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 6900 |
| ![icon_small_IngramSmartgun_Tier_02](assets/item-icons/icon_small_IngramSmartgun_Tier_02.png) | Automatics_IngramSmartgun_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 12500 |
| ![icon_small_IngramSmartgun_Tier_03](assets/item-icons/icon_small_IngramSmartgun_Tier_03.png) | Automatics_IngramSmartgun_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 13100 |
| ![icon_small_IngramSmartgun_Tier_08](assets/item-icons/icon_small_IngramSmartgun_Tier_08.png) | Automatics_IngramSmartgun_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 39700 |
| ![icon_small_IngramSmartgun_Tier_09](assets/item-icons/icon_small_IngramSmartgun_Tier_09.png) | Automatics_IngramSmartgun_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_IngramSmartgun_Tier_10](assets/item-icons/icon_small_IngramSmartgun_Tier_10.png) | Automatics_IngramSmartgun_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 198400 |
| ![icon_small_Mag5Mg_Tier_04](assets/item-icons/icon_small_Mag5Mg_Tier_04.png) | Automatics_Mag5Mg_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 26600 |
| ![icon_small_Mag5Mg_Tier_05](assets/item-icons/icon_small_Mag5Mg_Tier_05.png) | Automatics_Mag5Mg_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 29700 |
| ![icon_small_Automatics_ModSkua_Blue_Tier_07](assets/item-icons/icon_small_Automatics_ModSkua_Blue_Tier_07.png) | Automatics_ModSkua_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Automatics_ModSkua_Tier_07](assets/item-icons/icon_small_Automatics_ModSkua_Tier_07.png) | Automatics_ModSkua_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_SkuaDmr_Tier_05](assets/item-icons/icon_small_SkuaDmr_Tier_05.png) | Automatics_SkuaDmr_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 31200 |
| ![icon_small_SkuaDmr_Tier_08](assets/item-icons/icon_small_SkuaDmr_Tier_08.png) | Automatics_SkuaDmr_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 59500 |
| ![icon_small_SkuaDmr_Tier_09](assets/item-icons/icon_small_SkuaDmr_Tier_09.png) | Automatics_SkuaDmr_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_SkuaDmr_Tier_10](assets/item-icons/icon_small_SkuaDmr_Tier_10.png) | Automatics_SkuaDmr_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 297600 |

### Blade

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Cleaver_Tier_01](assets/item-icons/icon_small_Cleaver_Tier_01.png) | Blade_Cleaver_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 5100 |
| ![icon_small_Cleaver_Tier_02](assets/item-icons/icon_small_Cleaver_Tier_02.png) | Blade_Cleaver_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9200 |
| ![icon_small_Cleaver_Tier_08](assets/item-icons/icon_small_Cleaver_Tier_08.png) | Blade_Cleaver_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 43900 |
| ![icon_small_Cleaver_Tier_09](assets/item-icons/icon_small_Cleaver_Tier_09.png) | Blade_Cleaver_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_Cleaver_Tier_10](assets/item-icons/icon_small_Cleaver_Tier_10.png) | Blade_Cleaver_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 219500 |
| ![icon_small_Katana_Tier_01](assets/item-icons/icon_small_Katana_Tier_01.png) | Blade_Katana_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 6900 |
| ![icon_small_Katana_Tier_02](assets/item-icons/icon_small_Katana_Tier_02.png) | Blade_Katana_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 11500 |
| ![icon_small_Katana_Tier_03](assets/item-icons/icon_small_Katana_Tier_03.png) | Blade_Katana_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 13100 |
| ![icon_small_Katana_Tier_04](assets/item-icons/icon_small_Katana_Tier_04.png) | Blade_Katana_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17700 |
| ![icon_small_Katana_Tier_05](assets/item-icons/icon_small_Katana_Tier_05.png) | Blade_Katana_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 19700 |
| ![icon_small_Blade_Laser_Katana_Blue_Tier_07](assets/item-icons/icon_small_Blade_Laser_Katana_Blue_Tier_07.png) | Blade_Laser_Katana_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Blade_Laser_Katana_Tier_07](assets/item-icons/icon_small_Blade_Laser_Katana_Tier_07.png) | Blade_Laser_Katana_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_Macuahuitl_Tier_02](assets/item-icons/icon_small_Macuahuitl_Tier_02.png) | Blade_Macuahuitl_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9200 |
| ![icon_small_Macuahuitl_Tier_03](assets/item-icons/icon_small_Macuahuitl_Tier_03.png) | Blade_Macuahuitl_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9700 |
| ![icon_small_Macuahuitl_Tier_04](assets/item-icons/icon_small_Macuahuitl_Tier_04.png) | Blade_Macuahuitl_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 13100 |
| ![icon_small_Macuahuitl_Tier_05](assets/item-icons/icon_small_Macuahuitl_Tier_05.png) | Blade_Macuahuitl_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14600 |
| ![icon_small_Macuahuitl_Tier_08](assets/item-icons/icon_small_Macuahuitl_Tier_08.png) | Blade_Macuahuitl_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 29300 |
| ![icon_small_Macuahuitl_Tier_09](assets/item-icons/icon_small_Macuahuitl_Tier_09.png) | Blade_Macuahuitl_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_Macuahuitl_Tier_10](assets/item-icons/icon_small_Macuahuitl_Tier_10.png) | Blade_Macuahuitl_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 146300 |
| ![icon_small_VibroBlade_Tier_04](assets/item-icons/icon_small_VibroBlade_Tier_04.png) | Blade_VibroBlade_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 19600 |
| ![icon_small_VibroBlade_Tier_05](assets/item-icons/icon_small_VibroBlade_Tier_05.png) | Blade_VibroBlade_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 21900 |
| ![icon_small_VibroBlade_Tier_08](assets/item-icons/icon_small_VibroBlade_Tier_08.png) | Blade_VibroBlade_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 43900 |
| ![icon_small_VibroBlade_Tier_09](assets/item-icons/icon_small_VibroBlade_Tier_09.png) | Blade_VibroBlade_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_VibroBlade_Tier_10](assets/item-icons/icon_small_VibroBlade_Tier_10.png) | Blade_VibroBlade_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 219500 |

### Club

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_FoldingSpade_Tier_01](assets/item-icons/icon_small_FoldingSpade_Tier_01.png) | Club_FoldingSpade_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 6000 |
| ![icon_small_FoldingSpade_Tier_02](assets/item-icons/icon_small_FoldingSpade_Tier_02.png) | Club_FoldingSpade_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 10700 |
| ![icon_small_FoldingSpade_Tier_03](assets/item-icons/icon_small_FoldingSpade_Tier_03.png) | Club_FoldingSpade_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 11300 |
| ![icon_small_FoldingSpade_Tier_04](assets/item-icons/icon_small_FoldingSpade_Tier_04.png) | Club_FoldingSpade_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15300 |
| ![icon_small_FoldingSpade_Tier_05](assets/item-icons/icon_small_FoldingSpade_Tier_05.png) | Club_FoldingSpade_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17000 |
| ![icon_small_NailBoard_Tier_01](assets/item-icons/icon_small_NailBoard_Tier_01.png) | Club_NailBoard_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 4400 |
| ![icon_small_NailBoard_Tier_02](assets/item-icons/icon_small_NailBoard_Tier_02.png) | Club_NailBoard_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 8000 |
| ![icon_small_NailBoard_Tier_08](assets/item-icons/icon_small_NailBoard_Tier_08.png) | Club_NailBoard_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 31600 |
| ![icon_small_NailBoard_Tier_09](assets/item-icons/icon_small_NailBoard_Tier_09.png) | Club_NailBoard_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_NailBoard_Tier_10](assets/item-icons/icon_small_NailBoard_Tier_10.png) | Club_NailBoard_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 158100 |
| ![icon_small_ShockMace_Tier_02](assets/item-icons/icon_small_ShockMace_Tier_02.png) | Club_ShockMace_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 11900 |
| ![icon_small_ShockMace_Tier_03](assets/item-icons/icon_small_ShockMace_Tier_03.png) | Club_ShockMace_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 12500 |
| ![icon_small_ShockMace_Tier_04](assets/item-icons/icon_small_ShockMace_Tier_04.png) | Club_ShockMace_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17000 |
| ![icon_small_ShockMace_Tier_05](assets/item-icons/icon_small_ShockMace_Tier_05.png) | Club_ShockMace_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 18900 |
| ![icon_small_ShockMace_Tier_08](assets/item-icons/icon_small_ShockMace_Tier_08.png) | Club_ShockMace_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 37900 |
| ![icon_small_ShockMace_Tier_09](assets/item-icons/icon_small_ShockMace_Tier_09.png) | Club_ShockMace_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_ShockMace_Tier_10](assets/item-icons/icon_small_ShockMace_Tier_10.png) | Club_ShockMace_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 189700 |
| ![icon_small_Club_Sledgehammer_Blue_Tier_07](assets/item-icons/icon_small_Club_Sledgehammer_Blue_Tier_07.png) | Club_Sledgehammer_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Club_Sledgehammer_Tier_07](assets/item-icons/icon_small_Club_Sledgehammer_Tier_07.png) | Club_Sledgehammer_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_Tonfa_Tier_04](assets/item-icons/icon_small_Tonfa_Tier_04.png) | Club_Tonfa_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 11300 |
| ![icon_small_Tonfa_Tier_05](assets/item-icons/icon_small_Tonfa_Tier_05.png) | Club_Tonfa_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 12600 |
| ![icon_small_Tonfa_Tier_08](assets/item-icons/icon_small_Tonfa_Tier_08.png) | Club_Tonfa_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 25300 |
| ![icon_small_Tonfa_Tier_09](assets/item-icons/icon_small_Tonfa_Tier_09.png) | Club_Tonfa_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_Tonfa_Tier_10](assets/item-icons/icon_small_Tonfa_Tier_10.png) | Club_Tonfa_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 126500 |

### Conjuring

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_CombatFocus_Tier_01](assets/item-icons/icon_small_CombatFocus_Tier_01.png) | Conjuring_CombatFocus_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 7800 |
| ![icon_small_CombatFocus_Tier_03](assets/item-icons/icon_small_CombatFocus_Tier_03.png) | Conjuring_CombatFocus_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14800 |
| ![icon_small_CombatFocus_Tier_04](assets/item-icons/icon_small_CombatFocus_Tier_04.png) | Conjuring_CombatFocus_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20000 |
| ![icon_small_CombatFocus_Tier_05](assets/item-icons/icon_small_CombatFocus_Tier_05.png) | Conjuring_CombatFocus_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22300 |
| ![icon_small_CombatFocus_Tier_06](assets/item-icons/icon_small_CombatFocus_Tier_06.png) | Conjuring_CombatFocus_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23400 |
| ![icon_small_CombatFocus_Tier_08](assets/item-icons/icon_small_CombatFocus_Tier_08.png) | Conjuring_CombatFocus_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 44600 |
| ![icon_small_CombatFocus_Tier_09](assets/item-icons/icon_small_CombatFocus_Tier_09.png) | Conjuring_CombatFocus_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_CombatFocus_Tier_10](assets/item-icons/icon_small_CombatFocus_Tier_10.png) | Conjuring_CombatFocus_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 223200 |
| ![icon_small_ConjuringFocus_Tier_01](assets/item-icons/icon_small_ConjuringFocus_Tier_01.png) | Conjuring_ConjuringFocus_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 7800 |
| ![icon_small_ConjuringFocus_Tier_02](assets/item-icons/icon_small_ConjuringFocus_Tier_02.png) | Conjuring_ConjuringFocus_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14000 |
| ![icon_small_ConjuringFocus_Tier_03](assets/item-icons/icon_small_ConjuringFocus_Tier_03.png) | Conjuring_ConjuringFocus_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14800 |
| ![icon_small_ConjuringFocus_Tier_04](assets/item-icons/icon_small_ConjuringFocus_Tier_04.png) | Conjuring_ConjuringFocus_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20000 |
| ![icon_small_ConjuringFocus_Tier_05](assets/item-icons/icon_small_ConjuringFocus_Tier_05.png) | Conjuring_ConjuringFocus_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22300 |
| ![icon_small_ConjuringFocus_Tier_06](assets/item-icons/icon_small_ConjuringFocus_Tier_06.png) | Conjuring_ConjuringFocus_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23400 |
| ![icon_small_ConjuringFocus_Tier_08](assets/item-icons/icon_small_ConjuringFocus_Tier_08.png) | Conjuring_ConjuringFocus_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 67000 |
| ![icon_small_ConjuringFocus_Tier_09](assets/item-icons/icon_small_ConjuringFocus_Tier_09.png) | Conjuring_ConjuringFocus_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_ConjuringFocus_Tier_10](assets/item-icons/icon_small_ConjuringFocus_Tier_10.png) | Conjuring_ConjuringFocus_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 334800 |
| ![icon_small_Conjuring_Summoning_Focus_Blue_Tier_07](assets/item-icons/icon_small_Conjuring_Summoning_Focus_Blue_Tier_07.png) | Conjuring_Summoning_Focus_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Conjuring_Summoning_Focus_Tier_07](assets/item-icons/icon_small_Conjuring_Summoning_Focus_Tier_07.png) | Conjuring_Summoning_Focus_Tier_07 | Challenge02Phase03LootTable | No |  |

### Hacking

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Aztecha200_Tier_01](assets/item-icons/icon_small_Aztecha200_Tier_01.png) | Hacking_Aztecha200_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 10300 |
| ![icon_small_Aztecha200_Tier_03](assets/item-icons/icon_small_Aztecha200_Tier_03.png) | Hacking_Aztecha200_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15600 |
| ![icon_small_Aztecha200_Tier_04](assets/item-icons/icon_small_Aztecha200_Tier_04.png) | Hacking_Aztecha200_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 21100 |
| ![icon_small_Aztecha200_Tier_05](assets/item-icons/icon_small_Aztecha200_Tier_05.png) | Hacking_Aztecha200_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23500 |
| ![icon_small_Aztecha200_Tier_06](assets/item-icons/icon_small_Aztecha200_Tier_06.png) | Hacking_Aztecha200_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24700 |
| ![icon_small_Aztecha200_Tier_08](assets/item-icons/icon_small_Aztecha200_Tier_08.png) | Hacking_Aztecha200_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 47100 |
| ![icon_small_Aztecha200_Tier_09](assets/item-icons/icon_small_Aztecha200_Tier_09.png) | Hacking_Aztecha200_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_Aztecha200_Tier_10](assets/item-icons/icon_small_Aztecha200_Tier_10.png) | Hacking_Aztecha200_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 235600 |
| ![icon_small_Mcd1Deck_Tier_01](assets/item-icons/icon_small_Mcd1Deck_Tier_01.png) | Hacking_Mcd1Deck_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 8200 |
| ![icon_small_Mcd1Deck_Tier_02](assets/item-icons/icon_small_Mcd1Deck_Tier_02.png) | Hacking_Mcd1Deck_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14800 |
| ![icon_small_Mcd1Deck_Tier_03](assets/item-icons/icon_small_Mcd1Deck_Tier_03.png) | Hacking_Mcd1Deck_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15600 |
| ![icon_small_Mcd1Deck_Tier_04](assets/item-icons/icon_small_Mcd1Deck_Tier_04.png) | Hacking_Mcd1Deck_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 21100 |
| ![icon_small_Mcd1Deck_Tier_05](assets/item-icons/icon_small_Mcd1Deck_Tier_05.png) | Hacking_Mcd1Deck_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23500 |
| ![icon_small_Mcd1Deck_Tier_06](assets/item-icons/icon_small_Mcd1Deck_Tier_06.png) | Hacking_Mcd1Deck_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24700 |
| ![icon_small_MicrodeckSummit_Tier_03](assets/item-icons/icon_small_MicrodeckSummit_Tier_03.png) | Hacking_MicrodeckSummit_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 19500 |
| ![icon_small_MicrodeckSummit_Tier_04](assets/item-icons/icon_small_MicrodeckSummit_Tier_04.png) | Hacking_MicrodeckSummit_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 26400 |
| ![icon_small_MicrodeckSummit_Tier_05](assets/item-icons/icon_small_MicrodeckSummit_Tier_05.png) | Hacking_MicrodeckSummit_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 29400 |
| ![icon_small_MicrodeckSummit_Tier_06](assets/item-icons/icon_small_MicrodeckSummit_Tier_06.png) | Hacking_MicrodeckSummit_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 30900 |
| ![icon_small_MicrodeckSummit_Tier_08](assets/item-icons/icon_small_MicrodeckSummit_Tier_08.png) | Hacking_MicrodeckSummit_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 58900 |
| ![icon_small_MicrodeckSummit_Tier_09](assets/item-icons/icon_small_MicrodeckSummit_Tier_09.png) | Hacking_MicrodeckSummit_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_MicrodeckSummit_Tier_10](assets/item-icons/icon_small_MicrodeckSummit_Tier_10.png) | Hacking_MicrodeckSummit_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 294500 |
| ![icon_small_Hacking_Renraku_Tsurugi_Blue_Tier_07](assets/item-icons/icon_small_Hacking_Renraku_Tsurugi_Blue_Tier_07.png) | Hacking_Renraku_Tsurugi_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Hacking_Renraku_Tsurugi_Tier_07](assets/item-icons/icon_small_Hacking_Renraku_Tsurugi_Tier_07.png) | Hacking_Renraku_Tsurugi_Tier_07 | Challenge02Phase03LootTable | No |  |

### Pistol

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_AresLightfire_Tier_01](assets/item-icons/icon_small_AresLightfire_Tier_01.png) | Pistol_AresLightfire_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 5500 |
| ![icon_small_AresLightfire_Tier_02](assets/item-icons/icon_small_AresLightfire_Tier_02.png) | Pistol_AresLightfire_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9800 |
| ![icon_small_AresLightfire_Tier_03](assets/item-icons/icon_small_AresLightfire_Tier_03.png) | Pistol_AresLightfire_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 12900 |
| ![icon_small_AresLightfire_Tier_08](assets/item-icons/icon_small_AresLightfire_Tier_08.png) | Pistol_AresLightfire_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 39100 |
| ![icon_small_AresLightfire_Tier_09](assets/item-icons/icon_small_AresLightfire_Tier_09.png) | Pistol_AresLightfire_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_AresLightfire_Tier_10](assets/item-icons/icon_small_AresLightfire_Tier_10.png) | Pistol_AresLightfire_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 195300 |
| ![icon_small_AresPredator_Tier_01](assets/item-icons/icon_small_AresPredator_Tier_01.png) | Pistol_AresPredator_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 6800 |
| ![icon_small_AresPredator_Tier_03](assets/item-icons/icon_small_AresPredator_Tier_03.png) | Pistol_AresPredator_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 10300 |
| ![icon_small_AresPredator_Tier_05](assets/item-icons/icon_small_AresPredator_Tier_05.png) | Pistol_AresPredator_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15600 |
| ![icon_small_AresPredator_Tier_06](assets/item-icons/icon_small_AresPredator_Tier_06.png) | Pistol_AresPredator_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 16400 |
| ![icon_small_AresPredator_Tier_08](assets/item-icons/icon_small_AresPredator_Tier_08.png) | Pistol_AresPredator_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 31200 |
| ![icon_small_AresPredator_Tier_09](assets/item-icons/icon_small_AresPredator_Tier_09.png) | Pistol_AresPredator_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_AresPredator_Tier_10](assets/item-icons/icon_small_AresPredator_Tier_10.png) | Pistol_AresPredator_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 156200 |
| ![icon_small_CavalierDeputy_Tier_05](assets/item-icons/icon_small_CavalierDeputy_Tier_05.png) | Pistol_CavalierDeputy_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 19500 |
| ![icon_small_CavalierDeputy_Tier_06](assets/item-icons/icon_small_CavalierDeputy_Tier_06.png) | Pistol_CavalierDeputy_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20500 |
| ![icon_small_Haemmerli620s_Tier_04](assets/item-icons/icon_small_Haemmerli620s_Tier_04.png) | Pistol_Haemmerli620s_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17500 |
| ![icon_small_Pistol_Mod_Haemmerli_Blue_Tier_07](assets/item-icons/icon_small_Pistol_Mod_Haemmerli_Blue_Tier_07.png) | Pistol_Mod_Haemmerli_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Pistol_Mod_Haemmerli_Tier_07](assets/item-icons/icon_small_Pistol_Mod_Haemmerli_Tier_07.png) | Pistol_Mod_Haemmerli_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_SkSpraydown_Tier_04](assets/item-icons/icon_small_SkSpraydown_Tier_04.png) | Pistol_SkSpraydown_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14000 |
| ![icon_small_SuperWarhawk_Tier_02](assets/item-icons/icon_small_SuperWarhawk_Tier_02.png) | Pistol_SuperWarhawk_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14700 |
| ![icon_small_SuperWarhawk_Tier_03](assets/item-icons/icon_small_SuperWarhawk_Tier_03.png) | Pistol_SuperWarhawk_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15500 |
| ![icon_small_SuperWarhawk_Tier_05](assets/item-icons/icon_small_SuperWarhawk_Tier_05.png) | Pistol_SuperWarhawk_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23400 |
| ![icon_small_SuperWarhawk_Tier_06](assets/item-icons/icon_small_SuperWarhawk_Tier_06.png) | Pistol_SuperWarhawk_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24600 |
| ![icon_small_SuperWarhawk_Tier_08](assets/item-icons/icon_small_SuperWarhawk_Tier_08.png) | Pistol_SuperWarhawk_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 46900 |
| ![icon_small_SuperWarhawk_Tier_09](assets/item-icons/icon_small_SuperWarhawk_Tier_09.png) | Pistol_SuperWarhawk_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_SuperWarhawk_Tier_10](assets/item-icons/icon_small_SuperWarhawk_Tier_10.png) | Pistol_SuperWarhawk_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 234400 |

### Rigging

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_ControlRigInterface_Tier_01](assets/item-icons/icon_small_ControlRigInterface_Tier_01.png) | Rigging_ControlRigInterface_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 10000 |
| ![icon_small_ControlRigInterface_Tier_02](assets/item-icons/icon_small_ControlRigInterface_Tier_02.png) | Rigging_ControlRigInterface_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17900 |
| ![icon_small_ControlRigInterface_Tier_03](assets/item-icons/icon_small_ControlRigInterface_Tier_03.png) | Rigging_ControlRigInterface_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15100 |
| ![icon_small_ControlRigInterface_Tier_04](assets/item-icons/icon_small_ControlRigInterface_Tier_04.png) | Rigging_ControlRigInterface_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20400 |
| ![icon_small_ControlRigInterface_Tier_05](assets/item-icons/icon_small_ControlRigInterface_Tier_05.png) | Rigging_ControlRigInterface_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 28400 |
| ![icon_small_ControlRigInterface_Tier_06](assets/item-icons/icon_small_ControlRigInterface_Tier_06.png) | Rigging_ControlRigInterface_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23900 |
| ![icon_small_ControlRigInterface_Tier_08](assets/item-icons/icon_small_ControlRigInterface_Tier_08.png) | Rigging_ControlRigInterface_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 57000 |
| ![icon_small_ControlRigInterface_Tier_09](assets/item-icons/icon_small_ControlRigInterface_Tier_09.png) | Rigging_ControlRigInterface_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_ControlRigInterface_Tier_10](assets/item-icons/icon_small_ControlRigInterface_Tier_10.png) | Rigging_ControlRigInterface_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 285200 |
| ![icon_small_Rigging_MCT_Drone_Web_Blue_Tier_07](assets/item-icons/icon_small_Rigging_MCT_Drone_Web_Blue_Tier_07.png) | Rigging_MCT_Drone_Web_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Rigging_MCT_Drone_Web_Tier_07](assets/item-icons/icon_small_Rigging_MCT_Drone_Web_Tier_07.png) | Rigging_MCT_Drone_Web_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_ModdedRigInterface_Tier_01](assets/item-icons/icon_small_ModdedRigInterface_Tier_01.png) | Rigging_ModdedRigInterface_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14400 |
| ![icon_small_ModdedRigInterface_Tier_03](assets/item-icons/icon_small_ModdedRigInterface_Tier_03.png) | Rigging_ModdedRigInterface_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15100 |
| ![icon_small_ModdedRigInterface_Tier_04](assets/item-icons/icon_small_ModdedRigInterface_Tier_04.png) | Rigging_ModdedRigInterface_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20400 |
| ![icon_small_ModdedRigInterface_Tier_05](assets/item-icons/icon_small_ModdedRigInterface_Tier_05.png) | Rigging_ModdedRigInterface_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22800 |
| ![icon_small_ModdedRigInterface_Tier_06](assets/item-icons/icon_small_ModdedRigInterface_Tier_06.png) | Rigging_ModdedRigInterface_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23900 |
| ![icon_small_ModdedRigInterface_Tier_08](assets/item-icons/icon_small_ModdedRigInterface_Tier_08.png) | Rigging_ModdedRigInterface_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 45600 |
| ![icon_small_ModdedRigInterface_Tier_09](assets/item-icons/icon_small_ModdedRigInterface_Tier_09.png) | Rigging_ModdedRigInterface_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_ModdedRigInterface_Tier_10](assets/item-icons/icon_small_ModdedRigInterface_Tier_10.png) | Rigging_ModdedRigInterface_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 285200 |

### Shotgun

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Shotgun_Mossberg_AM_CMDT_Blue_Tier_07](assets/item-icons/icon_small_Shotgun_Mossberg_AM_CMDT_Blue_Tier_07.png) | Shotgun_Mossberg_AM_CMDT_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Shotgun_Mossberg_AM_CMDT_Tier_07](assets/item-icons/icon_small_Shotgun_Mossberg_AM_CMDT_Tier_07.png) | Shotgun_Mossberg_AM_CMDT_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_RemingtonRoomsweeper_Tier_03](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_03.png) | Shotgun_RemingtonRoomsweeper_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14800 |
| ![icon_small_RemingtonRoomsweeper_Tier_04](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_04.png) | Shotgun_RemingtonRoomsweeper_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20000 |
| ![icon_small_RemingtonRoomsweeper_Tier_05](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_05.png) | Shotgun_RemingtonRoomsweeper_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 22300 |
| ![icon_small_RemingtonRoomsweeper_Tier_06](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_06.png) | Shotgun_RemingtonRoomsweeper_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 23400 |
| ![icon_small_RemingtonSportsman_Tier_01](assets/item-icons/icon_small_RemingtonSportsman_Tier_01.png) | Shotgun_RemingtonSportsman_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 7800 |
| ![icon_small_RemingtonSportsman_Tier_02](assets/item-icons/icon_small_RemingtonSportsman_Tier_02.png) | Shotgun_RemingtonSportsman_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 14000 |
| ![icon_small_RemingtonRoomsweeper_Tier_08](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_08.png) | Shotgun_RemingtonSportsman_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 53600 |
| ![icon_small_RemingtonRoomsweeper_Tier_09](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_09.png) | Shotgun_RemingtonSportsman_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_RemingtonRoomsweeper_Tier_10](assets/item-icons/icon_small_RemingtonRoomsweeper_Tier_10.png) | Shotgun_RemingtonSportsman_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 267800 |
| ![icon_small_Spas22_Tier_01](assets/item-icons/icon_small_Spas22_Tier_01.png) | Shotgun_Spas22_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 9400 |
| ![icon_small_Spas22_Tier_03](assets/item-icons/icon_small_Spas22_Tier_03.png) | Shotgun_Spas22_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 17700 |
| ![icon_small_Spas22_Tier_04](assets/item-icons/icon_small_Spas22_Tier_04.png) | Shotgun_Spas22_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 24000 |
| ![icon_small_Spas22_Tier_05](assets/item-icons/icon_small_Spas22_Tier_05.png) | Shotgun_Spas22_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 26700 |
| ![icon_small_Spas22_Tier_06](assets/item-icons/icon_small_Spas22_Tier_06.png) | Shotgun_Spas22_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 28100 |
| ![icon_small_Spas22_Tier_08](assets/item-icons/icon_small_Spas22_Tier_08.png) | Shotgun_Spas22_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 44600 |
| ![icon_small_Spas22_Tier_09](assets/item-icons/icon_small_Spas22_Tier_09.png) | Shotgun_Spas22_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_Spas22_Tier_10](assets/item-icons/icon_small_Spas22_Tier_10.png) | Shotgun_Spas22_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 223200 |

### Spellcasting

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Spellcasting_Obsidian_Power_Focus_Blue_Tier_07](assets/item-icons/icon_small_Spellcasting_Obsidian_Power_Focus_Blue_Tier_07.png) | Spellcasting_Obsidian_Power_Focus_Blue_Tier_07 | Challenge01Phase03LootTable | No |  |
| ![icon_small_Spellcasting_Obsidian_Power_Focus_Tier_07](assets/item-icons/icon_small_Spellcasting_Obsidian_Power_Focus_Tier_07.png) | Spellcasting_Obsidian_Power_Focus_Tier_07 | Challenge02Phase03LootTable | No |  |
| ![icon_small_OverchargeFocus_Tier_01](assets/item-icons/icon_small_OverchargeFocus_Tier_01.png) | Spellcasting_OverchargeFocus_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 12900 |
| ![icon_small_OverchargeFocus_Tier_03](assets/item-icons/icon_small_OverchargeFocus_Tier_03.png) | Spellcasting_OverchargeFocus_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20300 |
| ![icon_small_OverchargeFocus_Tier_04](assets/item-icons/icon_small_OverchargeFocus_Tier_04.png) | Spellcasting_OverchargeFocus_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 27500 |
| ![icon_small_OverchargeFocus_Tier_05](assets/item-icons/icon_small_OverchargeFocus_Tier_05.png) | Spellcasting_OverchargeFocus_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 30600 |
| ![icon_small_OverchargeFocus_Tier_06](assets/item-icons/icon_small_OverchargeFocus_Tier_06.png) | Spellcasting_OverchargeFocus_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 32200 |
| ![icon_small_OverchargeFocus_Tier_08](assets/item-icons/icon_small_OverchargeFocus_Tier_08.png) | Spellcasting_OverchargeFocus_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 61400 |
| ![icon_small_OverchargeFocus_Tier_09](assets/item-icons/icon_small_OverchargeFocus_Tier_09.png) | Spellcasting_OverchargeFocus_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_OverchargeFocus_Tier_10](assets/item-icons/icon_small_OverchargeFocus_Tier_10.png) | Spellcasting_OverchargeFocus_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 306900 |
| ![icon_small_PowerFocus_Tier_01](assets/item-icons/icon_small_PowerFocus_Tier_01.png) | Spellcasting_PowerFocus_Tier_01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 8600 |
| ![icon_small_PowerFocus_Tier_02](assets/item-icons/icon_small_PowerFocus_Tier_02.png) | Spellcasting_PowerFocus_Tier_02 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 15400 |
| ![icon_small_PowerFocus_Tier_03](assets/item-icons/icon_small_PowerFocus_Tier_03.png) | Spellcasting_PowerFocus_Tier_03 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 20300 |
| ![icon_small_PowerFocus_Tier_04](assets/item-icons/icon_small_PowerFocus_Tier_04.png) | Spellcasting_PowerFocus_Tier_04 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 27500 |
| ![icon_small_PowerFocus_Tier_05](assets/item-icons/icon_small_PowerFocus_Tier_05.png) | Spellcasting_PowerFocus_Tier_05 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 30600 |
| ![icon_small_PowerFocus_Tier_06](assets/item-icons/icon_small_PowerFocus_Tier_06.png) | Spellcasting_PowerFocus_Tier_06 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 32200 |
| ![icon_small_PowerFocus_Tier_08](assets/item-icons/icon_small_PowerFocus_Tier_08.png) | Spellcasting_PowerFocus_Tier_08 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 61400 |
| ![icon_small_PowerFocus_Tier_09](assets/item-icons/icon_small_PowerFocus_Tier_09.png) | Spellcasting_PowerFocus_Tier_09 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | No |  |
| ![icon_small_PowerFocus_Tier_10](assets/item-icons/icon_small_PowerFocus_Tier_10.png) | Spellcasting_PowerFocus_Tier_10 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable, WeaponsLootTable | Yes | 306900 |

### Cyberware Arms

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_Cyberware_Bioware_Arms](assets/item-icons/icon_small_Item_Cyberware_Bioware_Arms.png) | Item_Cyberware_Bioware_Arms | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 67300 |
| ![icon_small_Item_Cyberware_BoostedBioware_Arms](assets/item-icons/icon_small_Item_Cyberware_BoostedBioware_Arms.png) | Item_Cyberware_BoostedBioware_Arms | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 168200 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_1_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_1_Alpha | OrganHarvestingLootTable | Yes | 4100 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_1_Beta](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_1_Beta.png) | Item_Cyberware_ReinforcedCyberarm_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 7100 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_2_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_2_Alpha | OrganHarvestingLootTable | Yes | 4900 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_2_Beta](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_2_Beta.png) | Item_Cyberware_ReinforcedCyberarm_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8600 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_3_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_3_Alpha | OrganHarvestingLootTable | Yes | 6300 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_3_Beta](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_3_Beta.png) | Item_Cyberware_ReinforcedCyberarm_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 11100 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_4_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_4_Alpha | OrganHarvestingLootTable | Yes | 13300 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_4_Beta](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_4_Beta.png) | Item_Cyberware_ReinforcedCyberarm_4_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 23300 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_5_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_5_Alpha | OrganHarvestingLootTable | Yes | 14700 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_6_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_6_Alpha | OrganHarvestingLootTable | Yes | 25800 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_6_Delta](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_6_Delta.png) | Item_Cyberware_ReinforcedCyberarm_6_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 73600 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_7_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_7_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_7_Alpha | OrganHarvestingLootTable | Yes | 25800 |
| ![icon_small_Item_Cyberware_ReinforcedCyberarm_7_Alpha](assets/item-icons/icon_small_Item_Cyberware_ReinforcedCyberarm_7_Alpha.png) | Item_Cyberware_ReinforcedCyberarm_8_Alpha | OrganHarvestingLootTable | Yes | 128800 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_1_Alpha.png) | Item_Cyberware_StabilisedCyberarm_1_Alpha | OrganHarvestingLootTable | Yes | 4100 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_1_Beta](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_1_Beta.png) | Item_Cyberware_StabilisedCyberarm_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 7100 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_2_Alpha.png) | Item_Cyberware_StabilisedCyberarm_2_Alpha | OrganHarvestingLootTable | Yes | 4900 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_2_Beta](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_2_Beta.png) | Item_Cyberware_StabilisedCyberarm_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8600 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_3_Alpha.png) | Item_Cyberware_StabilisedCyberarm_3_Alpha | OrganHarvestingLootTable | Yes | 6300 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_3_Beta](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_3_Beta.png) | Item_Cyberware_StabilisedCyberarm_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 11100 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_4_Alpha.png) | Item_Cyberware_StabilisedCyberarm_4_Alpha | OrganHarvestingLootTable | Yes | 13300 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_4_Beta](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_4_Beta.png) | Item_Cyberware_StabilisedCyberarm_4_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 23300 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_5_Alpha.png) | Item_Cyberware_StabilisedCyberarm_5_Alpha | OrganHarvestingLootTable | Yes | 14700 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_6_Alpha.png) | Item_Cyberware_StabilisedCyberarm_6_Alpha | OrganHarvestingLootTable | Yes | 25800 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_6_Delta](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_6_Delta.png) | Item_Cyberware_StabilisedCyberarm_6_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 73600 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_7_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_7_Alpha.png) | Item_Cyberware_StabilisedCyberarm_7_Alpha | OrganHarvestingLootTable | Yes | 25800 |
| ![icon_small_Item_Cyberware_StabilisedCyberarm_7_Alpha](assets/item-icons/icon_small_Item_Cyberware_StabilisedCyberarm_7_Alpha.png) | Item_Cyberware_StabilisedCyberarm_8_Alpha | OrganHarvestingLootTable | Yes | 128800 |

### Cyberware Head

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_Cyberware_Bioware_Head](assets/item-icons/icon_small_Item_Cyberware_Bioware_Head.png) | Item_Cyberware_Bioware_Head | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 58000 |
| ![icon_small_Item_Cyberware_BoostedBioware_Head](assets/item-icons/icon_small_Item_Cyberware_BoostedBioware_Head.png) | Item_Cyberware_BoostedBioware_Head | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 145000 |
| ![icon_small_Item_Cyberware_BoostedComlink_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_1_Alpha.png) | Item_Cyberware_BoostedComlink_1_Alpha | OrganHarvestingLootTable | Yes | 4200 |
| ![icon_small_Item_Cyberware_BoostedComlink_1_Beta](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_1_Beta.png) | Item_Cyberware_BoostedComlink_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 7400 |
| ![icon_small_Item_Cyberware_BoostedComlink_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_2_Alpha.png) | Item_Cyberware_BoostedComlink_2_Alpha | OrganHarvestingLootTable | Yes | 12700 |
| ![icon_small_Item_Cyberware_BoostedComlink_2_Delta](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_2_Delta.png) | Item_Cyberware_BoostedComlink_2_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 63500 |
| ![icon_small_Item_Cyberware_BoostedComlink_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_3_Alpha.png) | Item_Cyberware_BoostedComlink_3_Alpha | OrganHarvestingLootTable | Yes | 22200 |
| ![icon_small_Item_Cyberware_BoostedComlink_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_BoostedComlink_3_Alpha.png) | Item_Cyberware_BoostedComlink_4_Alpha | OrganHarvestingLootTable | Yes | 111100 |
| ![icon_small_Item_Cyberware_CranialRemote_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_1_Alpha.png) | Item_Cyberware_CranialRemote_1_Alpha | OrganHarvestingLootTable | Yes | 4200 |
| ![icon_small_Item_Cyberware_CranialRemote_1_Beta](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_1_Beta.png) | Item_Cyberware_CranialRemote_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 7400 |
| ![icon_small_Item_Cyberware_CranialRemote_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_2_Alpha.png) | Item_Cyberware_CranialRemote_2_Alpha | OrganHarvestingLootTable | Yes | 5500 |
| ![icon_small_Item_Cyberware_CranialRemote_2_Beta](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_2_Beta.png) | Item_Cyberware_CranialRemote_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 9600 |
| ![icon_small_Item_Cyberware_CranialRemote_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_3_Alpha.png) | Item_Cyberware_CranialRemote_3_Alpha | OrganHarvestingLootTable | Yes | 11500 |
| ![icon_small_Item_Cyberware_CranialRemote_3_Beta](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_3_Beta.png) | Item_Cyberware_CranialRemote_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 20100 |
| ![icon_small_Item_Cyberware_CranialRemote_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_4_Alpha.png) | Item_Cyberware_CranialRemote_4_Alpha | OrganHarvestingLootTable | Yes | 12700 |
| ![icon_small_Item_Cyberware_CranialRemote_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_5_Alpha.png) | Item_Cyberware_CranialRemote_5_Alpha | OrganHarvestingLootTable | Yes | 22200 |
| ![icon_small_Item_Cyberware_CranialRemote_5_Delta](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_5_Delta.png) | Item_Cyberware_CranialRemote_5_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 63500 |
| ![icon_small_Item_Cyberware_CranialRemote_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_6_Alpha.png) | Item_Cyberware_CranialRemote_6_Alpha | OrganHarvestingLootTable | Yes | 22200 |
| ![icon_small_Item_Cyberware_CranialRemote_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_CranialRemote_6_Alpha.png) | Item_Cyberware_CranialRemote_7_Alpha | OrganHarvestingLootTable | Yes | 111100 |
| ![icon_small_Item_Cyberware_VisionMagnification_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_1_Alpha.png) | Item_Cyberware_VisionMagnification_1_Alpha | OrganHarvestingLootTable | Yes | 5500 |
| ![icon_small_Item_Cyberware_VisionMagnification_1_Beta](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_1_Beta.png) | Item_Cyberware_VisionMagnification_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 9600 |
| ![icon_small_Item_Cyberware_VisionMagnification_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_2_Alpha.png) | Item_Cyberware_VisionMagnification_2_Alpha | OrganHarvestingLootTable | Yes | 11500 |
| ![icon_small_Item_Cyberware_VisionMagnification_2_Beta](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_2_Beta.png) | Item_Cyberware_VisionMagnification_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 20100 |
| ![icon_small_Item_Cyberware_VisionMagnification_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_3_Alpha.png) | Item_Cyberware_VisionMagnification_3_Alpha | OrganHarvestingLootTable | Yes | 12700 |
| ![icon_small_Item_Cyberware_VisionMagnification_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_4_Alpha.png) | Item_Cyberware_VisionMagnification_4_Alpha | OrganHarvestingLootTable | Yes | 22200 |
| ![icon_small_Item_Cyberware_VisionMagnification_4_Delta](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_4_Delta.png) | Item_Cyberware_VisionMagnification_4_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 63500 |
| ![icon_small_Item_Cyberware_VisionMagnification_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_5_Alpha.png) | Item_Cyberware_VisionMagnification_5_Alpha | OrganHarvestingLootTable | Yes | 22200 |
| ![icon_small_Item_Cyberware_VisionMagnification_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_VisionMagnification_5_Alpha.png) | Item_Cyberware_VisionMagnification_6_Alpha | OrganHarvestingLootTable | Yes | 111100 |

### Cyberware Legs

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_Cyberware_Bioware_Legs](assets/item-icons/icon_small_Item_Cyberware_Bioware_Legs.png) | Item_Cyberware_Bioware_Legs | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 69600 |
| ![icon_small_Item_Cyberware_BoostedBioware_Legs](assets/item-icons/icon_small_Item_Cyberware_BoostedBioware_Legs.png) | Item_Cyberware_BoostedBioware_Legs | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 174000 |
| ![icon_small_Item_Cyberware_Cyberlegs_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_1_Alpha.png) | Item_Cyberware_Cyberlegs_1_Alpha | OrganHarvestingLootTable | Yes | 5100 |
| ![icon_small_Item_Cyberware_Cyberlegs_1_Beta](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_1_Beta.png) | Item_Cyberware_Cyberlegs_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8900 |
| ![icon_small_Item_Cyberware_Cyberlegs_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_2_Alpha.png) | Item_Cyberware_Cyberlegs_2_Alpha | OrganHarvestingLootTable | Yes | 6600 |
| ![icon_small_Item_Cyberware_Cyberlegs_2_Beta](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_2_Beta.png) | Item_Cyberware_Cyberlegs_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 11500 |
| ![icon_small_Item_Cyberware_Cyberlegs_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_3_Alpha.png) | Item_Cyberware_Cyberlegs_3_Alpha | OrganHarvestingLootTable | Yes | 13800 |
| ![icon_small_Item_Cyberware_Cyberlegs_3_Beta](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_3_Beta.png) | Item_Cyberware_Cyberlegs_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 24200 |
| ![icon_small_Item_Cyberware_Cyberlegs_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_4_Alpha.png) | Item_Cyberware_Cyberlegs_4_Alpha | OrganHarvestingLootTable | Yes | 15200 |
| ![icon_small_Item_Cyberware_Cyberlegs_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_5_Alpha.png) | Item_Cyberware_Cyberlegs_5_Alpha | OrganHarvestingLootTable | Yes | 26700 |
| ![icon_small_Item_Cyberware_Cyberlegs_5_Delta](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_5_Delta.png) | Item_Cyberware_Cyberlegs_5_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 76200 |
| ![icon_small_Item_Cyberware_Cyberlegs_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_6_Alpha.png) | Item_Cyberware_Cyberlegs_6_Alpha | OrganHarvestingLootTable | Yes | 26700 |
| ![icon_small_Item_Cyberware_Cyberlegs_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Cyberlegs_6_Alpha.png) | Item_Cyberware_Cyberlegs_7_Alpha | OrganHarvestingLootTable | Yes | 133300 |
| ![icon_small_Item_Cyberware_GlandularImplants_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_1_Alpha.png) | Item_Cyberware_GlandularImplants_1_Alpha | OrganHarvestingLootTable | Yes | 5100 |
| ![icon_small_Item_Cyberware_GlandularImplants_1_Beta](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_1_Beta.png) | Item_Cyberware_GlandularImplants_1_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8900 |
| ![icon_small_Item_Cyberware_GlandularImplants_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_2_Alpha.png) | Item_Cyberware_GlandularImplants_2_Alpha | OrganHarvestingLootTable | Yes | 6600 |
| ![icon_small_Item_Cyberware_GlandularImplants_2_Beta](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_2_Beta.png) | Item_Cyberware_GlandularImplants_2_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 11500 |
| ![icon_small_Item_Cyberware_GlandularImplants_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_3_Alpha.png) | Item_Cyberware_GlandularImplants_3_Alpha | OrganHarvestingLootTable | Yes | 13800 |
| ![icon_small_Item_Cyberware_GlandularImplants_3_Beta](assets/item-icons/icon_small_Item_Cyberware_GlandularImplants_3_Beta.png) | Item_Cyberware_GlandularImplants_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 24200 |
|  | Item_Cyberware_Glandularimplants_4_Alpha | OrganHarvestingLootTable | Yes | 15200 |
|  | Item_Cyberware_Glandularimplants_5_Alpha | OrganHarvestingLootTable | Yes | 26700 |
|  | Item_Cyberware_Glandularimplants_5_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 76200 |
|  | Item_Cyberware_Glandularimplants_6_Alpha | OrganHarvestingLootTable | Yes | 26700 |
|  | Item_Cyberware_Glandularimplants_7_Alpha | OrganHarvestingLootTable | Yes | 133300 |

### Cyberware Torso

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_Cyberware_Bioware_Torso](assets/item-icons/icon_small_Item_Cyberware_Bioware_Torso.png) | Item_Cyberware_Bioware_Torso | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 92800 |
| ![icon_small_Item_Cyberware_BoostedBioware_Torso](assets/item-icons/icon_small_Item_Cyberware_BoostedBioware_Torso.png) | Item_Cyberware_BoostedBioware_Torso | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 232000 |
| ![icon_small_Item_Cyberware_Dermal_Plating_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_1_Alpha.png) | Item_Cyberware_Dermal_Plating_1_Alpha | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 6800 |
| ![icon_small_Item_Cyberware_Dermal_Plating_1_Beta](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_1_Beta.png) | Item_Cyberware_Dermal_Plating_1_Beta | OrganHarvestingLootTable | Yes | 11800 |
| ![icon_small_Item_Cyberware_Dermal_Plating_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_2_Alpha.png) | Item_Cyberware_Dermal_Plating_2_Alpha | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8700 |
| ![icon_small_Item_Cyberware_Dermal_Plating_2_Beta](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_2_Beta.png) | Item_Cyberware_Dermal_Plating_2_Beta | OrganHarvestingLootTable | Yes | 15300 |
| ![icon_small_Item_Cyberware_Dermal_Plating_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_3_Alpha.png) | Item_Cyberware_Dermal_Plating_3_Alpha | OrganHarvestingLootTable | Yes | 18400 |
| ![icon_small_Item_Cyberware_Dermal_Plating_3_Beta](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_3_Beta.png) | Item_Cyberware_Dermal_Plating_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 32200 |
| ![icon_small_Item_Cyberware_Dermal_Plating_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_4_Alpha.png) | Item_Cyberware_Dermal_Plating_4_Alpha | OrganHarvestingLootTable | Yes | 20300 |
| ![icon_small_Item_Cyberware_Dermal_Plating_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_5_Alpha.png) | Item_Cyberware_Dermal_Plating_5_Alpha | OrganHarvestingLootTable | Yes | 35500 |
| ![icon_small_Item_Cyberware_Dermal_Plating_5_Delta](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_5_Delta.png) | Item_Cyberware_Dermal_Plating_5_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 101500 |
| ![icon_small_Item_Cyberware_Dermal_Plating_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_6_Alpha.png) | Item_Cyberware_Dermal_Plating_6_Alpha | OrganHarvestingLootTable | Yes | 35500 |
| ![icon_small_Item_Cyberware_Dermal_Plating_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Dermal_Plating_6_Alpha.png) | Item_Cyberware_Dermal_Plating_7_Alpha | OrganHarvestingLootTable | Yes | 177700 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_1_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_1_Alpha.png) | Item_Cyberware_Wired_Reflexes_1_Alpha | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 6800 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_1_Beta](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_1_Beta.png) | Item_Cyberware_Wired_Reflexes_1_Beta | OrganHarvestingLootTable | Yes | 11800 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_2_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_2_Alpha.png) | Item_Cyberware_Wired_Reflexes_2_Alpha | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 8700 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_2_Beta](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_2_Beta.png) | Item_Cyberware_Wired_Reflexes_2_Beta | OrganHarvestingLootTable | Yes | 15300 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_3_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_3_Alpha.png) | Item_Cyberware_Wired_Reflexes_3_Alpha | OrganHarvestingLootTable | Yes | 18400 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_3_Beta](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_3_Beta.png) | Item_Cyberware_Wired_Reflexes_3_Beta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 32200 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_4_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_4_Alpha.png) | Item_Cyberware_Wired_Reflexes_4_Alpha | OrganHarvestingLootTable | Yes | 20300 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_5_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_5_Alpha.png) | Item_Cyberware_Wired_Reflexes_5_Alpha | OrganHarvestingLootTable | Yes | 35500 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_5_Delta](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_5_Delta.png) | Item_Cyberware_Wired_Reflexes_5_Delta | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, OrganHarvestingLootTable | Yes | 101500 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_6_Alpha.png) | Item_Cyberware_Wired_Reflexes_6_Alpha | OrganHarvestingLootTable | Yes | 35500 |
| ![icon_small_Item_Cyberware_Wired_Reflexes_6_Alpha](assets/item-icons/icon_small_Item_Cyberware_Wired_Reflexes_6_Alpha.png) | Item_Cyberware_Wired_Reflexes_7_Alpha | OrganHarvestingLootTable | Yes | 177700 |

### Cosmetic Boots

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_GaiterBoots](assets/item-icons/icon_small_Item_GaiterBoots.png) | Item_GaiterBoots | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HammerShoes](assets/item-icons/icon_small_Item_HammerShoes.png) | Item_HammerShoes | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HideBoots](assets/item-icons/icon_small_Item_HideBoots.png) | Item_HideBoots | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MagefireBoots](assets/item-icons/icon_small_Item_MagefireBoots.png) | Item_MagefireBoots | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_Pack2HighHeelsTight](assets/item-icons/icon_small_Item_Pack2HighHeelsTight.png) | Item_Pack2HighHeelsTight | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Shinguards](assets/item-icons/icon_small_Item_Shinguards.png) | Item_Shinguards | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_ShoesRigger](assets/item-icons/icon_small_Item_ShoesRigger.png) | Item_ShoesRigger | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_SnakeskinBoots](assets/item-icons/icon_small_Item_SnakeskinBoots.png) | Item_SnakeskinBoots | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_TronBoots](assets/item-icons/icon_small_Item_TronBoots.png) | Item_TronBoots | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic Glasses

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_BandanaAnarchy](assets/item-icons/icon_small_Item_BandanaAnarchy.png) | Item_BandanaAnarchy | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaBlack](assets/item-icons/icon_small_Item_BandanaBlack.png) | Item_BandanaBlack | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaBlue](assets/item-icons/icon_small_Item_BandanaBlue.png) | Item_BandanaBlue | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaCamoDesert](assets/item-icons/icon_small_Item_BandanaCamoDesert.png) | Item_BandanaCamoDesert | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaCamoUrban](assets/item-icons/icon_small_Item_BandanaCamoUrban.png) | Item_BandanaCamoUrban | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaCamoWoodland](assets/item-icons/icon_small_Item_BandanaCamoWoodland.png) | Item_BandanaCamoWoodland | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaPaisleyBlack](assets/item-icons/icon_small_Item_BandanaPaisleyBlack.png) | Item_BandanaPaisleyBlack | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaPaisleyBlue](assets/item-icons/icon_small_Item_BandanaPaisleyBlue.png) | Item_BandanaPaisleyBlue | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaPaisleyRed](assets/item-icons/icon_small_Item_BandanaPaisleyRed.png) | Item_BandanaPaisleyRed | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_BandanaRed](assets/item-icons/icon_small_Item_BandanaRed.png) | Item_BandanaRed | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_FullGasMask](assets/item-icons/icon_small_Item_FullGasMask.png) | Item_FullGasMask | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_FullHeadGasMask](assets/item-icons/icon_small_Item_FullHeadGasMask.png) | Item_FullHeadGasMask | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack3GlassesRayban](assets/item-icons/icon_small_Item_Pack3GlassesRayban.png) | Item_Pack3GlassesRayban | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack3GlassesRaybanFrame](assets/item-icons/icon_small_Item_Pack3GlassesRaybanFrame.png) | Item_Pack3GlassesRaybanFrame | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack3Monocle](assets/item-icons/icon_small_Item_Pack3Monocle.png) | Item_Pack3Monocle | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_ShamanMask2](assets/item-icons/icon_small_Item_ShamanMask2.png) | Item_ShamanMask2 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic Hands

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_MagefireBandagesArm](assets/item-icons/icon_small_Item_MagefireBandagesArm.png) | Item_MagefireBandagesArm | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_MagefireGlowHands](assets/item-icons/icon_small_Item_MagefireGlowHands.png) | Item_MagefireGlowHands | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_Pack2Knuckles](assets/item-icons/icon_small_Item_Pack2Knuckles.png) | Item_Pack2Knuckles | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic Head

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_CapotainHat](assets/item-icons/icon_small_Item_CapotainHat.png) | Item_CapotainHat | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HammerHelmet](assets/item-icons/icon_small_Item_HammerHelmet.png) | Item_HammerHelmet | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HatWizard](assets/item-icons/icon_small_Item_HatWizard.png) | Item_HatWizard | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MagefireHood](assets/item-icons/icon_small_Item_MagefireHood.png) | Item_MagefireHood | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_Pack2Beret](assets/item-icons/icon_small_Item_Pack2Beret.png) | Item_Pack2Beret | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack4TopHat](assets/item-icons/icon_small_Item_Pack4TopHat.png) | Item_Pack4TopHat | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RastaHatBlack](assets/item-icons/icon_small_Item_RastaHatBlack.png) | Item_RastaHatBlack | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RastaHatBlue](assets/item-icons/icon_small_Item_RastaHatBlue.png) | Item_RastaHatBlue | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RastaHatGreen](assets/item-icons/icon_small_Item_RastaHatGreen.png) | Item_RastaHatGreen | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RastaHatOrange](assets/item-icons/icon_small_Item_RastaHatOrange.png) | Item_RastaHatOrange | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RastaHatRed](assets/item-icons/icon_small_Item_RastaHatRed.png) | Item_RastaHatRed | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RiotHelmetKE](assets/item-icons/icon_small_Item_RiotHelmetKE.png) | Item_RiotHelmetKE | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RoadRageHelmet](assets/item-icons/icon_small_Item_RoadRageHelmet.png) | Item_RoadRageHelmet | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic LowerBody

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_ElegantPants](assets/item-icons/icon_small_Item_ElegantPants.png) | Item_ElegantPants | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_GothPants](assets/item-icons/icon_small_Item_GothPants.png) | Item_GothPants | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HammerPants](assets/item-icons/icon_small_Item_HammerPants.png) | Item_HammerPants | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MagefireKilt](assets/item-icons/icon_small_Item_MagefireKilt.png) | Item_MagefireKilt | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_Pack1JeansBlue01](assets/item-icons/icon_small_Item_Pack1JeansBlue01.png) | Item_Pack1JeansBlue01 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack1JeansStylus](assets/item-icons/icon_small_Item_Pack1JeansStylus.png) | Item_Pack1JeansStylus | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack1Skirt3](assets/item-icons/icon_small_Item_Pack1Skirt3.png) | Item_Pack1Skirt3 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack1Skirt4](assets/item-icons/icon_small_Item_Pack1Skirt4.png) | Item_Pack1Skirt4 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RiotPants](assets/item-icons/icon_small_Item_RiotPants.png) | Item_RiotPants | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_RiotPantsKE](assets/item-icons/icon_small_Item_RiotPantsKE.png) | Item_RiotPantsKE | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_ShamanPants](assets/item-icons/icon_small_Item_ShamanPants.png) | Item_ShamanPants | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_ShortsUrban](assets/item-icons/icon_small_Item_ShortsUrban.png) | Item_ShortsUrban | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_TronPants](assets/item-icons/icon_small_Item_TronPants.png) | Item_TronPants | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_VictorianPants](assets/item-icons/icon_small_Item_VictorianPants.png) | Item_VictorianPants | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic LowerUnderwear

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_Pack1StockingsGarters](assets/item-icons/icon_small_Item_Pack1StockingsGarters.png) | Item_Pack1StockingsGarters | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic Tattoo

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_AncientsLogoBlack](assets/item-icons/icon_small_Item_AncientsLogoBlack.png) | Item_AncientsLogoBlack | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Hex1Neon](assets/item-icons/icon_small_Item_Hex1Neon.png) | Item_Hex1Neon | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_LadyLuckTribal1Neon](assets/item-icons/icon_small_Item_LadyLuckTribal1Neon.png) | Item_LadyLuckTribal1Neon | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MaoriForearmTribal1Neon](assets/item-icons/icon_small_Item_MaoriForearmTribal1Neon.png) | Item_MaoriForearmTribal1Neon | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MaoriTribal1Neon](assets/item-icons/icon_small_Item_MaoriTribal1Neon.png) | Item_MaoriTribal1Neon | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic UpperBody

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_ClassicMageCoat](assets/item-icons/icon_small_Item_ClassicMageCoat.png) | Item_ClassicMageCoat | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_CoatBelmont](assets/item-icons/icon_small_Item_CoatBelmont.png) | Item_CoatBelmont | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Cybervest](assets/item-icons/icon_small_Item_Cybervest.png) | Item_Cybervest | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_HammerSleeves](assets/item-icons/icon_small_Item_HammerSleeves.png) | Item_HammerSleeves | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_MagefireBandagesWaist](assets/item-icons/icon_small_Item_MagefireBandagesWaist.png) | Item_MagefireBandagesWaist | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_MagefireShroud](assets/item-icons/icon_small_Item_MagefireShroud.png) | Item_MagefireShroud | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_NeoHarness](assets/item-icons/icon_small_Item_NeoHarness.png) | Item_NeoHarness | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack2SuitVestSpecial](assets/item-icons/icon_small_Item_Pack2SuitVestSpecial.png) | Item_Pack2SuitVestSpecial | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack3Biohazard](assets/item-icons/icon_small_Item_Pack3Biohazard.png) | Item_Pack3Biohazard | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack4LabCoat](assets/item-icons/icon_small_Item_Pack4LabCoat.png) | Item_Pack4LabCoat | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack4PrisonOverall](assets/item-icons/icon_small_Item_Pack4PrisonOverall.png) | Item_Pack4PrisonOverall | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_PoliceVest](assets/item-icons/icon_small_Item_PoliceVest.png) | Item_PoliceVest | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_PuffTieVestSpecial](assets/item-icons/icon_small_Item_PuffTieVestSpecial.png) | Item_PuffTieVestSpecial | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_TronCowl](assets/item-icons/icon_small_Item_TronCowl.png) | Item_TronCowl | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Cosmetic UpperUnderwear

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_NippleTape](assets/item-icons/icon_small_Item_NippleTape.png) | Item_NippleTape | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack1Jumpsuit](assets/item-icons/icon_small_Item_Pack1Jumpsuit.png) | Item_Pack1Jumpsuit | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_Pack1TopSkimpy](assets/item-icons/icon_small_Item_Pack1TopSkimpy.png) | Item_Pack1TopSkimpy | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |

### Armor

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_CeramicPlating1](assets/item-icons/icon_small_CeramicPlating1.png) | Item_CeramicPlating1 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 4300 |
| ![icon_small_CeramicPlating2](assets/item-icons/icon_small_CeramicPlating2.png) | Item_CeramicPlating2 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 9400 |
| ![icon_small_CeramicPlating3](assets/item-icons/icon_small_CeramicPlating3.png) | Item_CeramicPlating3 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 13300 |
| ![icon_small_CeramicPlating4](assets/item-icons/icon_small_CeramicPlating4.png) | Item_CeramicPlating4 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 14800 |
| ![icon_small_CeramicPlating5](assets/item-icons/icon_small_CeramicPlating5.png) | Item_CeramicPlating5 | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_CeramicPlating6](assets/item-icons/icon_small_CeramicPlating6.png) | Item_CeramicPlating6 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 29800 |
| ![icon_small_CeramicPlating7](assets/item-icons/icon_small_CeramicPlating7.png) | Item_CeramicPlating7 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 36800 |
| ![icon_small_CeramicPlating7](assets/item-icons/icon_small_CeramicPlating7.png) | Item_CeramicPlating8 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 148800 |
| ![icon_small_ElementalEssenceSpray2](assets/item-icons/icon_small_ElementalEssenceSpray2.png) | Item_ElementalEssenceSpray2 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 22, 7000 |
| ![icon_small_ElementalEssenceSpray3](assets/item-icons/icon_small_ElementalEssenceSpray3.png) | Item_ElementalEssenceSpray3 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 10000 |
| ![icon_small_ElementalEssenceSpray4](assets/item-icons/icon_small_ElementalEssenceSpray4.png) | Item_ElementalEssenceSpray4 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 11100 |
| ![icon_small_ElementalEssenceSpray5](assets/item-icons/icon_small_ElementalEssenceSpray5.png) | Item_ElementalEssenceSpray5 | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_ElementalEssenceSpray6](assets/item-icons/icon_small_ElementalEssenceSpray6.png) | Item_ElementalEssenceSpray6 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 22300 |
| ![icon_small_ElementalEssenceSpray7](assets/item-icons/icon_small_ElementalEssenceSpray7.png) | Item_ElementalEssenceSpray7 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 27600 |
| ![icon_small_ElementalEssenceSpray7](assets/item-icons/icon_small_ElementalEssenceSpray7.png) | Item_ElementalEssenceSpray8 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 111600 |
| ![icon_small_EnchantedBiofiber2](assets/item-icons/icon_small_EnchantedBiofiber2.png) | Item_EnchantedBiofiber2 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 11100 |
| ![icon_small_EnchantedBiofiber3](assets/item-icons/icon_small_EnchantedBiofiber3.png) | Item_EnchantedBiofiber3 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 12400 |
| ![icon_small_EnchantedBiofiber4](assets/item-icons/icon_small_EnchantedBiofiber4.png) | Item_EnchantedBiofiber4 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 22, 13000 |
| ![icon_small_EnchantedBiofiber5](assets/item-icons/icon_small_EnchantedBiofiber5.png) | Item_EnchantedBiofiber5 | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_EnchantedBiofiber6](assets/item-icons/icon_small_EnchantedBiofiber6.png) | Item_EnchantedBiofiber6 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 24800 |
| ![icon_small_EnchantedBiofiber7](assets/item-icons/icon_small_EnchantedBiofiber7.png) | Item_EnchantedBiofiber7 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 30600 |
| ![icon_small_EnchantedBiofiber7](assets/item-icons/icon_small_EnchantedBiofiber7.png) | Item_EnchantedBiofiber8 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 124000 |
| ![icon_small_PolymerCoating1](assets/item-icons/icon_small_PolymerCoating1.png) | Item_PolymerCoating1 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 2500 |
| ![icon_small_PolymerCoating2](assets/item-icons/icon_small_PolymerCoating2.png) | Item_PolymerCoating2 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 5500 |
| ![icon_small_PolymerCoating3](assets/item-icons/icon_small_PolymerCoating3.png) | Item_PolymerCoating3 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 7800 |
| ![icon_small_PolymerCoating4](assets/item-icons/icon_small_PolymerCoating4.png) | Item_PolymerCoating4 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 8700 |
| ![icon_small_PolymerCoating5](assets/item-icons/icon_small_PolymerCoating5.png) | Item_PolymerCoating5 | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_PolymerCoating6](assets/item-icons/icon_small_PolymerCoating6.png) | Item_PolymerCoating6 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 17400 |
| ![icon_small_PolymerCoating7](assets/item-icons/icon_small_PolymerCoating7.png) | Item_PolymerCoating7 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 21400 |
| ![icon_small_PolymerCoating7](assets/item-icons/icon_small_PolymerCoating7.png) | Item_PolymerCoating8 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 86800 |
| ![icon_small_ResponsiveECMFilaments2](assets/item-icons/icon_small_ResponsiveECMFilaments2.png) | Item_ResponsiveECMFilaments2 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 11100 |
| ![icon_small_ResponsiveECMFilaments3](assets/item-icons/icon_small_ResponsiveECMFilaments3.png) | Item_ResponsiveECMFilaments3 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 12400 |
| ![icon_small_ResponsiveECMFilaments4](assets/item-icons/icon_small_ResponsiveECMFilaments4.png) | Item_ResponsiveECMFilaments4 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 13000 |
| ![icon_small_ResponsiveECMFilaments5](assets/item-icons/icon_small_ResponsiveECMFilaments5.png) | Item_ResponsiveECMFilaments5 | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_ResponsiveECMFilaments6](assets/item-icons/icon_small_ResponsiveECMFilaments6.png) | Item_ResponsiveECMFilaments6 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 24800 |
| ![icon_small_ResponsiveECMFilaments7](assets/item-icons/icon_small_ResponsiveECMFilaments7.png) | Item_ResponsiveECMFilaments7 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 30600 |
| ![icon_small_ResponsiveECMFilaments7](assets/item-icons/icon_small_ResponsiveECMFilaments7.png) | Item_ResponsiveECMFilaments8 | ArmoredChestLootTable, ArmorsLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | Yes | 124000 |

### Pets

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_air_spirit_mini](assets/item-icons/icon_small_Item_air_spirit_mini.png) | Item_air_spirit_mini | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_assault_drone_mini](assets/item-icons/icon_small_Item_assault_drone_mini.png) | Item_assault_drone_mini | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_assault_drone_mini_red](assets/item-icons/icon_small_Item_assault_drone_mini_red.png) | Item_assault_drone_mini_red | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_barghest_brown_armored](assets/item-icons/icon_small_Item_barghest_brown_armored.png) | Item_barghest_brown_armored | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_barghest_brown_fire](assets/item-icons/icon_small_Item_barghest_brown_fire.png) | Item_barghest_brown_fire | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_barghest_brown_normal](assets/item-icons/icon_small_Item_barghest_brown_normal.png) | Item_barghest_brown_normal | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_barghest_white_armored](assets/item-icons/icon_small_Item_barghest_white_armored.png) | Item_barghest_white_armored | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_barghest_white_glowing](assets/item-icons/icon_small_Item_barghest_white_glowing.png) | Item_barghest_white_glowing | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_bear_spirit_mini](assets/item-icons/icon_small_Item_bear_spirit_mini.png) | Item_bear_spirit_mini | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_blood_deamon_mini_bloody](assets/item-icons/icon_small_Item_blood_deamon_mini_bloody.png) | Item_blood_deamon_mini_bloody | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_blood_deamon_mini_normal](assets/item-icons/icon_small_Item_blood_deamon_mini_normal.png) | Item_blood_deamon_mini_normal | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_cyberdog_advanced](assets/item-icons/icon_small_Item_cyberdog_advanced.png) | Item_cyberdog_advanced | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_cyberdog_normal](assets/item-icons/icon_small_Item_cyberdog_normal.png) | Item_cyberdog_normal | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_cyberdog_shiny](assets/item-icons/icon_small_Item_cyberdog_shiny.png) | Item_cyberdog_shiny | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_dwarf_mini](assets/item-icons/icon_small_Item_dwarf_mini.png) | Item_dwarf_mini | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_halloweenpet_01](assets/item-icons/icon_small_Item_halloweenpet_01.png) | Item_halloweenpet_01 | ArmoredChestLootTable, Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
| ![icon_small_Item_renraku_intruder_mini](assets/item-icons/icon_small_Item_renraku_intruder_mini.png) | Item_renraku_intruder_mini | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_suicide_bomber_advanced](assets/item-icons/icon_small_Item_suicide_bomber_advanced.png) | Item_suicide_bomber_advanced | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |
| ![icon_small_Item_suicide_bomber_normal](assets/item-icons/icon_small_Item_suicide_bomber_normal.png) | Item_suicide_bomber_normal | Challenge01Phase02LootTable, Challenge01Phase03LootTable, Challenge02Phase02LootTable, Challenge02Phase03LootTable | No |  |

### Tactical

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_BustersFlamethrower](assets/item-icons/icon_small_BustersFlamethrower.png) | Item_Consumable_BustersFlamethrower | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
| ![icon_small_Cram](assets/item-icons/icon_small_Cram.png) | Item_Consumable_Cram | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2350 |
| ![icon_small_DocWagonCardBasic](assets/item-icons/icon_small_DocWagonCardBasic.png) | Item_Consumable_DocWagonCardBasic | ArmoredChestLootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2200 |
| ![icon_small_DocWagonCardGold](assets/item-icons/icon_small_DocWagonCardGold.png) | Item_Consumable_DocWagonCardGold | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 3600 |
| ![icon_small_DocWagonCardPlatinum](assets/item-icons/icon_small_DocWagonCardPlatinum.png) | Item_Consumable_DocWagonCardPlatinum | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 7000 |
| ![icon_small_EMPGrenade](assets/item-icons/icon_small_EMPGrenade.png) | Item_Consumable_EMPGrenade | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 900 |
| ![icon_small_Exclusive_RocketLauncher](assets/item-icons/icon_small_Exclusive_RocketLauncher.png) | Item_Consumable_Exclusive_RocketLauncher | Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable | No |  |
| ![icon_small_FragGrenade](assets/item-icons/icon_small_FragGrenade.png) | Item_Consumable_FragGrenade | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 900 |
| ![icon_small_ImprovedEMPGrenade](assets/item-icons/icon_small_ImprovedEMPGrenade.png) | Item_Consumable_ImprovedEMPGrenade | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 1700 |
| ![icon_small_ImprovedFragGrenade](assets/item-icons/icon_small_ImprovedFragGrenade.png) | Item_Consumable_ImprovedFragGrenade | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2000 |
| ![icon_small_Jazz](assets/item-icons/icon_small_Jazz.png) | Item_Consumable_Jazz | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2550 |
| ![icon_small_Kamikaze](assets/item-icons/icon_small_Kamikaze.png) | Item_Consumable_Kamikaze | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 500 |
| ![icon_small_Medkit](assets/item-icons/icon_small_Medkit.png) | Item_Consumable_Medkit | ArmoredChestLootTable, FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 900 |
| ![icon_small_RocketLauncher](assets/item-icons/icon_small_RocketLauncher.png) | Item_Consumable_RocketLauncher | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 6700 |
| ![icon_small_SOTAEMPGrenade](assets/item-icons/icon_small_SOTAEMPGrenade.png) | Item_Consumable_SOTAEMPGrenade | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 6000 |
| ![icon_small_SOTAEMPGrenade](assets/item-icons/icon_small_SOTAEMPGrenade.png) | Item_Consumable_SOTAEMPGrenade2 | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2600 |
| ![icon_small_SOTAEMPGrenade](assets/item-icons/icon_small_SOTAEMPGrenade.png) | Item_Consumable_SOTAEMPGrenade3 | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 7400 |
| ![icon_small_SOTAFragGrenade](assets/item-icons/icon_small_SOTAFragGrenade.png) | Item_Consumable_SOTAFragGrenade | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 6000 |
| ![icon_small_SmedleysMinigun](assets/item-icons/icon_small_SmedleysMinigun.png) | Item_Consumable_SmedleysMinigun | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
| ![icon_small_StunGrenade](assets/item-icons/icon_small_StunGrenade.png) | Item_Consumable_SmokeGrenade | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 600 |
| ![icon_small_TaserRifle](assets/item-icons/icon_small_TaserRifle.png) | Item_Consumable_TaserRifle | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | Yes | 2700 |
| ![icon_small_TheRipper](assets/item-icons/icon_small_TheRipper.png) | Item_Consumable_TheRipper | ArmoredChestLootTable, Challenge01Phase01LootTable, Challenge01Phase02LootTable, Challenge02Phase01LootTable, Challenge02Phase02LootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |

### Valuable

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
|  | Item_BtlChip | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_CertifiedCredstickGold | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_CertifiedCredstickSilver | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_CertifiedCredstickStandard | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_CreeDidoScript | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
|  | Item_DamagedDataLock | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_FakeSinCommlink | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_FakeSinCommlinkSuperior | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_PaydataDatachip | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
|  | Item_Pearl | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_SignedMariaMercurialBootleg | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
|  | Item_SleepRegulator | FirstAidWardLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
|  | Item_SlipOrxanne | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
|  | Item_TedWilliamsBat | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |
|  | Item_TridProjector | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable | No |  |

### FacePaint

| IconImage | ItemId | SourceLootTables | SoldInShop | ShopPrice |
| --- | --- | --- | --- | --- |
| ![icon_small_Item_NativeIndianFacepaint2](assets/item-icons/icon_small_Item_NativeIndianFacepaint2.png) | Item_NativeIndianFacepaint2 | ArmoredChestLootTable, GeneralBigLootTable, GeneralSmallLootTable, OrganHarvestingLootTable | No |  |
