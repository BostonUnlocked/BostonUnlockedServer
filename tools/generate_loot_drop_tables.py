#!/usr/bin/env python3
"""Generate item-centric loot reference markdown with source tables and shop info.

Sources:
- server/static-data/serverData.json
- server/static-data/metagameplay.json
- server/static-data/weapons.json
- server/static-data/ids.json
- server/StreamingAssets/levels/*/mapdata.json

Output:
- docs/loot_drop_tables_reference.md
"""

from __future__ import annotations

import json
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
STATIC_DATA = ROOT / "server" / "static-data"
SERVER_DATA_PATH = STATIC_DATA / "serverData.json"
METAGAMEPLAY_PATH = STATIC_DATA / "metagameplay.json"
WEAPONS_PATH = STATIC_DATA / "weapons.json"
IDS_PATH = STATIC_DATA / "ids.json"
LEVELS_DIR = ROOT / "server" / "StreamingAssets" / "levels"
OUT_PATH = ROOT / "docs" / "loot_drop_tables_reference.md"
ICON_DIR = ROOT / "docs" / "assets" / "item-icons"

COSMETIC_TYPE_NAMES = {
    "UpperBody",
    "LowerBody",
    "Hands",
    "Boots",
    "UpperUnderwear",
    "LowerUnderwear",
    "Glasses",
    "Head",
    "Skin",
    "Tattoo",
}


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def sanitize_cell(value: Any) -> str:
    text = "" if value is None else str(value)
    return text.replace("|", "\\|").replace("\n", "<br>")


def md_table(headers: list[str], rows: list[list[Any]]) -> str:
    lines = [
        "| " + " | ".join(sanitize_cell(h) for h in headers) + " |",
        "| " + " | ".join(["---"] * len(headers)) + " |",
    ]
    for row in rows:
        lines.append("| " + " | ".join(sanitize_cell(cell) for cell in row) + " |")
    return "\n".join(lines)


def short_type(type_name: str | None) -> str:
    if not type_name:
        return "Unknown"
    return type_name.split(",", 1)[0].split(".")[-1]


def icon_basename(icon_path: str | None) -> str:
    if not icon_path:
        return ""
    clean = icon_path.replace("\\", "/")
    base = clean.rsplit("/", 1)[-1]
    if "." in base:
        base = base.rsplit(".", 1)[0]
    return base


def load_icon_relpath_by_name() -> dict[str, str]:
    if not ICON_DIR.exists():
        return {}
    out: dict[str, str] = {}
    for file in ICON_DIR.glob("*.png"):
        out[file.stem] = f"assets/item-icons/{file.name}"
    return out


def icon_markdown(item_id: str, item_icon_paths: dict[str, str], icon_relpath_by_name: dict[str, str]) -> str:
    icon_path = item_icon_paths.get(item_id, "")
    base = icon_basename(icon_path)
    relpath = icon_relpath_by_name.get(base, "")
    if not relpath:
        return ""
    return f"![{base}]({relpath})"


def normalize_itemtype_name(raw: str) -> str:
    if not raw:
        return ""
    tail = raw.rsplit(".", 1)[-1]
    prefix = "Itemtype_"
    if tail.startswith(prefix):
        return tail[len(prefix) :]
    return tail


def classify_equipment_subtype(item_type_name: str) -> str:
    if not item_type_name:
        return "Equipment"

    if item_type_name.startswith("Cyberware_"):
        return f"{item_type_name.replace('_', ' ')}"
    if item_type_name == "Armor":
        return "Armor"
    if item_type_name == "Pet":
        return "Pets"
    if item_type_name in COSMETIC_TYPE_NAMES:
        return f"Cosmetic {item_type_name}"

    return item_type_name.replace("_", " ")


def classify_weapon_subtype(item_id: str) -> str:
    first = item_id.split("_", 1)[0]
    if not first:
        return "Weapon Other"
    return first


def load_item_metadata() -> tuple[dict[str, str], dict[str, str]]:
    """Returns (item_icon_paths, item_subtype_by_id)."""
    metagameplay = load_json(METAGAMEPLAY_PATH)
    weapons = load_json(WEAPONS_PATH)
    ids_map = load_json(IDS_PATH)

    logic = metagameplay["Components"][1]
    weapon_defs = logic.get("WeaponItemDefinitions") or []
    equipment_defs = logic.get("EquipmentItemDefinitions") or []
    consumable_defs = logic.get("ConsumableItemDefinitions") or []
    valuable_defs = logic.get("ValuableItemDefinitions") or []

    weapon_data_map = weapons["Components"][0]["Weapons"]

    item_icon_paths: dict[str, str] = {}
    item_subtype_by_id: dict[str, str] = {}

    for item in weapon_defs:
        item_id = item.get("Id")
        if not isinstance(item_id, str) or not item_id:
            continue

        icon = item.get("Icon") or ""
        if not icon:
            weapon_ref = str(item.get("MissionWeaponReference", ""))
            weapon_data = weapon_data_map.get(weapon_ref, {})
            visual = weapon_data.get("WeaponData") or {}
            icon = (visual.get("SmallIcon") or visual.get("Icon") or "")

        item_icon_paths[item_id] = icon
        item_subtype_by_id[item_id] = classify_weapon_subtype(item_id)

    for item in equipment_defs:
        item_id = item.get("Id")
        if not isinstance(item_id, str) or not item_id:
            continue

        item_icon_paths[item_id] = item.get("Icon") or ""

        item_type_id = str(item.get("ItemTypeId", ""))
        raw_item_type = ids_map.get(item_type_id, "")
        item_type_name = normalize_itemtype_name(raw_item_type)
        item_subtype_by_id[item_id] = classify_equipment_subtype(item_type_name)

    for item in consumable_defs:
        item_id = item.get("Id")
        if not isinstance(item_id, str) or not item_id:
            continue
        item_icon_paths[item_id] = item.get("Icon") or ""
        item_subtype_by_id[item_id] = "Tactical"

    for item in valuable_defs:
        item_id = item.get("Id")
        if not isinstance(item_id, str) or not item_id:
            continue
        item_icon_paths[item_id] = item.get("Icon") or ""
        item_subtype_by_id[item_id] = "Valuable"

    return item_icon_paths, item_subtype_by_id


def load_shop_price_map() -> dict[str, set[int]]:
    metagameplay = load_json(METAGAMEPLAY_PATH)
    shop_comp = metagameplay["Components"][0]
    shop_lists = shop_comp.get("ShopListDefinitions") or []

    prices_by_item: dict[str, set[int]] = defaultdict(set)
    for shop in shop_lists:
        entries = shop.get("ShopListEntries") or []
        for entry in entries:
            item_id = entry.get("ItemId")
            price = entry.get("Price")
            if not isinstance(item_id, str) or not item_id:
                continue
            if isinstance(price, int):
                prices_by_item[item_id].add(price)

    return dict(prices_by_item)


def build_flattened_item_set(
    table_id: str,
    tables_by_id: dict[str, dict[str, Any]],
    path: list[str] | None = None,
) -> tuple[set[str], list[str]]:
    if path is None:
        path = []

    if table_id in path:
        cycle_path = " -> ".join(path + [table_id])
        return set(), [f"Cycle detected: {cycle_path}"]

    table = tables_by_id.get(table_id)
    if table is None:
        return set(), [f"Missing table reference: {table_id}"]

    out: set[str] = set()
    warnings: list[str] = []

    for entry in table.get("Entries") or []:
        ref = entry.get("Reference") or {}
        ref_type = short_type(ref.get("TypeName"))
        ref_id = ref.get("Reference")

        if ref_type == "LogicItemLootReference":
            if isinstance(ref_id, str) and ref_id:
                out.add(ref_id)
            else:
                warnings.append(f"Invalid item reference in {table_id}: {ref}")
        elif ref_type == "LootTableLootReference":
            if isinstance(ref_id, str) and ref_id:
                child_items, child_warnings = build_flattened_item_set(ref_id, tables_by_id, path + [table_id])
                out.update(child_items)
                warnings.extend(child_warnings)
            else:
                warnings.append(f"Invalid table reference in {table_id}: {ref}")
        else:
            warnings.append(f"Unsupported reference type in {table_id}: {ref_type}")

    return out, warnings


def extract_loot_roots_from_mapdata(mapdata_path: Path) -> set[str]:
    data = load_json(mapdata_path)
    out: set[str] = set()

    def walk(obj: Any) -> None:
        if isinstance(obj, dict):
            if obj.get("Id") == "LootTable" and isinstance(obj.get("Value"), str):
                out.add(obj["Value"])
            for value in obj.values():
                walk(value)
        elif isinstance(obj, list):
            for value in obj:
                walk(value)

    walk(data)
    return out


def collect_level_loot_roots() -> dict[str, set[str]]:
    level_roots: dict[str, set[str]] = {}
    if not LEVELS_DIR.exists():
        return level_roots

    for level_dir in sorted(LEVELS_DIR.iterdir()):
        if not level_dir.is_dir():
            continue
        mapdata_path = level_dir / "mapdata.json"
        if not mapdata_path.exists():
            continue

        roots = extract_loot_roots_from_mapdata(mapdata_path)
        if roots:
            level_roots[level_dir.name] = roots

    return level_roots


def subtype_sort_key(name: str) -> tuple[int, str]:
    if name in {
        "Pistol",
        "Shotgun",
        "Automatics",
        "Blade",
        "Club",
        "Conjuring",
        "Hacking",
        "Rigging",
        "Spellcasting",
    }:
        return (0, name)
    if name.startswith("Cyberware"):
        return (1, name)
    if name.startswith("Cosmetic"):
        return (2, name)
    if name == "Armor":
        return (3, name)
    if name == "Pets":
        return (4, name)
    if name == "Tactical":
        return (5, name)
    if name == "Valuable":
        return (6, name)
    return (7, name)


def main() -> None:
    server_data = load_json(SERVER_DATA_PATH)
    item_icon_paths, item_subtype_by_id = load_item_metadata()
    shop_prices_by_item = load_shop_price_map()
    icon_relpath_by_name = load_icon_relpath_by_name()

    loot_collection = next(
        c
        for c in server_data["Components"]
        if isinstance(c, dict) and "LootTableDefinitionCollection" in (c.get("TypeName") or "")
    )
    tables = loot_collection.get("LootTables") or []
    tables_by_id = {t["Id"]: t for t in tables if "Id" in t}

    level_roots = collect_level_loot_roots()

    # Flatten all level mapdata loot roots into item membership and source tables.
    all_roots: set[str] = set()
    for roots in level_roots.values():
        all_roots.update(roots)

    all_warnings: Counter[str] = Counter()

    # Aggregate each item only once with all source loot tables.
    item_sources: dict[str, set[str]] = defaultdict(set)

    for root_table in sorted(all_roots):
        items, warnings = build_flattened_item_set(root_table, tables_by_id)
        for warning in warnings:
            all_warnings[warning] += 1

        for item_id in items:
            item_sources[item_id].add(root_table)

    items_by_subtype: dict[str, list[str]] = defaultdict(list)
    for item_id in sorted(item_sources.keys()):
        subtype = item_subtype_by_id.get(item_id, "Unknown")
        items_by_subtype[subtype].append(item_id)

    lines: list[str] = [
        "# Loot Drop Tables Reference",
        "",
        "Generated from `server/static-data/serverData.json`, `server/static-data/metagameplay.json`, and `server/StreamingAssets/levels/*/mapdata.json`.",
        "",
        "## Assumptions",
        "",
        "- Each item appears once, in exactly one item-type table.",
        "- `SourceLootTables` are mapdata-linked loot roots where that item is present after recursive flattening.",
        "- `SoldInShop` and `ShopPrice` are derived from `ShopListDefinitions` in `metagameplay.json`.",
        "- This reference is membership-based (drop availability), not weighted chance percentages.",
        "",
        "## Coverage",
        "",
        f"- Levels with loot roots: {len(level_roots)}",
        f"- Root loot tables used: {len(all_roots)}",
        f"- Unique items in mission loot: {len(item_sources)}",
        f"- Loot table definitions parsed: {len(tables)}",
        "",
        "## Items By Type",
        "",
    ]

    for subtype in sorted(items_by_subtype.keys(), key=subtype_sort_key):
        lines.extend([f"### {subtype}", ""])

        rows: list[list[str]] = []
        for item_id in items_by_subtype[subtype]:
            source_tables = ", ".join(sorted(item_sources[item_id]))
            prices = sorted(shop_prices_by_item.get(item_id, set()))
            sold_in_shop = "Yes" if prices else "No"
            price_text = ", ".join(str(price) for price in prices)
            rows.append(
                [
                    icon_markdown(item_id, item_icon_paths, icon_relpath_by_name),
                    item_id,
                    source_tables,
                    sold_in_shop,
                    price_text,
                ]
            )

        lines.append(md_table(["IconImage", "ItemId", "SourceLootTables", "SoldInShop", "ShopPrice"], rows))
        lines.append("")

    if all_warnings:
        lines.extend(["## Flatten Warnings", ""])
        warning_rows = [[message, str(count)] for message, count in sorted(all_warnings.items())]
        lines.append(md_table(["Warning", "Occurrences"], warning_rows))
        lines.append("")

    OUT_PATH.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")

    print(f"Generated: {OUT_PATH.relative_to(ROOT)}")
    print(f"Subtype tables: {len(items_by_subtype)}")
    print(f"Levels with loot roots: {len(level_roots)}")
    print(f"Root loot tables used: {len(all_roots)}")
    print(f"Unique items: {len(item_sources)}")
    print(f"Icons mapped: {len(icon_relpath_by_name)}")


if __name__ == "__main__":
    main()
