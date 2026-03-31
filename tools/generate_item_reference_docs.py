#!/usr/bin/env python3
"""Generate markdown reference docs for weapons, cyberware, armor, and tactical items.

Data source:
- server/static-data/metagameplay.json
- server/static-data/weapons.json
- server/static-data/ids.json
"""

from __future__ import annotations

import json
import re
from collections import defaultdict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
STATIC_DATA = ROOT / "server" / "static-data"
DOCS_DIR = ROOT / "docs"

METAGAMEPLAY_PATH = STATIC_DATA / "metagameplay.json"
WEAPONS_PATH = STATIC_DATA / "weapons.json"
IDS_PATH = STATIC_DATA / "ids.json"
GLOBALS_PATH = STATIC_DATA / "globals.json"

WEAPONS_DOC = DOCS_DIR / "items_weapons_reference.md"
CYBERWARE_DOC = DOCS_DIR / "items_cyberware_reference.md"
ARMOR_DOC = DOCS_DIR / "items_armor_reference.md"
TACTICAL_DOC = DOCS_DIR / "items_tactical_reference.md"

CYBERWARE_ITEM_TYPES = {
    196826: "Legs",
    196827: "Torso",
    196824: "Head",
    196825: "Arms",
}

ARMOR_ITEM_TYPE = 196821
CONSUMABLE_ITEM_TYPE = 196820


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def id_label(ids_map: dict[str, str], raw_id: int | str | None) -> str:
    if raw_id is None:
        return ""
    return ids_map.get(str(raw_id), str(raw_id))


def short_stat_name(ids_map: dict[str, str], stat_id: int | str) -> str:
    full = id_label(ids_map, stat_id)
    return full.split(".")[-1]


def infer_tier(item_id: str) -> int | None:
    patterns = [
        r"_Tier_(\d+)",
        r"_Q(\d+)",
        r"_(\d+)_(?:Alpha|Beta|Gamma|Delta|Omega)$",
        r"_(\d+)$",
        r"(\d+)$",
    ]
    for pattern in patterns:
        m = re.search(pattern, item_id)
        if m:
            try:
                return int(m.group(1))
            except ValueError:
                return None
    return None


def item_type_from_id(item_id: str) -> str:
    m = re.match(r"([A-Za-z0-9]+)_", item_id)
    if m:
        return m.group(1)
    return "Other"


def table(headers: list[str], rows: list[list[str]]) -> str:
    lines = [
        "| " + " | ".join(headers) + " |",
        "| " + " | ".join(["---"] * len(headers)) + " |",
    ]
    for row in rows:
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines)


def fmt_value(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        text = f"{value:.4f}".rstrip("0").rstrip(".")
        return text if text else "0"
    return str(value)


def collect_status_values(status_modifiers: list[dict[str, Any]] | None) -> dict[int, Any]:
    out: dict[int, Any] = {}
    for mod in status_modifiers or []:
        out[int(mod["ID"])] = mod.get("Modifier", 0)
    return out


def _walk(obj: Any):
    if isinstance(obj, dict):
        yield obj
        for value in obj.values():
            for nested in _walk(value):
                yield nested
    elif isinstance(obj, list):
        for value in obj:
            for nested in _walk(value):
                yield nested


def extract_skill_stats(skill: dict[str, Any], ids_map: dict[str, str]) -> dict[str, str]:
    stats: dict[str, str] = {}

    damage_types: list[str] = []
    relative_damage_values: list[float] = []
    range_values: list[float] = []
    radius_values: list[float] = []
    heal_amount_values: list[float] = []
    crit_chance_values: list[float] = []
    status_effect_ids: list[int] = []

    for node in _walk(skill):
        if not isinstance(node, dict):
            continue

        if "Damage" in node and isinstance(node["Damage"], str):
            damage_types.append(node["Damage"])

        if "ModifiedRelativeDamage" in node:
            value = node["ModifiedRelativeDamage"]
            if isinstance(value, (int, float)):
                relative_damage_values.append(float(value))

        if "ModifiedRange" in node:
            value = node["ModifiedRange"]
            if isinstance(value, (int, float)):
                range_values.append(float(value))

        if "Radius" in node:
            value = node["Radius"]
            if isinstance(value, (int, float)):
                radius_values.append(float(value))

        if "HealAmount" in node:
            value = node["HealAmount"]
            if isinstance(value, (int, float)):
                heal_amount_values.append(float(value))

        if "ModifiedChanceToCrit" in node:
            value = node["ModifiedChanceToCrit"]
            if isinstance(value, (int, float)):
                crit_chance_values.append(float(value))

        if "StatusEffectId" in node:
            value = node["StatusEffectId"]
            if isinstance(value, int):
                status_effect_ids.append(value)

    if damage_types:
        stats["DamageType"] = ", ".join(sorted(set(damage_types)))

    if relative_damage_values:
        stats["RelativeDamage"] = ", ".join(fmt_value(v) for v in sorted(set(relative_damage_values)))

    if range_values:
        stats["Range"] = ", ".join(fmt_value(v) for v in sorted(set(range_values)))

    if radius_values:
        stats["AreaRadius"] = ", ".join(fmt_value(v) for v in sorted(set(radius_values)))

    if heal_amount_values:
        stats["HealAmount"] = ", ".join(fmt_value(v) for v in sorted(set(heal_amount_values)))

    if crit_chance_values:
        non_zero = [v for v in crit_chance_values if v != 0]
        if non_zero:
            stats["CritChanceModifier"] = ", ".join(fmt_value(v) for v in sorted(set(non_zero)))

    if status_effect_ids:
        effect_labels = []
        for effect_id in sorted(set(status_effect_ids)):
            label = id_label(ids_map, effect_id)
            if label == str(effect_id):
                effect_labels.append(str(effect_id))
            else:
                effect_labels.append(f"{label} ({effect_id})")
        stats["StatusEffects"] = ", ".join(effect_labels)

    return stats


def build_weapons_doc(
    ids_map: dict[str, str],
    weapon_item_defs: list[dict[str, Any]],
    weapon_data_map: dict[str, dict[str, Any]],
) -> str:
    by_type: dict[str, list[dict[str, Any]]] = defaultdict(list)

    for item in weapon_item_defs:
        item_id = item["Id"]
        weapon_ref = str(item.get("MissionWeaponReference", ""))
        weapon_data = weapon_data_map.get(weapon_ref, {})
        stats = collect_status_values(weapon_data.get("StatusValueModifiers"))

        by_type[item_type_from_id(item_id)].append(
            {
                "tier": infer_tier(item_id),
                "name": item.get("Name") or "",
                "id": item_id,
                "stats": stats,
            }
        )

    lines = [
        "# Weapons Reference",
        "",
        "Generated from `server/static-data/metagameplay.json`, `server/static-data/weapons.json`, and `server/static-data/ids.json`.",
        "",
    ]

    for weapon_type in sorted(by_type.keys()):
        entries = by_type[weapon_type]
        stat_ids = sorted({sid for entry in entries for sid in entry["stats"].keys()})

        headers = ["Tier", "Name", "Id"] + [short_stat_name(ids_map, sid) for sid in stat_ids]
        rows: list[list[str]] = []

        entries.sort(key=lambda x: (x["tier"] is None, x["tier"] if x["tier"] is not None else 999, x["name"], x["id"]))

        for entry in entries:
            row = [
                "" if entry["tier"] is None else str(entry["tier"]),
                fmt_value(entry["name"]),
                entry["id"],
            ]
            row.extend(fmt_value(entry["stats"].get(sid, 0)) for sid in stat_ids)
            rows.append(row)

        lines.extend([
            f"## {weapon_type}",
            "",
            table(headers, rows),
            "",
        ])

    return "\n".join(lines).rstrip() + "\n"


def stat_columns_for_items(items: list[dict[str, Any]]) -> list[int]:
    all_stats = sorted({sid for item in items for sid in item["stats"].keys()})
    non_zero_stats: list[int] = []
    for sid in all_stats:
        if any(float(item["stats"].get(sid, 0)) != 0.0 for item in items):
            non_zero_stats.append(sid)
    return non_zero_stats


def build_cyberware_doc(ids_map: dict[str, str], equipment_defs: list[dict[str, Any]]) -> str:
    lines = [
        "# Cyberware Reference",
        "",
        "Generated from `server/static-data/metagameplay.json` and `server/static-data/ids.json`.",
        "",
    ]

    for type_id, slot_name in [(196826, "Legs"), (196827, "Torso"), (196824, "Head"), (196825, "Arms")]:
        entries: list[dict[str, Any]] = []
        for item in equipment_defs:
            if int(item.get("ItemTypeId", -1)) != type_id:
                continue
            item_id = item["Id"]
            entries.append(
                {
                    "tier": infer_tier(item_id),
                    "name": item.get("Name") or "",
                    "id": item_id,
                    "stats": collect_status_values(item.get("StatusValueModifiers")),
                }
            )

        entries.sort(key=lambda x: (x["tier"] is None, x["tier"] if x["tier"] is not None else 999, x["name"], x["id"]))
        stat_ids = stat_columns_for_items(entries)

        headers = ["Tier", "Name", "Id"] + [short_stat_name(ids_map, sid) for sid in stat_ids]
        rows: list[list[str]] = []

        for entry in entries:
            row = [
                "" if entry["tier"] is None else str(entry["tier"]),
                entry["name"],
                entry["id"],
            ]
            row.extend(fmt_value(entry["stats"].get(sid, 0)) for sid in stat_ids)
            rows.append(row)

        lines.extend([
            f"## {slot_name}",
            "",
            table(headers, rows),
            "",
        ])

    return "\n".join(lines).rstrip() + "\n"


def build_armor_doc(ids_map: dict[str, str], equipment_defs: list[dict[str, Any]]) -> str:
    entries: list[dict[str, Any]] = []
    for item in equipment_defs:
        if int(item.get("ItemTypeId", -1)) != ARMOR_ITEM_TYPE:
            continue
        item_id = item["Id"]
        entries.append(
            {
                "tier": infer_tier(item_id),
                "name": item.get("Name") or "",
                "id": item_id,
                "stats": collect_status_values(item.get("StatusValueModifiers")),
            }
        )

    entries.sort(key=lambda x: (x["tier"] is None, x["tier"] if x["tier"] is not None else 999, x["name"], x["id"]))
    stat_ids = stat_columns_for_items(entries)

    headers = ["Tier", "Name", "Id"] + [short_stat_name(ids_map, sid) for sid in stat_ids]
    rows: list[list[str]] = []
    for entry in entries:
        row = [
            "" if entry["tier"] is None else str(entry["tier"]),
            entry["name"],
            entry["id"],
        ]
        row.extend(fmt_value(entry["stats"].get(sid, 0)) for sid in stat_ids)
        rows.append(row)

    lines = [
        "# Armor Reference",
        "",
        "Generated from `server/static-data/metagameplay.json` and `server/static-data/ids.json`.",
        "",
        table(headers, rows),
        "",
    ]

    return "\n".join(lines)


def tactical_family(item_id: str) -> str:
    name = item_id
    for prefix in ["Item_Consumable_", "Item_"]:
        if name.startswith(prefix):
            name = name[len(prefix) :]
            break

    if name.startswith("Exclusive_"):
        name = name[len("Exclusive_") :]

    quality_prefixes = [
        "Basic",
        "Improved",
        "SOTA",
        "Milspec",
        "Advanced",
        "Enhanced",
        "Superior",
        "Prototype",
    ]
    changed = True
    while changed:
        changed = False
        for prefix in quality_prefixes:
            if name.startswith(prefix):
                name = name[len(prefix) :]
                changed = True

    quality_suffixes = [
        "Basic",
        "Improved",
        "SOTA",
        "Milspec",
        "Advanced",
        "Enhanced",
        "Superior",
        "Prototype",
        "Gold",
        "Platinum",
    ]
    changed = True
    while changed:
        changed = False
        for suffix in quality_suffixes:
            if name.endswith(suffix):
                name = name[: -len(suffix)]
                changed = True

    name = re.sub(r"\d+$", "", name)
    name = name.rstrip("_-")
    return name or item_id


def tactical_category(item_id: str, family: str) -> str:
    if item_id in {
        "Item_Consumable_Medkit",
        "Item_Consumable_DocWagonCardBasic",
        "Item_Consumable_DocWagonCardGold",
        "Item_Consumable_DocWagonCardPlatinum",
    }:
        return "Healing Items"

    if item_id in {
        "Item_Consumable_InteractionHacking",
        "Item_Consumable_InteractionLockpicking",
        "Item_Consumable_InteractionDemolish",
        "Item_Consumable_InteractionOrganHarvest",
    }:
        return "Interactions"

    return family


def build_tactical_doc(
    ids_map: dict[str, str],
    consumable_defs: list[dict[str, Any]],
    activities: dict[str, dict[str, Any]],
) -> str:
    entries = []
    for item in consumable_defs:
        item_id = item["Id"]
        skill_id = item.get("SkillId")
        family = tactical_family(item_id)
        entries.append(
            {
                "tier": infer_tier(item_id),
                "id": item_id,
                "name": item.get("Name") or "",
                "description": item.get("Description") or "",
                "skill_id": skill_id,
                "skill_name": id_label(ids_map, skill_id) if skill_id is not None else "",
                "sell_price": item.get("SellPrice"),
                "max_stack": item.get("MaxStacksize"),
                "icon": item.get("Icon") or "",
                "family": family,
                "category": tactical_category(item_id, family),
                "skill_stats": extract_skill_stats(activities.get(str(skill_id), {}), ids_map)
                if skill_id is not None
                else {},
            }
        )

    entries.sort(
        key=lambda x: (
            x["category"].lower(),
            x["tier"] is None,
            x["tier"] if x["tier"] is not None else 999,
            x["id"],
        )
    )

    lines = [
        "# Tactical Items Reference",
        "",
        "Generated from `server/static-data/metagameplay.json` and `server/static-data/ids.json`.",
        "",
    ]

    current_category = None
    for entry in entries:
        stat_keys = [
            "DamageType",
            "RelativeDamage",
            "Range",
            "AreaRadius",
            "HealAmount",
            "StatusEffects",
            "CritChanceModifier",
        ]
        stat_parts = []
        for key in stat_keys:
            value = entry["skill_stats"].get(key)
            if value:
                stat_parts.append(f"{key}={value}")
        skill_stats_line = "; ".join(stat_parts) if stat_parts else "None parsed"

        if entry["category"] != current_category:
            current_category = entry["category"]
            lines.extend([f"## {current_category}", ""])
            headers = [
                "Tier",
                "Id",
                "Name",
                "Description",
                "SkillId",
                "SkillIdentifier",
                "SkillStats",
                "SellPrice",
                "MaxStacksize",
                "Icon",
            ]
            lines.append("| " + " | ".join(headers) + " |")
            lines.append("| " + " | ".join(["---"] * len(headers)) + " |")

        row = [
            fmt_value(entry["tier"]),
            entry["id"],
            entry["name"],
            entry["description"],
            fmt_value(entry["skill_id"]),
            entry["skill_name"],
            skill_stats_line,
            fmt_value(entry["sell_price"]),
            fmt_value(entry["max_stack"]),
            entry["icon"],
        ]
        lines.append("| " + " | ".join(row) + " |")

    return "\n".join(lines).rstrip() + "\n"


def main() -> None:
    ids_map = load_json(IDS_PATH)
    metagameplay = load_json(METAGAMEPLAY_PATH)
    weapons = load_json(WEAPONS_PATH)
    globals_data = load_json(GLOBALS_PATH)

    logic = metagameplay["Components"][1]
    equipment_defs = logic["EquipmentItemDefinitions"]
    weapon_item_defs = logic["WeaponItemDefinitions"]
    consumable_defs = logic["ConsumableItemDefinitions"]
    weapon_data_map = weapons["Components"][0]["Weapons"]
    activity_component = next(
        c
        for c in globals_data["Components"]
        if isinstance(c, dict) and "ActivityData" in c.get("TypeName", "")
    )
    activities = activity_component.get("Activities", {})

    WEAPONS_DOC.write_text(
        build_weapons_doc(ids_map, weapon_item_defs, weapon_data_map),
        encoding="utf-8",
    )
    CYBERWARE_DOC.write_text(
        build_cyberware_doc(ids_map, equipment_defs),
        encoding="utf-8",
    )
    ARMOR_DOC.write_text(
        build_armor_doc(ids_map, equipment_defs),
        encoding="utf-8",
    )
    TACTICAL_DOC.write_text(
        build_tactical_doc(ids_map, consumable_defs, activities),
        encoding="utf-8",
    )

    print("Generated:")
    print(f"- {WEAPONS_DOC.relative_to(ROOT)}")
    print(f"- {CYBERWARE_DOC.relative_to(ROOT)}")
    print(f"- {ARMOR_DOC.relative_to(ROOT)}")
    print(f"- {TACTICAL_DOC.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
