#!/usr/bin/env python3
"""Create a privacy-preserving, read-only inventory of a drone dataset.

The report intentionally omits source paths, file names, absolute coordinates,
serial numbers, and capture timestamps so it can be used to build public
benchmark manifests without publishing sensitive collection details.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import statistics
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable


INSPECTOR_VERSION = "1.0.0"
IMAGE_EXTENSIONS = {".jpeg", ".jpg"}
AUXILIARY_EXTENSIONS = {".mrk", ".nav", ".obs", ".rtk"}
SOF_MARKERS = {
    0xC0,
    0xC1,
    0xC2,
    0xC3,
    0xC5,
    0xC6,
    0xC7,
    0xC9,
    0xCA,
    0xCB,
    0xCD,
    0xCE,
    0xCF,
}
STANDALONE_MARKERS = {0x01, *range(0xD0, 0xD9)}
DJI_XMP_PATTERN = re.compile(rb'drone-dji:([A-Za-z0-9]+)="([^"]*)"')
REQUIRED_XMP_FIELDS = (
    "ProductName",
    "DroneModel",
    "GpsLatitude",
    "GpsLongitude",
    "AbsoluteAltitude",
    "RelativeAltitude",
    "GimbalPitchDegree",
    "GimbalRollDegree",
    "GimbalYawDegree",
    "FlightPitchDegree",
    "FlightRollDegree",
    "FlightYawDegree",
    "RtkFlag",
    "RtkStdLon",
    "RtkStdLat",
    "RtkStdHgt",
    "UTCAtExposure",
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def aggregate_fingerprint(records: Iterable[tuple[str, int, str]]) -> str:
    digest = hashlib.sha256()
    for relative_path, size, content_hash in sorted(records):
        digest.update(relative_path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(str(size).encode("ascii"))
        digest.update(b"\0")
        digest.update(content_hash.encode("ascii"))
        digest.update(b"\n")
    return f"sha256:{digest.hexdigest()}"


def find_soi_offsets(data: bytes) -> list[int]:
    offsets: list[int] = []
    cursor = 0
    while True:
        cursor = data.find(b"\xff\xd8", cursor)
        if cursor < 0:
            return offsets
        offsets.append(cursor)
        cursor += 2


def mpf_number_of_images(data: bytes) -> int | None:
    marker = data.find(b"MPF\x00")
    if marker < 0:
        return None
    tiff = marker + 4
    if data[tiff : tiff + 2] == b"II":
        byte_order = "little"
    elif data[tiff : tiff + 2] == b"MM":
        byte_order = "big"
    else:
        return None
    if int.from_bytes(data[tiff + 2 : tiff + 4], byte_order) != 42:
        return None

    ifd_offset = int.from_bytes(data[tiff + 4 : tiff + 8], byte_order)
    ifd = tiff + ifd_offset
    if ifd + 2 > len(data):
        return None
    entry_count = int.from_bytes(data[ifd : ifd + 2], byte_order)
    for index in range(entry_count):
        entry = ifd + 2 + index * 12
        if entry + 12 > len(data):
            return None
        tag = int.from_bytes(data[entry : entry + 2], byte_order)
        field_type = int.from_bytes(data[entry + 2 : entry + 4], byte_order)
        count = int.from_bytes(data[entry + 4 : entry + 8], byte_order)
        if tag != 0xB001 or count != 1:
            continue
        if field_type == 4:
            return int.from_bytes(data[entry + 8 : entry + 12], byte_order)
        if field_type == 3:
            return int.from_bytes(data[entry + 8 : entry + 10], byte_order)
    return None


def jpeg_dimensions_at(data: bytes, start: int) -> tuple[int, int] | None:
    if data[start : start + 2] != b"\xff\xd8":
        return None

    cursor = start + 2
    limit = len(data)
    while cursor + 3 < limit:
        if data[cursor] != 0xFF:
            cursor += 1
            continue
        while cursor < limit and data[cursor] == 0xFF:
            cursor += 1
        if cursor >= limit:
            break

        marker = data[cursor]
        cursor += 1
        if marker in STANDALONE_MARKERS:
            continue
        if marker == 0xDA:
            break
        if cursor + 2 > limit:
            break

        segment_length = int.from_bytes(data[cursor : cursor + 2], "big")
        if segment_length < 2 or cursor + segment_length > limit:
            break
        if marker in SOF_MARKERS and segment_length >= 7:
            height = int.from_bytes(data[cursor + 3 : cursor + 5], "big")
            width = int.from_bytes(data[cursor + 5 : cursor + 7], "big")
            return width, height
        cursor += segment_length
    return None


def jpeg_frame_dimensions(data: bytes) -> list[tuple[int, int]]:
    dimensions: list[tuple[int, int]] = []
    for offset in find_soi_offsets(data):
        value = jpeg_dimensions_at(data, offset)
        if value and value not in dimensions:
            dimensions.append(value)
    return dimensions


def extract_dji_xmp(data: bytes) -> dict[str, str]:
    fields: dict[str, str] = {}
    for name, value in DJI_XMP_PATTERN.findall(data):
        fields[name.decode("ascii")] = value.decode("utf-8", errors="replace")
    return fields


def parse_float(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except ValueError:
        return None


def numeric_summary(values: list[float], precision: int = 6) -> dict[str, float] | None:
    if not values:
        return None
    return {
        "min": round(min(values), precision),
        "median": round(statistics.median(values), precision),
        "max": round(max(values), precision),
    }


def rounded_counter(counter: Counter[str]) -> dict[str, int]:
    return dict(sorted(counter.items(), key=lambda item: item[0]))


def haversine_m(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    radius_m = 6_371_008.8
    phi1 = math.radians(lat1)
    phi2 = math.radians(lat2)
    delta_phi = math.radians(lat2 - lat1)
    delta_lambda = math.radians(lon2 - lon1)
    a = (
        math.sin(delta_phi / 2) ** 2
        + math.cos(phi1) * math.cos(phi2) * math.sin(delta_lambda / 2) ** 2
    )
    return 2 * radius_m * math.atan2(math.sqrt(a), math.sqrt(1 - a))


def parse_capture_time(value: str | None) -> datetime | None:
    if not value:
        return None
    normalized = value.strip().replace("Z", "+00:00")
    try:
        return datetime.fromisoformat(normalized)
    except ValueError:
        return None


def scan_dataset(source: Path, source_id: str) -> dict[str, Any]:
    paths = sorted(path for path in source.rglob("*") if path.is_file())
    before = {
        path: (path.stat().st_size, path.stat().st_mtime_ns)
        for path in paths
    }

    extension_counts: Counter[str] = Counter()
    extension_bytes: Counter[str] = Counter()
    content_hash_counts: Counter[str] = Counter()
    fingerprint_records: list[tuple[str, int, str]] = []
    image_hash_records: list[tuple[str, int, str]] = []
    auxiliary_hash_records: list[tuple[str, int, str]] = []

    mpf_marker_count = 0
    mpf_image_count_distribution: Counter[str] = Counter()
    frame_dimension_distribution: Counter[str] = Counter()
    product_names: Counter[str] = Counter()
    drone_models: Counter[str] = Counter()
    gimbal_pitch_distribution: Counter[str] = Counter()
    rtk_flag_distribution: Counter[str] = Counter()
    xmp_field_presence: Counter[str] = Counter()
    xmp_field_set_distribution: Counter[str] = Counter()

    gps_latitudes: list[float] = []
    gps_longitudes: list[float] = []
    nadir_latitudes: list[float] = []
    nadir_longitudes: list[float] = []
    relative_altitudes: list[float] = []
    absolute_altitudes: list[float] = []
    rtk_std_lon: list[float] = []
    rtk_std_lat: list[float] = []
    rtk_std_hgt: list[float] = []
    capture_times: list[datetime] = []
    nadir_hashes: list[str] = []
    image_bytes = 0
    auxiliary_bytes = 0
    image_count = 0
    auxiliary_count = 0

    for path in paths:
        relative_path = path.relative_to(source).as_posix()
        extension = path.suffix.lower()
        size = path.stat().st_size
        content_hash = sha256_file(path)
        extension_counts[extension or "<none>"] += 1
        extension_bytes[extension or "<none>"] += size
        content_hash_counts[content_hash] += 1
        fingerprint_records.append((relative_path, size, content_hash))

        if extension in AUXILIARY_EXTENSIONS:
            auxiliary_count += 1
            auxiliary_bytes += size
            auxiliary_hash_records.append((relative_path, size, content_hash))
            continue
        if extension not in IMAGE_EXTENSIONS:
            continue

        image_count += 1
        image_bytes += size
        image_hash_records.append((relative_path, size, content_hash))
        data = path.read_bytes()
        if b"MPF\x00" in data:
            mpf_marker_count += 1

        declared_mpf_images = mpf_number_of_images(data)
        mpf_image_count_distribution[
            str(declared_mpf_images) if declared_mpf_images is not None else "unreadable"
        ] += 1
        dimensions = jpeg_frame_dimensions(data)
        dimensions_key = "|".join(f"{width}x{height}" for width, height in dimensions)
        frame_dimension_distribution[dimensions_key or "unreadable"] += 1

        xmp = extract_dji_xmp(data)
        xmp_field_set_distribution[str(len(xmp))] += 1
        for field in REQUIRED_XMP_FIELDS:
            if field in xmp and xmp[field] != "":
                xmp_field_presence[field] += 1

        if xmp.get("ProductName"):
            product_names[xmp["ProductName"]] += 1
        if xmp.get("DroneModel"):
            drone_models[xmp["DroneModel"]] += 1

        pitch = parse_float(xmp.get("GimbalPitchDegree"))
        is_nadir = pitch is not None and pitch <= -85.0
        if pitch is not None:
            pitch_bucket = str(int(round(pitch)))
            gimbal_pitch_distribution[pitch_bucket] += 1
            if is_nadir:
                nadir_hashes.append(content_hash)

        if xmp.get("RtkFlag"):
            rtk_flag_distribution[xmp["RtkFlag"]] += 1

        latitude = parse_float(xmp.get("GpsLatitude"))
        longitude = parse_float(xmp.get("GpsLongitude"))
        if latitude is not None and longitude is not None:
            gps_latitudes.append(latitude)
            gps_longitudes.append(longitude)
            if is_nadir:
                nadir_latitudes.append(latitude)
                nadir_longitudes.append(longitude)

        for field, destination in (
            ("RelativeAltitude", relative_altitudes),
            ("AbsoluteAltitude", absolute_altitudes),
            ("RtkStdLon", rtk_std_lon),
            ("RtkStdLat", rtk_std_lat),
            ("RtkStdHgt", rtk_std_hgt),
        ):
            parsed = parse_float(xmp.get(field))
            if parsed is not None:
                destination.append(parsed)

        capture_time = parse_capture_time(xmp.get("UTCAtExposure"))
        if capture_time is not None:
            capture_times.append(capture_time)

    after = {
        path: (path.stat().st_size, path.stat().st_mtime_ns)
        for path in paths
    }
    if before != after:
        raise RuntimeError("Source dataset changed while it was being inspected")

    duplicate_groups = sum(1 for count in content_hash_counts.values() if count > 1)
    duplicate_extra_files = sum(count - 1 for count in content_hash_counts.values() if count > 1)

    relative_extent_m = None
    if gps_latitudes and gps_longitudes:
        min_lat, max_lat = min(gps_latitudes), max(gps_latitudes)
        min_lon, max_lon = min(gps_longitudes), max(gps_longitudes)
        center_lat = (min_lat + max_lat) / 2
        center_lon = (min_lon + max_lon) / 2
        relative_extent_m = {
            "east_west": round(haversine_m(center_lat, min_lon, center_lat, max_lon), 2),
            "north_south": round(haversine_m(min_lat, center_lon, max_lat, center_lon), 2),
            "diagonal": round(haversine_m(min_lat, min_lon, max_lat, max_lon), 2),
        }

    nadir_relative_extent_m = None
    if nadir_latitudes and nadir_longitudes:
        min_lat, max_lat = min(nadir_latitudes), max(nadir_latitudes)
        min_lon, max_lon = min(nadir_longitudes), max(nadir_longitudes)
        center_lat = (min_lat + max_lat) / 2
        center_lon = (min_lon + max_lon) / 2
        nadir_relative_extent_m = {
            "east_west": round(haversine_m(center_lat, min_lon, center_lat, max_lon), 2),
            "north_south": round(haversine_m(min_lat, center_lon, max_lat, center_lon), 2),
            "diagonal": round(haversine_m(min_lat, min_lon, max_lat, max_lon), 2),
        }

    capture_duration_seconds = None
    if capture_times:
        capture_duration_seconds = round((max(capture_times) - min(capture_times)).total_seconds(), 3)

    nadir_digest = hashlib.sha256()
    for content_hash in sorted(nadir_hashes):
        nadir_digest.update(content_hash.encode("ascii"))
        nadir_digest.update(b"\n")

    total_bytes = sum(size for size, _ in before.values())
    return {
        "inventory_schema_version": "1.0.0",
        "inspector_version": INSPECTOR_VERSION,
        "source_id": source_id,
        "source_policy": {
            "mode": "read_only",
            "source_paths_emitted": False,
            "file_names_emitted": False,
            "absolute_coordinates_emitted": False,
            "serial_numbers_emitted": False,
            "capture_timestamps_emitted": False,
            "source_unchanged_during_scan": True,
        },
        "dataset": {
            "file_count": len(paths),
            "total_bytes": total_bytes,
            "fingerprint": aggregate_fingerprint(fingerprint_records),
            "extension_counts": rounded_counter(extension_counts),
            "extension_bytes": rounded_counter(extension_bytes),
            "duplicate_content_groups": duplicate_groups,
            "duplicate_extra_files": duplicate_extra_files,
        },
        "imagery": {
            "count": image_count,
            "total_bytes": image_bytes,
            "fingerprint": aggregate_fingerprint(image_hash_records),
            "mpf_marker_count": mpf_marker_count,
            "mpf_image_count_distribution": rounded_counter(mpf_image_count_distribution),
            "embedded_jpeg_dimension_distribution": rounded_counter(frame_dimension_distribution),
            "product_name_distribution": rounded_counter(product_names),
            "drone_model_distribution": rounded_counter(drone_models),
            "gimbal_pitch_degree_distribution": rounded_counter(gimbal_pitch_distribution),
            "rtk_flag_distribution": rounded_counter(rtk_flag_distribution),
            "required_xmp_field_presence": {
                field: xmp_field_presence[field] for field in REQUIRED_XMP_FIELDS
            },
            "xmp_field_count_distribution": rounded_counter(xmp_field_set_distribution),
            "gps_record_count": len(gps_latitudes),
            "relative_extent_m": relative_extent_m,
            "capture_duration_seconds": capture_duration_seconds,
            "relative_altitude_m": numeric_summary(relative_altitudes, 3),
            "absolute_altitude_m": numeric_summary(absolute_altitudes, 3),
            "rtk_std_lon_m": numeric_summary(rtk_std_lon, 6),
            "rtk_std_lat_m": numeric_summary(rtk_std_lat, 6),
            "rtk_std_hgt_m": numeric_summary(rtk_std_hgt, 6),
        },
        "auxiliary": {
            "count": auxiliary_count,
            "total_bytes": auxiliary_bytes,
            "fingerprint": aggregate_fingerprint(auxiliary_hash_records),
            "extension_counts": {
                extension: extension_counts[extension]
                for extension in sorted(AUXILIARY_EXTENSIONS)
                if extension_counts[extension]
            },
        },
        "logical_subsets": {
            "regular_nadir": {
                "selection_rule": "GimbalPitchDegree <= -85",
                "image_count": len(nadir_hashes),
                "fingerprint": f"sha256:{nadir_digest.hexdigest()}",
                "relative_extent_m": nadir_relative_extent_m,
            }
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--source-id", required=True)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    source = args.source.resolve(strict=True)
    output = args.output.resolve()
    if not source.is_dir():
        raise SystemExit("--source must be a directory")
    if output == source or source in output.parents:
        raise SystemExit("--output must be outside the read-only source directory")

    report = scan_dataset(source, args.source_id)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
