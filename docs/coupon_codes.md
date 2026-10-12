# Coupon Codes and Rewards

This document lists all coupon codes currently accepted by the local service coupon system and the rewards they grant.

**Package coupon rewards are applied to all existing occupied careers and future occupied careers for the account.**

The launcher reads the numeric `Code` field of the coupon response as the official `RedeemResult` enum; it does not use the response `Message` to display redemption errors. In particular, `2` means `PackageLimitReached`, `6` means `DoesNotExist`, `7` means `CouponIsAlreadyUsedByThatAccount`, and `11` means `PackageNameIsMissing`. When their reward package resolves, custom package coupons use the same successful `Ok` result (`0`) as game-defined packages; an unavailable reward package returns `PackageNameIsMissing` rather than `PackageLimitReached`.

`BONUSCOSMETICS` and `HALLOWEEN` are built-in item lists and remain resolvable when static game data or mission assets are unavailable. Other game-defined item packages continue to load from static game data.

Static data is read from the portable `<exe directory>\Resources\static-data` directory when present, otherwise from `<LocalServiceRoot>\static-data`. Streaming assets are read from the portable `<exe directory>\Resources\StreamingAssets` directory when present, otherwise from `<GameRootDir>\Shadowrun_Data\StreamingAssets`.

## Consolidated List of All Coupon Codes

Copy and claim these one by one in the launcher to activate the corresponding DLC. The contents are listed in subsequent sections of this file.

    SRO-Addon1
    SRO-Addon2
    SRO-GAME-KEY
    SRO-ITEM-CATALYST
    SRO-ITEM-CATALYST-SKUA
    SRO-ITEM-CATALYST-SPRAYDOWN
    SRO-ITEM-COMMUNITY-GOODIES1
    SRO-ITEM-GAMESROCKET
    SRO-ITEM-HAREBRAINED
    SRO-ITEM-LAGISSUES1
    SRO-ITEM-PAX-18
    SRO-ITEM-PEGASUS
    SRO-ITEM-STARTER-GEAR
    BONUSCOSMETICS
    HALLOWEEN
    SRO-KS-AWAKENED
    SRO-KS-COOP
    SRO-KS-MASTER
    SRO-ReflexRecorder
    SRO-STEAM-DLC-DELUXE

## Major AddOns

### SRO-Addon1

Infected! DLC

### SRO-Addon2

Missions DLC

## Item Package Coupons

These include exclusive backer rewards previously unavailable to most players.

### SRO-GAME-KEY

* Item_AncientsLogoNeon
* Item_BandanaSkull
* Item_barghest_white_normal
* Item_blood_deamon_mini_armored
* Item_Cyberware_Pneumatic_Arm_Beta
* Item_HeartsBoxers
* Item_KimonoMitsuhama
* Item_KimonoWuxing
* Item_NinjaGloves
* Item_RastaHatRasta
* Item_roto_drone_mini
* Item_ShortsSunrise
* Item_SneakersAmerica
* Item_SoycafShirt

### SRO-ITEM-CATALYST

* Automatics_Exclusive_IngramSmartgunSL_Tier_01
* Blade_Exclusive_SledgeSword_Tier_01
* Pistol_Exclusive_Haemmerli620s_Tier_01

### SRO-ITEM-CATALYST-SKUA

* Automatics_SkuaDmr_Coupon_Tier_05

### SRO-ITEM-CATALYST-SPRAYDOWN

* Pistol_SkSpraydown_Coupon_Tier_04

### SRO-ITEM-COMMUNITY-GOODIES1

* Item_BandanaCamoUrban
* Item_barghest_white_armored
* Item_Consumable_RocketLauncher x3

### SRO-ITEM-GAMESROCKET

* Item_Consumable_Exclusive_RocketLauncher

### SRO-ITEM-HAREBRAINED

* Item_HarebrainedShirt

### SRO-ITEM-LAGISSUES1

* Item_Consumable_BustersFlamethrower x3
* Item_Cyberware_Bioware_1_Alpha
* Item_Cyberware_Bioware_2_Alpha

### SRO-ITEM-PAX-18

* Automatics_Exclusive_Mag5Mg_Tier_01

### SRO-ITEM-PEGASUS

* Pistol_Exclusive_Haemmerli620s_Tier_01

### SRO-ITEM-STARTER-GEAR

* Item_Consumable_DocWagonCardBasic x2
* Item_Consumable_FragGrenade x2
* Item_Consumable_RocketLauncher x2
* Item_Cyberware_Wired_Reflexes_1_Beta
* Item_ElementalEssenceSpray1
* Item_PolymerCoating2

### BONUSCOSMETICS

Grants one copy of each item in the combined 52-item snapshot formerly split between this package and `UnobtainableCosmetics`. The consolidated hardcoded list retains every unique item from both packages; coupon resolution does not read or recursively scan mission assets at runtime. The retired `UnobtainableCosmetics` code is no longer accepted. Existing account entitlements recorded under that package name are mapped to `BONUSCOSMETICS` for future careers.

* Item_AncientsLogoNeon
* Item_AstralCoat
* Item_BandanaSkull
* Item_BasicMageVest
* Item_BeardBraided
* Item_BeardConchita
* Item_BeardFull
* Item_BeardStubble
* Item_BikerPants
* Item_CyberdocOverall
* Item_DefaultLowerUnderwear
* Item_DefaultUpperUnderwear
* Item_DocGloves
* Item_DocOverall
* Item_DoubleDragon1Neon
* Item_DragonHead1Neon
* Item_ElegantTop
* Item_HairDreads
* Item_HairDreadsUndercut
* Item_HairLadyLuck
* Item_HairLong2
* Item_HairMohawkLimp
* Item_HairMohawkSideburns
* Item_HairPancake
* Item_HarebrainedShirt
* Item_HeartsBoxers
* Item_HipsterBoots
* Item_HornsBasic
* Item_KimonoMitsuhama
* Item_KimonoTop
* Item_KimonoWuxing
* Item_LeatherBoots
* Item_LeatherCoat
* Item_MagicalLongsleeve
* Item_NinjaGloves
* Item_Pack1HackerTop
* Item_Pack1MageCoat
* Item_Pack3Corset
* Item_Pack4BagBoots
* Item_Pack4HospitalGown
* Item_PuffTie
* Item_PuffTieVest
* Item_RastaHatRasta
* Item_RunnerLongsleeve
* Item_ShortsSunrise
* Item_Sideburns
* Item_SneakersAmerica
* Item_SoycafShirt
* Item_StreetCorset
* Item_SuitJacket
* Item_Tribal2Neon
* Item_WesternTop

### HALLOWEEN

Grants one copy of the Halloween pet, **Flying pumpkin**.

* Item_halloweenpet_01

### SRO-KS-AWAKENED

* Item_BackerTalisman
* Item_Pack2BackerShirt

### SRO-KS-COOP

* Item_Pack2BackerShirt

### SRO-KS-MASTER

* Item_BackerTalisman
* Item_CertifiedCredstickStandard
* Item_Consumable_DocWagonCardBasic x2
* Item_Consumable_FragGrenade x2
* Item_Consumable_RocketLauncher x2
* Item_Consumable_SmokeGrenade x2
* Item_Cyberware_Cultural_Bioware_Reflex_Recorder_1_Alpha
* Item_Cyberware_Wired_Reflexes_1_Beta
* Item_DoubleDragon1Neon
* Item_DragonHead1Neon
* Item_ElementalEssenceSpray1
* Item_Pack2BackerShirt
* Item_Pack4_HoloDragon
* Item_PolymerCoating2

### SRO-ReflexRecorder

* Item_Cyberware_Cultural_Bioware_Reflex_Recorder_1_Alpha

### SRO-STEAM-DLC-DELUXE

* Item_Consumable_DocWagonCardBasic x2
* Item_Consumable_FragGrenade x2
* Item_Consumable_RocketLauncher x2
* Item_Cyberware_Wired_Reflexes_1_Beta
* Item_ElementalEssenceSpray1
* Item_PolymerCoating2
