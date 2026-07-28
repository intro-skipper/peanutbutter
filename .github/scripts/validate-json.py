"""Validate Jellyfin manifest JSON files."""

from __future__ import annotations

import json
import pathlib
import re
import uuid
from urllib.parse import urlparse


VERSION_RE = re.compile(r"^\d+\.\d+(?:\.\d+){0,2}$")
CHECKSUM_RE = re.compile(r"^[0-9a-fA-F]{32}$")
MANIFEST_NAMES = {"manifest.json", "beta-manifest.json"}


def validate_manifest(path: pathlib.Path, data: object) -> list[str]:
    errors: list[str] = []
    if not isinstance(data, list):
        return ["top-level value must be an array"]

    for index, plugin in enumerate(data):
        prefix = f"$[{index}]"
        if not isinstance(plugin, dict):
            errors.append(f"{prefix} must be an object")
            continue
        for field in ("guid", "name", "overview", "description", "owner", "category", "versions"):
            if field not in plugin:
                errors.append(f"{prefix}.{field} is required")
        try:
            uuid.UUID(str(plugin.get("guid", "")))
        except (ValueError, AttributeError, TypeError):
            errors.append(f"{prefix}.guid is not a valid GUID")

        versions = plugin.get("versions")
        if not isinstance(versions, list):
            errors.append(f"{prefix}.versions must be an array")
            continue
        for version_index, version in enumerate(versions):
            version_prefix = f"{prefix}.versions[{version_index}]"
            if not isinstance(version, dict):
                errors.append(f"{version_prefix} must be an object")
                continue
            for field in ("version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"):
                if field not in version:
                    errors.append(f"{version_prefix}.{field} is required")
            if not VERSION_RE.fullmatch(str(version.get("version", ""))):
                errors.append(f"{version_prefix}.version is not a System.Version")
            if not VERSION_RE.fullmatch(str(version.get("targetAbi", ""))):
                errors.append(f"{version_prefix}.targetAbi is not a System.Version")
            source_url = str(version.get("sourceUrl", ""))
            parsed_url = urlparse(source_url)
            if parsed_url.scheme not in {"http", "https"} or not parsed_url.netloc or not parsed_url.path.lower().endswith(".zip"):
                errors.append(f"{version_prefix}.sourceUrl must be an absolute URL ending in .zip")
            if not CHECKSUM_RE.fullmatch(str(version.get("checksum", ""))):
                errors.append(f"{version_prefix}.checksum must be a 32-character MD5")
    return errors


def main() -> int:
    errors: list[str] = []
    for path in sorted(pathlib.Path(".").rglob("*.json")):
        try:
            with path.open("r", encoding="utf-8") as json_file:
                data = json.load(json_file)
        except (OSError, json.JSONDecodeError) as exception:
            errors.append(f"{path}: {exception}")
            continue
        if path.name in MANIFEST_NAMES:
            errors.extend(f"{path}: {error}" for error in validate_manifest(path, data))
        print(f"OK: {path}")

    if errors:
        print("\nValidation errors:")
        print("\n".join(f"- {error}" for error in errors))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
