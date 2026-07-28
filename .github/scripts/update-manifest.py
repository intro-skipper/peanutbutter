"""Update one Peanut Butter Jellyfin plugin manifest entry."""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path


PLUGIN_GUID = "d6f8f4f2-65c9-4ebc-a3a8-4b5b7b0e6f59"
PLUGIN_METADATA = {
    "guid": PLUGIN_GUID,
    "name": "Peanut Butter",
    "overview": "Install and update Jellyfin plugins from ZIP files or DLLs over the network.",
    "description": "Provides an administrator-only, staged installer for verified Jellyfin plugin ZIP archives and standalone DLLs.",
    "owner": "Intro Skipper",
    "category": "General",
    "imageUrl": "https://raw.githubusercontent.com/intro-skipper/peanutbutter/main/images/logo.png",
    "versions": [],
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--target-abi", required=True)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--changelog", required=True)
    parser.add_argument("--timestamp", default=None)
    parser.add_argument(
        "--replace-version",
        action="store_true",
        help="Replace an existing entry with the same version instead of retaining it.",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    with args.manifest.open("r", encoding="utf-8") as manifest_file:
        manifest = json.load(manifest_file)

    if not isinstance(manifest, list):
        raise SystemExit(f"{args.manifest} must contain a JSON array")

    plugin = next(
        (
            item
            for item in manifest
            if isinstance(item, dict)
            and str(item.get("guid", "")).lower() == PLUGIN_GUID.lower()
        ),
        None,
    )
    if plugin is None:
        plugin = dict(PLUGIN_METADATA)
        plugin["versions"] = []
        manifest.append(plugin)

    versions = plugin.setdefault("versions", [])
    if not isinstance(versions, list):
        raise SystemExit(f"The versions field in {args.manifest} must be an array")

    timestamp = args.timestamp or datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    entry = {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": timestamp,
    }

    if args.replace_version:
        versions[:] = [item for item in versions if item.get("version") != args.version]
    else:
        versions[:] = [item for item in versions if item.get("sourceUrl") != args.source_url]

    versions.insert(0, entry)
    versions.sort(key=lambda item: item.get("timestamp", ""), reverse=True)

    with args.manifest.open("w", encoding="utf-8", newline="\n") as manifest_file:
        json.dump(manifest, manifest_file, indent=4)
        manifest_file.write("\n")

    print(f"Updated {args.manifest} with {args.version} from {args.source_url}")


if __name__ == "__main__":
    main()
