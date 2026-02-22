import argparse
import os
from pathlib import Path

import UnityPy


def iter_text_assets(env):
    # UnityPy exposes objects via env.objects; TextAsset is type name "TextAsset".
    for obj in env.objects:
        try:
            if obj.type.name != "TextAsset":
                continue
            data = obj.read()
            name = getattr(data, "m_Name", None) or getattr(data, "m_PathName", None) or getattr(data, "name", None)
            if not name:
                continue

            # UnityPy 1.24.x: TextAsset text is typically in m_Script.
            script = None
            if hasattr(data, "m_Script"):
                script = getattr(data, "m_Script")
            elif hasattr(data, "script"):
                script = getattr(data, "script")

            if script is None:
                continue

            yield name, script
        except Exception:
            continue


def safe_relpath(name: str) -> Path:
    # Some assets use forward slashes to represent folders.
    name = name.replace("\\", "/")
    parts = [p for p in name.split("/") if p and p not in (".", "..")]
    if not parts:
        return Path("unnamed")
    return Path(*parts)


def ensure_json_extension(path: Path) -> Path:
    if path.suffix.lower() == ".json":
        return path
    return path.with_suffix(path.suffix + ".json" if path.suffix else ".json")


def normalize_name(value: str) -> str:
    value = (value or "").strip().replace("\\", "/")
    if not value:
        return ""
    base = value.rsplit("/", 1)[-1]
    if base.lower().endswith(".json"):
        base = base[:-5]
    return base.lower()


def main():
    ap = argparse.ArgumentParser(description="Extract GameLogic JSON/TextAssets from Unity resources.assets")
    ap.add_argument(
        "--assets",
        required=True,
        help="Path to resources.assets (Unity asset database file)",
    )
    ap.add_argument(
        "--out",
        required=True,
        help="Output folder (will be created if missing)",
    )
    ap.add_argument(
        "--filter",
        default="",
        help="Only export TextAssets whose name contains this substring (default: export all).",
    )
    ap.add_argument(
        "--names",
        default="",
        help="Comma-separated allowlist of TextAsset names to export (matches by name or basename; extension optional).",
    )
    args = ap.parse_args()

    assets_path = Path(args.assets)
    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    # UnityPy expects to load the .assets file; it will pick up the .resS automatically if adjacent.
    env = UnityPy.load(str(assets_path))

    exported = 0
    skipped = 0
    filt = args.filter or ""

    allowlist = None
    if args.names:
        allowlist = {normalize_name(n) for n in args.names.split(",") if normalize_name(n)}
        if not allowlist:
            allowlist = None

    for name, script in iter_text_assets(env):
        if filt and filt not in name:
            skipped += 1
            continue

        if allowlist is not None:
            n0 = normalize_name(name)
            if not n0 or n0 not in allowlist:
                skipped += 1
                continue

        rel = ensure_json_extension(safe_relpath(name))
        dest = out_dir / rel
        dest.parent.mkdir(parents=True, exist_ok=True)

        # TextAsset script can be bytes; attempt utf-8 decode for json, else raw bytes.
        if isinstance(script, (bytes, bytearray)):
            data_bytes = bytes(script)
        else:
            data_bytes = str(script).encode("utf-8", errors="replace")

        dest.write_bytes(data_bytes)
        exported += 1

    print(f"exported={exported} skipped={skipped} out={out_dir}")


if __name__ == "__main__":
    main()
