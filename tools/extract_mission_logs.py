from __future__ import annotations

import argparse
import json
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterator


LEGACY_MAIN_LOG_NAME = "requests.log"
LEGACY_LOG_GLOB = "requests*.log"
EVENTS_LOG_GLOB = "events-*.jsonl"
DIAGNOSTICS_LOG_GLOB = "diagnostics-*.jsonl"
OUTPUT_MAIN_LOG_NAME = "events.jsonl"
DEFAULT_MAP_NAME = "C_01_Server"


@dataclass(frozen=True)
class SourceLineRef:
    file_name: str
    line_number: int


@dataclass(frozen=True)
class MissionInstance:
    map_name: str
    key_kind: str
    key_value: str
    connection_hash: str | None
    peer: str | None
    coop_group_name: str | None
    mission_level: int | None
    start_ts: str | None
    start_ref: SourceLineRef
    start_status: str
    end_ts: str | None
    end_ref: SourceLineRef
    end_reason: str
    mission_play_started_ts: str | None
    mission_play_started_ref: SourceLineRef | None


@dataclass
class OpenMissionInstance:
    map_name: str
    key_kind: str
    key_value: str
    connection_hash: str | None
    peer: str | None
    coop_group_name: str | None
    mission_level: int | None
    start_ts: str | None
    start_ref: SourceLineRef
    start_status: str
    mission_play_started_ts: str | None = None
    mission_play_started_ref: SourceLineRef | None = None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Extract raw log slices for a single mission instance from LocalService JSON-line logs."
    )
    parser.add_argument(
        "log_dir",
        help="Directory containing structured events/diagnostics JSONL files or legacy requests logs.",
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
        if value.endswith("Z"):
            try:
                return datetime.fromisoformat(value[:-1] + "+00:00")
            except ValueError:
                return None
        return None


def normalize_map_name(value: str) -> str:
    return value.strip().lower()


def discover_main_log_files(log_dir: Path) -> list[Path]:
    files = sorted(path for path in log_dir.glob(EVENTS_LOG_GLOB) if path.is_file())
    if files:
        return files

    legacy = log_dir / LEGACY_MAIN_LOG_NAME
    return [legacy] if legacy.is_file() else []


def discover_companion_streams(log_dir: Path) -> dict[str, list[Path]]:
    main_files = discover_main_log_files(log_dir)
    if not main_files:
        return {}

    structured_main = any(path.match(EVENTS_LOG_GLOB) for path in main_files)
    streams: dict[str, list[Path]] = {OUTPUT_MAIN_LOG_NAME: main_files}

    if structured_main:
        diagnostic_files = sorted(path for path in log_dir.glob(DIAGNOSTICS_LOG_GLOB) if path.is_file())
        if diagnostic_files:
            streams["diagnostics.jsonl"] = diagnostic_files
        return streams

    for source_path in sorted(path for path in log_dir.glob(LEGACY_LOG_GLOB) if path.is_file()):
        if source_path.name == LEGACY_MAIN_LOG_NAME:
            continue
        streams[source_path.name] = [source_path]
    return streams


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

    connection_hash = payload.get("connectionHash")
    if isinstance(connection_hash, str) and connection_hash:
        return "connectionHash", connection_hash

    peer = payload.get("peer")
    if isinstance(peer, str) and peer:
        return "peer", peer

    return None


def get_timestamp(payload: dict) -> str | None:
    timestamp = payload.get("timestamp")
    if isinstance(timestamp, str) and timestamp:
        return timestamp
    ts = payload.get("ts")
    return ts if isinstance(ts, str) and ts else None


def get_component(payload: dict) -> str | None:
    component = payload.get("component")
    if isinstance(component, str) and component:
        return component
    record_type = payload.get("type")
    return record_type if isinstance(record_type, str) and record_type else None


def close_instance(
    open_instance: OpenMissionInstance,
    end_ts: str | None,
    end_ref: SourceLineRef,
    end_reason: str,
) -> MissionInstance:
    return MissionInstance(
        map_name=open_instance.map_name,
        key_kind=open_instance.key_kind,
        key_value=open_instance.key_value,
        connection_hash=open_instance.connection_hash,
        peer=open_instance.peer,
        coop_group_name=open_instance.coop_group_name,
        mission_level=open_instance.mission_level,
        start_ts=open_instance.start_ts,
        start_ref=open_instance.start_ref,
        start_status=open_instance.start_status,
        end_ts=end_ts,
        end_ref=end_ref,
        end_reason=end_reason,
        mission_play_started_ts=open_instance.mission_play_started_ts,
        mission_play_started_ref=open_instance.mission_play_started_ref,
    )


def collect_mission_instances(main_log_paths: list[Path], target_map_name: str) -> list[MissionInstance]:
    target_map_key = normalize_map_name(target_map_name)
    open_instances: dict[tuple[str, str], OpenMissionInstance] = {}
    instances: list[MissionInstance] = []
    last_ref = SourceLineRef(file_name=main_log_paths[-1].name, line_number=0)
    last_ts: str | None = None

    for main_log_path in main_log_paths:
        for line_number, _raw_line, payload in iter_json_lines(main_log_path):
            last_ref = SourceLineRef(file_name=main_log_path.name, line_number=line_number)
            if payload is None:
                continue

            ts = get_timestamp(payload)
            if ts is not None:
                last_ts = ts

            map_name = payload.get("mapName")
            status = payload.get("status")
            component = get_component(payload)
            key = record_key(payload)

            if component == "sim-cleanup" and key is not None:
                open_instance = open_instances.pop(key, None)
                if open_instance is not None:
                    instances.append(close_instance(open_instance, ts, last_ref, "sim-cleanup"))
                continue

            if not isinstance(map_name, str) or normalize_map_name(map_name) != target_map_key:
                continue

            if key is None:
                continue

            mission_level = payload.get("missionLevel")
            if not isinstance(mission_level, int):
                level_field = payload.get("level")
                mission_level = level_field if isinstance(level_field, int) else None

            if component == "sim" and status == "created":
                previous = open_instances.pop(key, None)
                if previous is not None:
                    instances.append(close_instance(previous, ts, last_ref, "superseded-by-created"))

                open_instances[key] = OpenMissionInstance(
                    map_name=map_name,
                    key_kind=key[0],
                    key_value=key[1],
                    connection_hash=payload.get("connectionHash") if isinstance(payload.get("connectionHash"), str) else None,
                    peer=payload.get("peer") if isinstance(payload.get("peer"), str) else None,
                    coop_group_name=payload.get("coopGroupName") if isinstance(payload.get("coopGroupName"), str) else None,
                    mission_level=mission_level,
                    start_ts=ts,
                    start_ref=last_ref,
                    start_status="created",
                )
                continue

            if component == "sim" and status == "mission-play-started":
                current = open_instances.get(key)
                if current is None:
                    current = OpenMissionInstance(
                        map_name=map_name,
                        key_kind=key[0],
                        key_value=key[1],
                        connection_hash=payload.get("connectionHash") if isinstance(payload.get("connectionHash"), str) else None,
                        peer=payload.get("peer") if isinstance(payload.get("peer"), str) else None,
                        coop_group_name=payload.get("coopGroupName") if isinstance(payload.get("coopGroupName"), str) else None,
                        mission_level=mission_level,
                        start_ts=ts,
                        start_ref=last_ref,
                        start_status="mission-play-started",
                    )
                    open_instances[key] = current

                if current.mission_play_started_ts is None:
                    current.mission_play_started_ts = ts
                    current.mission_play_started_ref = last_ref
                if current.mission_level is None:
                    current.mission_level = mission_level

    for key in sorted(open_instances):
        instances.append(close_instance(open_instances[key], last_ts, last_ref, "end-of-file"))

    instances.sort(key=mission_sort_key)
    return instances


def mission_sort_key(instance: MissionInstance) -> tuple[datetime, str, int, str]:
    timestamp = parse_timestamp(instance.start_ts)
    if timestamp is None:
        timestamp = datetime.min
    return timestamp, instance.start_ref.file_name, instance.start_ref.line_number, instance.key_value


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
            f"duration={format_duration(instance)} mainStart={instance.start_ref.file_name}:{instance.start_ref.line_number} "
            f"mainEnd={instance.end_ref.file_name}:{instance.end_ref.line_number} endReason={instance.end_reason}"
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
        record_ts = parse_timestamp(get_timestamp(payload))
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

    if instance.connection_hash is not None:
        return payload.get("connectionHash") == instance.connection_hash

    if instance.peer is not None:
        return payload.get("peer") == instance.peer

    return False


def extract_main_logs(main_log_paths: list[Path], output_path: Path, instance: MissionInstance) -> dict:
    copied = 0
    source_files: list[dict[str, object]] = []

    with output_path.open("w", encoding="utf-8", newline="") as destination:
        for main_log_path in main_log_paths:
            file_copied = 0
            first_source_line = None
            last_source_line = None
            started = main_log_path.name != instance.start_ref.file_name
            finished = False

            with main_log_path.open("r", encoding="utf-8", errors="replace") as source:
                for line_number, raw_line in enumerate(source, start=1):
                    if not started:
                        if line_number < instance.start_ref.line_number:
                            continue
                        started = True

                    if started and main_log_path.name == instance.end_ref.file_name and line_number > instance.end_ref.line_number:
                        finished = True
                        break

                    if not started:
                        continue

                    destination.write(raw_line)
                    copied += 1
                    file_copied += 1
                    if first_source_line is None:
                        first_source_line = line_number
                    last_source_line = line_number

            if file_copied > 0:
                source_files.append(
                    {
                        "fileName": main_log_path.name,
                        "copiedLines": file_copied,
                        "firstSourceLine": first_source_line,
                        "lastSourceLine": last_source_line,
                    }
                )

            if main_log_path.name == instance.end_ref.file_name and finished:
                break

    return {
        "copiedLines": copied,
        "filter": "main-stream-window",
        "sourceFiles": source_files,
    }


def extract_companion_logs(source_paths: list[Path], output_path: Path, instance: MissionInstance) -> dict:
    copied = 0
    source_files: list[dict[str, object]] = []

    with output_path.open("w", encoding="utf-8", newline="") as destination:
        for source_path in source_paths:
            file_copied = 0
            first_line = None
            last_line = None
            for line_number, raw_line, payload in iter_json_lines(source_path):
                if payload is None:
                    continue
                if not record_matches_instance(payload, instance, require_time_window=True):
                    continue
                destination.write(raw_line)
                copied += 1
                file_copied += 1
                if first_line is None:
                    first_line = line_number
                last_line = line_number

            if file_copied > 0:
                source_files.append(
                    {
                        "fileName": source_path.name,
                        "copiedLines": file_copied,
                        "firstSourceLine": first_line,
                        "lastSourceLine": last_line,
                    }
                )

    return {
        "copiedLines": copied,
        "filter": "time-window-and-mission-key",
        "sourceFiles": source_files,
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
    streams = discover_companion_streams(log_dir)
    main_log_paths = streams.get(OUTPUT_MAIN_LOG_NAME, [])
    if not main_log_paths:
        print(f"No supported LocalService log streams found in {log_dir}")
        return 1

    instances = collect_mission_instances(main_log_paths, args.map)
    if args.list:
        print_instances(instances)
        return 0

    if not instances:
        print(f"No mission instances found for map {args.map} in {log_dir}")
        return 2

    selected_index = args.instance_index if args.instance_index is not None else len(instances) - 1
    if selected_index < 0 or selected_index >= len(instances):
        print(f"Instance index out of range: {selected_index}. Matching instances: {len(instances)}")
        return 3

    instance = instances[selected_index]
    output_dir = Path(args.output_dir) if args.output_dir else default_output_dir(log_dir, instance)
    output_dir.mkdir(parents=True, exist_ok=True)

    extraction_results: dict[str, dict] = {}
    extraction_results[OUTPUT_MAIN_LOG_NAME] = extract_main_logs(main_log_paths, output_dir / OUTPUT_MAIN_LOG_NAME, instance)

    for output_name, source_paths in streams.items():
        if output_name == OUTPUT_MAIN_LOG_NAME:
            continue
        extraction_results[output_name] = extract_companion_logs(source_paths, output_dir / output_name, instance)

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