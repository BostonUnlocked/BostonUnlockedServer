# Hit Chance Explained

This guide explains how **chance to hit** is calculated in combat.

## 1) Core idea

The game builds hit chance in two stages:

1. Build the attacker's base chance from accuracy/range math.
2. Apply target factors (cover, dodge/evasion, skill effects).

All internal values are decimals (`0.75` = `75%`).

## 2) Base hit chance (before target defenses)

```text
BaseHit = max(0, MaxCtH + PointBlankBonus - RangePenalty)
```

Where:

- `MaxCtH = attacker MaximumChanceToHit + temporary source accuracy bonuses`
- `PointBlankBonus` rewards being closer than point-blank range:

```text
If PointBlankRange <= 1.01 or InitialMaxCtH <= 0.01: 0
Else if distance >= PointBlankRange: 0
Else: (PointBlankRange - distance) * ((1 - InitialMaxCtH) / (PointBlankRange - 1))
```

- `RangePenalty` applies when beyond effective range:

```text
EffectiveRange = attacker Range + temporary source range bonuses
RangePenalty = ((max(0, distance - EffectiveRange) / 10) ^ 1.5)
```

`distance` uses the game's custom grid distance function. Adjacent attacks are treated as no range penalty.

## 3) Target defenses and hit modifiers

For normal direct attacks:

```text
FinalHit = max(0, BaseHit - CoverPenalty + TargetToHitModifier)
```

### Cover penalties

- **No cover**: `0.00`
- **Half cover**: `0.30` (−30 percentage points)
- **Full cover**: `0.50` (−50 percentage points)

Important:

- Adjacent attacker/target gives **no cover benefit**.
- If multiple cover directions qualify, the game uses the **strongest** one.

### Dodge / evasion

Enemy dodge/evasion is represented by `TargetToHitModifier`:

- Negative values reduce your hit chance (example: `-0.15` = −15 points).
- Positive values increase chance to be hit.
- It can come from traits, status effects, and skill calculations.

## 4) Other factors that can change hit chance

- Some skills add extra target-space modifiers (`ModifiedChanceToHitEnemy`), often penalties like `-0.50`.
- **Indirect attacks** use a different base:

```text
BaseHit = 1.0
FinalHit = max(0, 1.0 - CoverPenalty + TargetToHitModifier)
```

- Some attacks explicitly ignore cover and clamp to `[0, 1]`.

## 5) Final roll and hidden player bonus

At hit roll time:

```text
RollChance = FinalHit
If attacker is player-controlled (not AI): RollChance += 0.10
Hit if random(0..1) <= RollChance
```

Notes:

- Normal direct-hit path does not hard-cap at `1.0` before the roll, so values above 100% are effectively guaranteed hits.
- UI values are shown as percentages and rounded to nearest whole number.

## 6) Quick examples

### Example A: Rifle shot at medium range vs half cover

- `MaxCtH = 0.80`
- `PointBlankBonus = 0.00`
- `RangePenalty = 0.05`
- `CoverPenalty = 0.30`
- `TargetToHitModifier = -0.10`

```text
BaseHit = 0.80 + 0.00 - 0.05 = 0.75
FinalHit = 0.75 - 0.30 - 0.10 = 0.35 (35%)
Player-controlled attacker roll bonus: 45%
```

### Example B: Close-range flank with no cover

- `MaxCtH = 0.85`
- `PointBlankBonus = 0.07`
- `RangePenalty = 0.00`
- `CoverPenalty = 0.00`
- `TargetToHitModifier = 0.00`

```text
BaseHit = 0.92
FinalHit = 0.92 (92%)
Player-controlled attacker roll bonus: 102% (effectively guaranteed hit)
```

## 7) Technical source references

- `GameLogic/Calculations/ChanceToHitTargetPositionCalculation.cs`
- `GameLogic/EntityCalculations/AnAttackChanceToHitCalculation.cs`
- `GameLogic/EntityCalculations/DirectAttackChanceToHitCalculation.cs`
- `GameLogic/EntityCalculations/IndirectAttackChanceToHitCalculation.cs`
- `Gameworld/Map/HalfCover.cs`, `FullCover.cs`, `NoCover.cs`, `CoverSystem.cs`
- `GameLogic/EntityCalculations/ResolveHitQualityCalculation.cs`
