#!/usr/bin/env python3
"""Validate baseline JSON invariants without third-party Python packages."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


WINDOWS_ABSOLUTE_PATH = re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]")
REQUIRED_ROLES = {
    "oblique-complex",
    "regular-nadir",
    "control-reference-accuracy",
    "gaussian-novel-view",
    "failure-quality-control",
    "cross-source-regression",
}
REQUIRED_METRIC_GROUPS = {
    "alignment",
    "coverage",
    "gsd",
    "geometry",
    "mesh",
    "gaussian",
    "performance",
}
PRIVATE_OWNER_FORBIDDEN_KEYS = {
    "aircraft_model",
    "capture_duration_seconds",
    "declared_mpf_images_per_file",
    "embedded_rtk_flag_coverage",
    "exif_thumbnail",
    "file_count",
    "gimbal_pitch_degrees",
    "image_bytes",
    "image_count",
    "mpf_marker_coverage",
    "mpo_auxiliary_frame",
    "photogrammetry_frame",
    "relative_input_extent_m",
    "required_dji_xmp_field_coverage",
    "total_bytes",
}


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def iter_metric_objects(value: Any):
    if isinstance(value, dict):
        if {"status", "value", "unit", "method", "reason"} <= value.keys():
            yield value
        for child in value.values():
            yield from iter_metric_objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from iter_metric_objects(child)


def validate_metric(metric: dict[str, Any]) -> None:
    status = metric["status"]
    if status == "measured":
        assert isinstance(metric["value"], (int, float)), "measured metric must have a number"
        assert metric["reason"] is None, "measured metric must not carry a missing-value reason"
    else:
        assert metric["value"] is None, "unmeasured metric must have a null value"
        assert isinstance(metric["reason"], str) and metric["reason"], "unmeasured metric needs a reason"


def collect_keys(value: Any) -> set[str]:
    if isinstance(value, dict):
        return set(value) | {key for child in value.values() for key in collect_keys(child)}
    if isinstance(value, list):
        return {key for child in value for key in collect_keys(child)}
    return set()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    root = args.root.resolve()

    registry_path = root / "dataset-registry.json"
    dataset_schema_path = root / "schemas" / "dataset-registry.schema.json"
    benchmark_schema_path = root / "schemas" / "benchmark-record.schema.json"
    example_path = root / "examples" / "benchmark-record.example.json"

    registry = load_json(registry_path)
    dataset_schema = load_json(dataset_schema_path)
    benchmark_schema = load_json(benchmark_schema_path)
    example = load_json(example_path)

    datasets = registry["datasets"]
    ids = [dataset["id"] for dataset in datasets]
    assert len(ids) == len(set(ids)), "dataset IDs must be unique"
    registered_roles = {role for dataset in datasets for role in dataset["roles"]}
    assert REQUIRED_ROLES <= registered_roles, "all baseline roles are required"
    assert all(dataset["expected_results"] for dataset in datasets), "each dataset needs expected results"
    assert all(dataset["source_policy"]["mode"] == "read-only" for dataset in datasets)
    physical_sources = [dataset for dataset in datasets if dataset["kind"] == "physical"]
    assert len(physical_sources) >= 3, "the portfolio needs at least three independent physical releases"
    assert len({dataset["source_binding"] for dataset in physical_sources}) == len(physical_sources)
    remote_sources = [dataset for dataset in physical_sources if dataset["availability"] == "selected-remote"]
    assert len(remote_sources) >= 2, "at least two independent remote releases are required"
    assert all(dataset["source_policy"]["license_evidence_url"] for dataset in remote_sources)

    owner = next(dataset for dataset in datasets if dataset["id"] == "owner-oblique-sample-v1")
    assert owner["source_binding"] == "env:QIONGTU_OWNER_SAMPLE"
    assert owner["identity"] == {
        "method": "private-local-manifest",
        "value": "env:QIONGTU_OWNER_SAMPLE_MANIFEST",
    }
    assert not (PRIVATE_OWNER_FORBIDDEN_KEYS & collect_keys(owner)), (
        "public owner entry contains private inventory keys"
    )
    assert "sha256:" not in json.dumps(owner, sort_keys=True), (
        "public owner entry must not publish a content fingerprint"
    )

    fixture_sets = [dataset for dataset in datasets if dataset["kind"] == "derived-fixtures"]
    assert len(fixture_sets) == 1, "exactly one derived fixture set is required"
    recipes = fixture_sets[0]["derivation_recipes"]
    assert len(recipes) >= 5, "fault coverage requires at least five recipes"
    assert all(recipe["source_mutation_allowed"] is False for recipe in recipes)

    assert REQUIRED_METRIC_GROUPS == set(example["metrics"]), "benchmark example metric groups drifted"
    metrics = list(iter_metric_objects(example["metrics"]))
    assert metrics, "benchmark example must contain metric records"
    for metric in metrics:
        validate_metric(metric)

    assert dataset_schema["title"] == "QiongTu benchmark dataset registry"
    assert benchmark_schema["title"] == "QiongTu engine benchmark record"

    public_text = "\n".join(
        path.read_text(encoding="utf-8")
        for path in (registry_path, dataset_schema_path, benchmark_schema_path, example_path)
    )
    assert not WINDOWS_ABSOLUTE_PATH.search(public_text), "public baseline JSON contains an absolute Windows path"
    assert '"GpsLatitude":' not in public_text and '"GpsLongitude":' not in public_text, (
        "public baseline JSON contains precise coordinate fields"
    )

    print(
        json.dumps(
            {
                "datasets": len(datasets),
                "physical_sources": len(physical_sources),
                "registered_roles": len(registered_roles),
                "fault_recipes": len(recipes),
                "example_metrics": len(metrics),
                "absolute_windows_paths": 0,
                "precise_coordinate_fields": 0,
                "owner_private_manifest": True,
                "result": "pass",
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
