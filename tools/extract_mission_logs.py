from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterator


MAIN_LOG_NAME = "requests-csharp.log"
DEFAULT_MAP_NAME = "C_01_Server"


@dataclass(frozen=True)
class MissionInstance:
    map_name: str
    key_kind: str
    key_value: str
    peer: str | None
    coop_group_name: str | None
    mission_level: int | None
    start_ts: str | None
    start_line: int
    start_status: str
    end_ts: str | None
    end_line: int
    end_reason: str
    mission_play_started_ts: str | None
    mission_play_started_line: int | None


@dataclass
class OpenMissionInstance:
    map_name: str
    key_kind: str
    key_value: str
    peer: str | None
    coop_group_name: str | None
    mission_level: int | None
    start_ts: str | None
    start_line: int
    start_status: str
    mission_play_started_ts: str | None = None
    mission_play_started_line: int | None = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Extract raw log slices for a single mission instance from LocalService JSON-line logs."
    )
    parser.add_argument(
        "log_dir",
        help="Directory containing requests-csharp.log and companion logs.",
    )
    parser.add_argument(
        "--map",
        default=DEFAULT_MAP_NAME,
        help=f"Mission map name to extract (default: {DEFAULT_MAP_NAME}).",
    )
    parser.add_argument(
        "--instance-index",
        type=int,
        default=None,
        help="Zero-based mission instance index to extract. Defaults to the latest matching instance.",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        help="List matching mission instances and exit without writing output.",
    )
    parser.add_argument(
        "--output-dir",
        default=None,
        help="Output directory for extracted files. Defaults under <log_dir>/extracted-missions/<map>/.",
    )
    return parser.parse_args()


def parse_timestamp(value: object) -> datetime | None:
    if not isinstance(value, str) or not value:
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError:
        return None


def normalize_map_name(value: str) -> str:
    return value.strip().lower()


def discover_log_files(log_dir: Path) -> list[Path]:
    return sorted(path for path in log_dir.glob("requests-csharp*.log") if path.is_file())


def iter_json_lines(path: Path) -> Iterator[tuple[int, str, dict | None]]:
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line_number, raw_line in enumerate(handle, start=1):
            stripped = raw_line.rstrip("\n")
            if not stripped.strip():
                continue
            try:
                payload = json.loads(stripped)
            except json.JSONDecodeError:
                payload = None
            yield line_number, raw_line, payload if isinstance(payload, dict) else None


def record_key(payload: dict) -> tuple[str, str] | None:
    coop_group_name = payload.get("coopGroupName")
    if isinstance(coop_group_name, str) and coop_group_name:
        return "coopGroupName", coop_group_name

    peer = payload.get("peer")
    if isinstance(peer, str) and peer:
        return "peer", peer

    return None


def close_instance(
    open_instance: OpenMissionInstance,
    end_ts: str | None,
    end_line: int,
    end_reason: str,
) -> MissionInstance:
    return MissionInstance(
        map_name=open_instance.map_name,
        key_kind=open_instance.key_kind,
        key_value=open_instance.key_value,
        peer=open_instance.peer,
        coop_group_name=open_instance.coop_group_name,
        mission_level=open_instance.mission_level,
        start_ts=open_instance.start_ts,
        start_line=open_instance.start_line,
        start_status=open_instance.start_status,
        end_ts=end_ts,
        end_line=end_line,
        end_reason=end_reason,
        mission_play_started_ts=open_instance.mission_play_started_ts,
        mission_play_started_line=open_instance.mission_play_started_line,
    )


def collect_mission_instances(main_log_path: Path, target_map_name: str) -> list[MissionInstance]:
    target_map_key = normalize_map_name(target_map_name)
    open_instances: dict[tuple[str, str], OpenMissionInstance] = {}
    instances: list[MissionInstance] = []
    last_line_number = 0
    last_ts: str | None = None

    for line_number, _raw_line, payload in iter_json_lines(main_log_path):
        last_line_number = line_number
        if payload is None:
            continue

        ts = payload.get("ts") if isinstance(payload.get("ts"), str) else None
        if ts is not None:
            last_ts = ts

        map_name = payload.get("mapName")
        status = payload.get("status")
        record_type = payload.get("type")
        key = record_key(payload)

        if record_type == "sim-cleanup" and key is not None:
            open_instance = open_instances.pop(key, None)
            if open_instance is not None:
                instances.append(close_instance(open_instance, ts, line_number, "sim-cleanup"))
            continue

        if not isinstance(map_name, str) or normalize_map_name(map_name) != target_map_key:
            continue

        if key is None:
            continue

        mission_level = payload.get("missionLevel")
        if not isinstance(mission_level, int):
            level_field = payload.get("level")
            mission_level = level_field if isinstance(level_field, int) else None

        if record_type == "sim" and status == "created":
            previous = open_instances.pop(key, None)
            if previous is not None:
                instances.append(close_instance(previous, ts, line_number, "superseded-by-created"))

            open_instances[key] = OpenMissionInstance(
                map_name=map_name,
                key_kind=key[0],
                key_value=key[1],
                peer=payload.get("peer") if isinstance(payload.get("peer"), str) else None,
                coop_group_name=payload.get("coopGroupName") if isinstance(payload.get("coopGroupName"), str) else None,
                mission_level=mission_level,
                start_ts=ts,
                start_line=line_number,
                start_status="created",
            )
            continue

        if record_type == "sim" and status == "mission-play-started":
            current = open_instances.get(key)
            if current is None:
                current = OpenMissionInstance(
                    map_name=map_name,
                    key_kind=key[0],
                    key_value=key[1],
                    peer=payload.get("peer") if isinstance(payload.get("peer"), str) else None,
                    coop_group_name=payload.get("coopGroupName") if isinstance(payload.get("coopGroupName"), str) else None,
                    mission_level=mission_level,
                    start_ts=ts,
                    start_line=line_number,
                    start_status="mission-play-started",
                )
                open_instances[key] = current

            if current.mission_play_started_ts is None:
                current.mission_play_started_ts = ts
                current.mission_play_started_line = line_number
            if current.mission_level is None:
                current.mission_level = mission_level

    for key in sorted(open_instances):
        instances.append(close_instance(open_instances[key], last_ts, last_line_number, "end-of-file"))

    instances.sort(key=mission_sort_key)
    return instances


def mission_sort_key(instance: MissionInstance) -> tuple[datetime, int, str]:
    timestamp = parse_timestamp(instance.start_ts)
    if timestamp is None:
        timestamp = datetime.min
    return timestamp, instance.start_line, instance.key_value


def format_duration(instance: MissionInstance) -> str:
    start_dt = parse_timestamp(instance.start_ts)
    end_dt = parse_timestamp(instance.end_ts)
    if start_dt is None or end_dt is None or end_dt < start_dt:
        return "unknown"
    total_seconds = int((end_dt - start_dt).total_seconds())
    hours, remainder = divmod(total_seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}"


def print_instances(instances: list[MissionInstance]) -> None:
    if not instances:
        print("No matching mission instances found.")
        return

    for index, instance in enumerate(instances):
        print(
            f"[{index}] map={instance.map_name} key={instance.key_kind}:{instance.key_value} "
            f"start={instance.start_ts or 'unknown'} end={instance.end_ts or 'open'} "
            f"duration={format_duration(instance)} mainLines={instance.start_line}-{instance.end_line} "
            f"endReason={instance.end_reason}"
        )


def sanitize_path_fragment(value: str) -> str:
    sanitized = []
    for char in value:
        if char.isalnum() or char in ("-", "_", "."):
            sanitized.append(char)
        else:
            sanitized.append("_")
    return "".join(sanitized).strip("_") or "instance"


def default_output_dir(log_dir: Path, instance: MissionInstance) -> Path:
    stamp = instance.start_ts or "unknown-start"
    stamp = sanitize_path_fragment(stamp.replace(":", "-"))
    key_fragment = sanitize_path_fragment(instance.key_value)
    map_fragment = sanitize_path_fragment(instance.map_name)
    return log_dir / "extracted-missions" / map_fragment / f"{stamp}__{key_fragment}"


def record_matches_instance(payload: dict, instance: MissionInstance, require_time_window: bool) -> bool:
    if require_time_window:
        record_ts = parse_timestamp(payload.get("ts"))
        start_ts = parse_timestamp(instance.start_ts)
        end_ts = parse_timestamp(instance.end_ts)
        if record_ts is None or start_ts is None:
            return False
        if record_ts < start_ts:
            return False
        if end_ts is not None and record_ts > end_ts:
            return False

    if instance.coop_group_name is not None:
        return payload.get("coopGroupName") == instance.coop_group_name

    if instance.peer is not None:
        return payload.get("peer") == instance.peer

    return False


def extract_main_log(main_log_path: Path, output_path: Path, instance: MissionInstance) -> dict:
    copied = 0
    first_line = None
    last_line = None

    with main_log_path.open("r", encoding="utf-8", errors="replace") as source, output_path.open(
        "w", encoding="utf-8", newline=""
    ) as destination:
        for line_number, raw_line in enumerate(source, start=1):
            if line_number < instance.start_line:
                continue
            if line_number > instance.end_line:
                break
            destination.write(raw_line)
            copied += 1
            if first_line is None:
                first_line = line_number
            last_line = line_number

    return {
        "copiedLines": copied,
        "firstSourceLine": first_line,
        "lastSourceLine": last_line,
        "filter": "main-log-line-range",
    }


def extract_companion_log(source_path: Path, output_path: Path, instance: MissionInstance) -> dict:
    copied = 0
    first_line = None
    last_line = None

    with output_path.open("w", encoding="utf-8", newline="") as destination:
        for line_number, raw_line, payload in iter_json_lines(source_path):
            if payload is None:
                continue
            if not record_matches_instance(payload, instance, require_time_window=True):
                continue
            destination.write(raw_line)
            copied += 1
            if first_line is None:
                first_line = line_number
            last_line = line_number

    return {
        "copiedLines": copied,
        "firstSourceLine": first_line,
        "lastSourceLine": last_line,
        "filter": "time-window-and-mission-key",
    }


def write_metadata(
    output_dir: Path,
    log_dir: Path,
    instance: MissionInstance,
    extraction_results: dict[str, dict],
    selected_index: int,
    instance_count: int,
) -> None:
    metadata = {
        "selectedInstanceIndex": selected_index,
        "matchingInstanceCount": instance_count,
        "logDirectory": str(log_dir),
        "instance": asdict(instance),
        "duration": format_duration(instance),
        "outputs": extraction_results,
    }
    metadata_path = output_dir / "mission-instance.json"
    metadata_path.write_text(json.dumps(metadata, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def run_extraction(args: argparse.Namespace) -> int:
    log_dir = Path(args.log_dir)
    main_log_path = log_dir / MAIN_LOG_NAME
    if not main_log_path.is_file():
        print(f"Main log not found: {main_log_path}")
        return 1

    instances = collect_mission_instances(main_log_path, args.map)
    if args.list:
        print_instances(instances)
        return 0

    if not instances:
        print(f"No mission instances found for map {args.map} in {main_log_path}")
        return 2

    selected_index = args.instance_index if args.instance_index is not None else len(instances) - 1
    if selected_index < 0 or selected_index >= len(instances):
        print(f"Instance index out of range: {selected_index}. Matching instances: {len(instances)}")
        return 3

    instance = instances[selected_index]
    output_dir = Path(args.output_dir) if args.output_dir else default_output_dir(log_dir, instance)
    output_dir.mkdir(parents=True, exist_ok=True)

    extraction_results: dict[str, dict] = {}

    output_main_path = output_dir / MAIN_LOG_NAME
    extraction_results[MAIN_LOG_NAME] = extract_main_log(main_log_path, output_main_path, instance)

    for source_path in discover_log_files(log_dir):
        if source_path.name == MAIN_LOG_NAME:
            continue
        output_path = output_dir / source_path.name
        extraction_results[source_path.name] = extract_companion_log(source_path, output_path, instance)

    write_metadata(output_dir, log_dir, instance, extraction_results, selected_index, len(instances))

    print(f"Selected mission instance [{selected_index}] for map {instance.map_name}")
    print(f"Key: {instance.key_kind}={instance.key_value}")
    print(f"Window: {instance.start_ts or 'unknown'} -> {instance.end_ts or 'open'}")
    print(f"Output: {output_dir}")
    return 0


def main() -> int:
    args = parse_args()
    return run_extraction(args)


if __name__ == "__main__":
    raise SystemExit(main())