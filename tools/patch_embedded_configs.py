from __future__ import annotations

import argparse
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional
from urllib.parse import urlsplit, urlunsplit
import xml.etree.ElementTree as ET

import UnityPy


DEFAULT_RESOURCES_ASSETS = (
    r"d:/SteamLibrary/steamapps/common/ShadowrunChronicles/Shadowrun_Data/resources.assets"
)


@dataclass
class PatchStats:
    base_config_patched: bool = False
    launcher_config_patched: bool = False
    updated_fields: int = 0


def _strip_ns(tag: str) -> str:
    if not tag:
        return tag
    if tag.startswith("{") and "}" in tag:
        return tag.split("}", 1)[1]
    return tag


def _rewrite_hostport(value: str, new_host: str) -> str:
    # Expected formats seen in BaseConfiguration: "host:port".
    # Preserve :port if present and numeric.
    v = value.strip()
    if not v:
        return value

    if ":" not in v:
        return new_host

    host_part, port_part = v.rsplit(":", 1)
    if port_part.isdigit():
        return f"{new_host}:{port_part}"

    # Not a numeric port; treat as plain host.
    return new_host


def _rewrite_url(value: str, new_host: str) -> str:
    v = value.strip()
    if not v:
        return value

    try:
        parts = urlsplit(v)
    except Exception:
        return value

    if not parts.scheme or not parts.netloc:
        return value

    port = parts.port
    # Preserve the original explicit port (if present). Do not inject new.
    netloc = new_host if port is None else f"{new_host}:{port}"

    return urlunsplit((parts.scheme, netloc, parts.path, parts.query, parts.fragment))


def _xml_to_bytes(root: ET.Element) -> bytes:
    return ET.tostring(root, encoding="utf-8", xml_declaration=True)


def _patch_base_configuration_xml(xml_text: str, new_host: str) -> tuple[str, int]:
    root = ET.fromstring(xml_text)

    url_tags = {
        "RemoteClientBaseConfigPath",
        "AccountServiceAddress",
        "MatchmakingServiceAddress",
        "ChatAndFriendServiceAddress",
        "DebugInfoStorageAddress",
    }
    hostport_tags = {
        "ServerAddress",
        "PhotonProxyEndPoint",
    }

    updated = 0

    for el in root.iter():
        tag = _strip_ns(el.tag)
        if el.text is None:
            continue

        old = el.text
        new = old

        if tag in url_tags:
            new = _rewrite_url(old, new_host)
        elif tag in hostport_tags:
            new = _rewrite_hostport(old, new_host)

        if new != old:
            el.text = new
            updated += 1

    return _xml_to_bytes(root).decode("utf-8"), updated


def _patch_launcher_config_xml(xml_text: str, new_host: str) -> tuple[str, int]:
    root = ET.fromstring(xml_text)

    # LauncherConfig uses URL fields.
    updated = 0

    for el in root.iter():
        tag = _strip_ns(el.tag)
        if el.text is None:
            continue

        old = el.text
        if "http://" not in old and "https://" not in old:
            continue

        # Patch common URL fields; also patch any *Url elements under <Services>.
        if tag.endswith("Url") or tag.endswith("URL") or tag in {"PatchesUrl", "RemoteConfigUrl"}:
            new = _rewrite_url(old, new_host)
            if new != old:
                el.text = new
                updated += 1

    return _xml_to_bytes(root).decode("utf-8"), updated


def _read_textasset_payload_bytes(text_asset) -> bytes:
    for attr in ("script", "m_Script", "m_ScriptBytes", "m_ScriptString", "data", "m_Data"):
        if hasattr(text_asset, attr):
            v = getattr(text_asset, attr)
            if isinstance(v, (bytes, bytearray)) and v:
                return bytes(v)
            if isinstance(v, str) and v:
                return v.encode("utf-8", errors="replace")
            try:
                b = bytes(v)
                if b:
                    return b
            except Exception:
                pass
    return b""


def _coerce_to_text(value) -> Optional[str]:
    if value is None:
        return None
    if isinstance(value, str):
        return value
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).decode("utf-8", errors="replace")
    try:
        return bytes(value).decode("utf-8", errors="replace")
    except Exception:
        return None


def _decode_xml(payload: bytes) -> Optional[str]:
    for enc in ("utf-8", "utf-16le", "utf-16"):
        try:
            s = payload.decode(enc)
        except Exception:
            continue
        if "<" in s and ("<?xml" in s or "<BaseConfiguration" in s or "<LauncherConfig" in s):
            return s
    return None


def _iter_textasset_objects(env) -> Iterable:
    for obj in env.objects:
        if obj.type.name == "TextAsset":
            yield obj


def patch_resources_assets(asset_path: Path, new_host: str) -> PatchStats:
    env = UnityPy.load(str(asset_path))

    stats = PatchStats()

    for obj in _iter_textasset_objects(env):
        # For this Unity version/build, TextAsset editing is reliably done via typetree.
        # Generated TextAsset.save() returns None here.
        tt = obj.read_typetree()
        script_value = tt.get("m_Script")
        xml_text = _coerce_to_text(script_value)
        if not xml_text:
            continue

        if "<BaseConfiguration" in xml_text:
            patched_xml, updated = _patch_base_configuration_xml(xml_text, new_host)
            if updated:
                tt["m_Script"] = patched_xml
                obj.save_typetree(tt)
                stats.base_config_patched = True
                stats.updated_fields += updated

        elif "<LauncherConfig" in xml_text:
            patched_xml, updated = _patch_launcher_config_xml(xml_text, new_host)
            if updated:
                tt["m_Script"] = patched_xml
                obj.save_typetree(tt)
                stats.launcher_config_patched = True
                stats.updated_fields += updated

    # Write the modified asset back out.
    # resources.assets is usually a single file in env.files.
    out_bytes: Optional[bytes] = None
    for f in env.files.values():
        out_bytes = f.save()
        break
    if out_bytes is None:
        raise RuntimeError("UnityPy environment had no files to save")

    # On Windows, atomic rename/replace can fail if the destination is held open.
    # Writing in-place is sufficient here (the tool already creates a full backup).
    asset_path.write_bytes(out_bytes)

    return stats


def _validate_host_arg(host: str) -> str:
    host = host.strip()
    if not host:
        raise ValueError("--host must be non-empty")
    if "://" in host:
        raise ValueError("--host should be a hostname/IP only (no scheme)")
    if "/" in host or "\\" in host:
        raise ValueError("--host should not include a path")
    if ":" in host:
        # Disallow embedding a port; requirement says ports are preserved from original.
        raise ValueError("--host should not include a port; ports are preserved from existing endpoints")
    return host


def cmd_patch(args: argparse.Namespace) -> int:
    asset_path = Path(args.asset)
    backup_path = Path(args.backup) if args.backup else asset_path.with_suffix(asset_path.suffix + ".bak")

    if not asset_path.exists():
        print(
            f"ERROR: asset not found: {asset_path}\n"
            f"Hint: pass --asset with the full path to your game's resources.assets (note: --asset goes before the subcommand), e.g.:\n"
            f"  patch_embedded_configs.exe --asset \"{DEFAULT_RESOURCES_ASSETS}\" patch --host 127.0.0.1\n"
            f"  patch_embedded_configs.exe --asset \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\ShadowrunChronicles\\Shadowrun_Data\\resources.assets\" patch --host 127.0.0.1",
            file=sys.stderr,
        )
        return 2

    try:
        new_host = _validate_host_arg(args.host)
    except ValueError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2

    if not backup_path.exists() or args.force_backup:
        backup_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(asset_path, backup_path)
        print(f"Backup written: {backup_path}")
    else:
        print(f"Backup exists: {backup_path}")

    stats = patch_resources_assets(asset_path, new_host)
    if not stats.base_config_patched:
        print("WARNING: did not patch any <BaseConfiguration> TextAsset")
    if not stats.launcher_config_patched:
        print("WARNING: did not patch any <LauncherConfig> TextAsset")

    print(f"Patched: {asset_path}")
    print(f"Updated fields: {stats.updated_fields}")
    return 0


def cmd_restore(args: argparse.Namespace) -> int:
    asset_path = Path(args.asset)
    backup_path = Path(args.backup) if args.backup else asset_path.with_suffix(asset_path.suffix + ".bak")

    if not backup_path.exists():
        print(f"ERROR: backup not found: {backup_path}", file=sys.stderr)
        return 2

    asset_path.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(backup_path, asset_path)
    print(f"Restored: {asset_path}")
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description=(
            "Patch embedded BaseConfiguration/LauncherConfig TextAssets in Unity resources.assets "
            "to rewrite endpoint hostnames without editing the Windows hosts file."
        )
    )
    p.add_argument(
        "--asset",
        default=DEFAULT_RESOURCES_ASSETS,
        help=f"Path to resources.assets to patch (example: {DEFAULT_RESOURCES_ASSETS})",
    )
    p.add_argument(
        "--backup",
        default=None,
        help="Path to write/read backup (default: <asset>.bak)",
    )

    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("patch", help="Patch endpoints to a new host")
    sp.add_argument("--host", required=True, help="New hostname/IP to use (no scheme, no port)")
    sp.add_argument(
        "--force-backup",
        action="store_true",
        help="Overwrite existing backup with current asset before patching",
    )
    sp.set_defaults(func=cmd_patch)

    sr = sub.add_parser("restore", help="Restore resources.assets from backup")
    sr.set_defaults(func=cmd_restore)

    return p


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
