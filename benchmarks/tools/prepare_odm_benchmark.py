#!/usr/bin/env python3
"""Prepare a reproducible, read-only ODM benchmark run without executing it.

This utility deliberately does *not* call Docker, download data, hash image
contents, or run a reconstruction.  It validates a materialized local image
directory and writes an ignored local run manifest containing the exact Docker
argv, pinned image reference, ODM parameters, and expected output paths.  A
later runner may execute the recorded argv after independently checking Docker
availability and available resources.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import tempfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


MANIFEST_VERSION = "1.0.0"
IMAGE_EXTENSIONS = frozenset({".jpg", ".jpeg", ".tif", ".tiff", ".png"})
RUN_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{0,62}$")
SHA256_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
FORBIDDEN_ODM_ARGUMENTS = frozenset({"--project-path", "--images"})


def resolved(path: Path) -> Path:
    """Resolve a possibly-not-yet-created path without requiring it to exist."""

    return path.expanduser().resolve(strict=False)


def path_is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
    except ValueError:
        return False
    return True


def power_shell_quote(value: str) -> str:
    """Quote one literal argument for copy/paste in PowerShell."""

    return "'" + value.replace("'", "''") + "'"


def source_snapshot(source: Path) -> tuple[tuple[str, int, int], ...]:
    """Collect shallow input evidence without opening or hashing source files."""

    entries: list[tuple[str, int, int]] = []
    for entry in sorted(source.iterdir(), key=lambda item: item.name.casefold()):
        if entry.is_file():
            stat = entry.stat()
            entries.append((entry.name, stat.st_size, stat.st_mtime_ns))
    return tuple(entries)


def validate_input_directory(source: Path) -> dict[str, Any]:
    """Validate an ODM-ready, flat image directory without mutating it."""

    if not source.is_dir():
        raise ValueError(f"--input-dir must be an existing directory: {source}")

    before = source_snapshot(source)
    extension_counts: Counter[str] = Counter()
    non_image_file_count = 0
    nested_directory_count = 0
    for entry in source.iterdir():
        if entry.is_dir():
            nested_directory_count += 1
            continue
        if not entry.is_file():
            continue
        extension = entry.suffix.casefold()
        if extension in IMAGE_EXTENSIONS:
            extension_counts[extension] += 1
        else:
            non_image_file_count += 1
    after = source_snapshot(source)
    if before != after:
        raise RuntimeError("Input directory changed while it was being validated")

    image_count = sum(extension_counts.values())
    if image_count == 0:
        supported = ", ".join(sorted(IMAGE_EXTENSIONS))
        raise ValueError(f"No directly contained ODM image files found ({supported})")

    return {
        "mode": "read-only-bind-mount",
        "layout": "flat-directory",
        "recognized_image_count": image_count,
        "recognized_image_extensions": dict(sorted(extension_counts.items())),
        "non_image_file_count": non_image_file_count,
        "nested_directory_count": nested_directory_count,
        "source_unchanged_during_validation": True,
        "notes": [
            "Only directly contained image files are counted because ODM receives this directory as its images mount.",
            "Nested directories are not copied or mounted as separate image inputs; inspect them before the run if present.",
            "Image bytes are not opened or hashed by this preparation utility.",
        ],
    }


def validate_identity(dataset_id: str, run_id: str, image: str, image_digest: str) -> None:
    if not RUN_ID_PATTERN.fullmatch(dataset_id):
        raise ValueError("--dataset-id must use lowercase letters, digits, and hyphens")
    if not RUN_ID_PATTERN.fullmatch(run_id):
        raise ValueError("--run-id must use lowercase letters, digits, and hyphens")
    if "@" in image or not image.strip():
        raise ValueError("--image must be a repository/tag reference without an @ digest")
    if not SHA256_PATTERN.fullmatch(image_digest):
        raise ValueError("--image-digest must be sha256: followed by 64 lowercase hexadecimal characters")


def validate_odm_arguments(arguments: list[str]) -> None:
    for argument in arguments:
        if argument in FORBIDDEN_ODM_ARGUMENTS or argument.startswith("--project-path="):
            raise ValueError(f"ODM argument {argument!r} is controlled by this tool and cannot be overridden")
        if "\x00" in argument:
            raise ValueError("ODM arguments must not contain NUL bytes")


def docker_mount(source: Path, target: str, readonly: bool) -> str:
    parts = ["type=bind", f"source={source}", f"target={target}"]
    if readonly:
        parts.append("readonly")
    return ",".join(parts)


def prepare_manifest(
    *,
    dataset_id: str,
    run_id: str,
    input_dir: Path,
    artifacts_root: Path,
    image: str,
    image_digest: str,
    odm_arguments: list[str],
) -> tuple[Path, dict[str, Any]]:
    """Validate inputs and create a local-only benchmark manifest.

    The caller is responsible for putting ``artifacts_root`` below a Git-ignored
    location.  This function deliberately refuses to create a run underneath
    the source input to ensure the source cannot receive ODM outputs.
    """

    validate_identity(dataset_id, run_id, image, image_digest)
    validate_odm_arguments(odm_arguments)

    source = resolved(input_dir)
    artifact_root = resolved(artifacts_root)
    if not source.exists():
        raise ValueError(f"--input-dir does not exist: {source}")
    if path_is_within(artifact_root, source) or artifact_root == source:
        raise ValueError("--artifacts-root must be outside --input-dir")

    input_validation = validate_input_directory(source)
    run_root = artifact_root / "odm-runs" / dataset_id / run_id
    work_dir = run_root / "work"
    manifest_path = run_root / "odm-run.manifest.local.json"
    if "," in str(source) or "," in str(work_dir):
        raise ValueError("Docker --mount cannot safely represent a source or work path containing a comma")
    if run_root.exists():
        raise ValueError(f"Refusing to overwrite an existing run directory: {run_root}")
    if path_is_within(source, run_root):
        raise ValueError("Run directory must not contain --input-dir")

    project_name = "benchmark"
    container_project = f"/datasets/{project_name}"
    image_reference = f"{image}@{image_digest}"
    docker_argv = [
        "docker",
        "run",
        "--rm",
        "--name",
        f"qiongtu-odm-{run_id}",
        "--mount",
        docker_mount(source, f"{container_project}/images", readonly=True),
        "--mount",
        docker_mount(work_dir, container_project, readonly=False),
        image_reference,
        "--project-path",
        "/datasets",
        project_name,
        *odm_arguments,
    ]
    expected_outputs = {
        "dom_geotiff": str(work_dir / "odm_orthophoto" / "odm_orthophoto.tif"),
        "dsm_geotiff": str(work_dir / "odm_dem" / "dsm.tif"),
        "dense_point_cloud": str(work_dir / "odm_georeferencing" / "odm_georeferenced_model.laz"),
        "textured_mesh": str(work_dir / "odm_texturing" / "odm_textured_model.obj"),
        "engine_log": str(work_dir / "odm_report" / "report.json"),
    }
    manifest: dict[str, Any] = {
        "schema_version": MANIFEST_VERSION,
        "prepared_at_utc": datetime.now(timezone.utc).replace(microsecond=0).isoformat(),
        "execution_state": "prepared-not-executed",
        "safety_boundary": {
            "source_mutation_allowed": False,
            "source_bind_mount_readonly": True,
            "downloads_performed": False,
            "image_content_hashing_performed": False,
            "docker_invoked": False,
            "note": "This manifest is local-only operational evidence and must remain under the Git-ignored artifacts directory.",
        },
        "benchmark": {"dataset_id": dataset_id, "run_id": run_id},
        "input": {"path": str(source), "validation": input_validation},
        "runtime": {
            "kind": "docker-optional-odm-benchmark",
            "product_runtime_dependency": False,
            "image": image,
            "image_digest": image_digest,
            "image_reference": image_reference,
            "image_inspection_status": "pending-docker-pull-and-inspect",
        },
        "odm": {
            "project_name": project_name,
            "project_path_in_container": "/datasets",
            "parameters": odm_arguments,
        },
        "paths": {
            "run_root": str(run_root),
            "work_dir": str(work_dir),
            "manifest": str(manifest_path),
            "expected_outputs": expected_outputs,
        },
        "command": {
            "docker_argv": docker_argv,
            "powershell": " ".join(power_shell_quote(part) for part in docker_argv),
            "run_instruction": "Review the manifest, confirm Docker/image/resource readiness, then execute the recorded argv exactly once.",
        },
    }

    run_root.mkdir(parents=True, exist_ok=False)
    work_dir.mkdir()
    temporary_manifest = manifest_path.with_suffix(manifest_path.suffix + ".tmp")
    temporary_manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary_manifest, manifest_path)
    return manifest_path, manifest


def run_self_test() -> int:
    """Exercise preparation safety without Docker or a real imagery dataset."""

    digest = "sha256:" + "a" * 64
    with tempfile.TemporaryDirectory(prefix="qiongtu-odm-prepare-") as temporary:
        root = Path(temporary)
        source = root / "source"
        source.mkdir()
        (source / "photo.JPG").write_bytes(b"not-decoded-by-prepare-tool")
        artifacts = root / "artifacts"
        manifest_path, manifest = prepare_manifest(
            dataset_id="self-test-dataset",
            run_id="self-test-run",
            input_dir=source,
            artifacts_root=artifacts,
            image="opendronemap/odm:example",
            image_digest=digest,
            odm_arguments=["--dsm", "--pc-quality", "medium"],
        )
        assert manifest_path.is_file()
        assert manifest["execution_state"] == "prepared-not-executed"
        assert manifest["input"]["validation"]["recognized_image_count"] == 1
        assert "readonly" in manifest["command"]["docker_argv"][6]
        assert manifest["runtime"]["product_runtime_dependency"] is False
        try:
            prepare_manifest(
                dataset_id="self-test-dataset",
                run_id="unsafe-output",
                input_dir=source,
                artifacts_root=source / "artifacts",
                image="opendronemap/odm:example",
                image_digest=digest,
                odm_arguments=[],
            )
        except ValueError as error:
            assert "outside --input-dir" in str(error)
        else:
            raise AssertionError("artifacts root inside source was not rejected")
    print(json.dumps({"result": "pass", "self_test": True, "docker_invoked": False}))
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true", help="Run isolated checks without Docker or real imagery.")
    parser.add_argument("--dataset-id", help="Registered benchmark dataset ID, lowercase letters/digits/hyphens.")
    parser.add_argument("--run-id", help="Unique local run ID, lowercase letters/digits/hyphens.")
    parser.add_argument("--input-dir", type=Path, help="Existing, materialized, flat raw-image directory; never modified.")
    parser.add_argument(
        "--artifacts-root",
        type=Path,
        default=Path("artifacts/benchmarks"),
        help="Git-ignored local root for run manifests and engine work directories.",
    )
    parser.add_argument("--image", help="ODM image repository/tag, without @digest.")
    parser.add_argument("--image-digest", help="Immutable OCI digest, formatted sha256:<64 lowercase hex>.")
    parser.add_argument(
        "--odm-arg",
        action="append",
        default=[],
        help="One ODM argument per occurrence; --project-path and --images are controlled by this tool.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.self_test:
        return run_self_test()
    required = ("dataset_id", "run_id", "input_dir", "image", "image_digest")
    missing = [name.replace("_", "-") for name in required if getattr(args, name) is None]
    if missing:
        raise SystemExit("Missing required arguments: " + ", ".join("--" + name for name in missing))
    manifest_path, manifest = prepare_manifest(
        dataset_id=args.dataset_id,
        run_id=args.run_id,
        input_dir=args.input_dir,
        artifacts_root=args.artifacts_root,
        image=args.image,
        image_digest=args.image_digest,
        odm_arguments=args.odm_arg,
    )
    print(json.dumps({"result": "prepared", "manifest": str(manifest_path), "command": manifest["command"]["powershell"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
