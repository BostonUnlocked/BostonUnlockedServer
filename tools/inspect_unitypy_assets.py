from __future__ import annotations

import argparse
import os
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import UnityPy


DEFAULT_ASSET_PATH = r"d:/SteamLibrary/steamapps/common/ShadowrunChronicles/Shadowrun_Data/resources.assets"


@dataclass(frozen=True)
class TextAssetHit:
    asset_file: str
    name: str
    matched_needles: tuple[str, ...]
    size_bytes: int


def _to_bytes_maybe(value) -> bytes:
    if value is None:
        return b""
    if isinstance(value, (bytes, bytearray)):
        return bytes(value)
    if isinstance(value, str):
        return value.encode("utf-8", errors="replace")
    return str(value).encode("utf-8", errors="replace")


def _read_textasset_bytes(text_asset) -> bytes:
    for attr in ("script", "m_Script", "m_ScriptBytes", "m_ScriptString", "data", "m_Data"):
        if hasattr(text_asset, attr):
            data = getattr(text_asset, attr)
            b = _to_bytes_maybe(data)
            if b:
                return b
    return b""


def _iter_textassets(asset_path: str) -> Iterable[tuple[str, object, bytes]]:
    env = UnityPy.load(asset_path)
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        ta = obj.read()
        name = getattr(ta, "name", "<unnamed>")
        payload = _read_textasset_bytes(ta)
        yield name, ta, payload


def _find_needles(payload: bytes, needles: list[str]) -> tuple[str, ...]:
    if not payload:
        return ()
    hits: list[str] = []
    for needle in needles:
        try:
            if needle.encode("ascii") in payload:
                hits.append(needle)
                continue
        except UnicodeEncodeError:
            pass
        if needle.encode("utf-8", errors="ignore") in payload:
            hits.append(needle)
    return tuple(sorted(set(hits)))


def inspect_asset(asset_path: str, needles: list[str], dump_dir: str | None) -> list[TextAssetHit]:
    env = UnityPy.load(asset_path)
    counts = Counter(obj.type.name for obj in env.objects)
    print(f"== {asset_path} ==")
    print("top types:")
    for name, count in counts.most_common(20):
        print(f"{count:8} {name}")

    hits: list[TextAssetHit] = []
    textassets = [obj for obj in env.objects if obj.type.name == "TextAsset"]
    print("TextAsset count:", len(textassets))

    out_dir: Path | None = None
    if dump_dir:
        out_dir = Path(dump_dir)
        out_dir.mkdir(parents=True, exist_ok=True)

    for obj in textassets:
        ta = obj.read()
        name = getattr(ta, "name", "<unnamed>")
        payload = _read_textasset_bytes(ta)
        matched = _find_needles(payload, needles)
        if not matched:
            continue

        hit = TextAssetHit(
            asset_file=os.path.basename(asset_path),
            name=str(name),
            matched_needles=matched,
            size_bytes=len(payload),
        )
        hits.append(hit)
        print(f"HIT TextAsset name='{hit.name}' size={hit.size_bytes} needles={list(hit.matched_needles)}")

        if out_dir is not None:
            safe = "".join(ch if ch.isalnum() or ch in ("-", "_", ".") else "_" for ch in hit.name)[:120]
            path_id = getattr(obj, "path_id", None)
            suffix = f"__{path_id}" if path_id is not None else ""
            p = out_dir / f"{hit.asset_file}__{safe}{suffix}.bin"
            p.write_bytes(payload)

    if not hits:
        print("No matching TextAssets.")
    return hits


def main() -> None:
    parser = argparse.ArgumentParser(description="Inspect Unity .assets files with UnityPy and find matching TextAssets.")
    parser.add_argument("asset", nargs="?", default=DEFAULT_ASSET_PATH, help="Path to a Unity .assets file")
    parser.add_argument(
        "--needles",
        nargs="*",
        default=[
            "content.cliffhanger-productions.com",
            "cdn-sro01.cliffhanger-productions.com",
            "/Patches/SRO/StandaloneWindows/live",
            "/SRO/configs/",
            "LauncherConfig.xml",
            "clientBaseConfig",
        ],
        help="Strings to search for inside TextAssets",
    )
    parser.add_argument(
        "--dump-dir",
        default=None,
        help="If set, writes matching TextAsset payloads to this directory as .bin files",
    )
    args = parser.parse_args()

    inspect_asset(args.asset, needles=list(args.needles), dump_dir=args.dump_dir)


if __name__ == "__main__":
    main()
