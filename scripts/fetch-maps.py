#!/usr/bin/env python3
"""Fetch Squad map data for offline use; never installs packages or runtimes.

Requires an existing Python with Pillow and WebP support. Map graphics are
converted to JPEG for Windows/.NET Framework image support. Height PNG bytes
are preserved unchanged because their R/B channels encode terrain elevation.
"""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import date
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import tempfile
import time
import urllib.request
import xml.etree.ElementTree as ET

from PIL import Image, features


REPO = "sh4rkman/SquadCalc"
DATA_ORIGIN = "https://squadcalc.app/api"
NAMES = {
    "AlBasrah": "Al Basrah", "Anvil": "Anvil", "BlackCoast": "Black Coast",
    "Chora": "Chora", "Fallujah": "Fallujah", "FoolsRoad": "Fool's Road",
    "GooseBay": "Goose Bay", "Gorodok": "Gorodok", "Harju": "Harju",
    "Jensen": "Jensen's Range", "Kamdesh": "Kamdesh Highlands",
    "Kohat": "Kohat Toi", "Kokan": "Kokan", "Lashkar": "Lashkar Valley",
    "Logar": "Logar Valley", "Manicouagan": "Manicouagan", "Mestia": "Mestia",
    "Mutaha": "Mutaha", "Narva": "Narva", "Pacific": "Pacific Proving Grounds",
    "Sanxian": "Sanxian Islands", "Skorpo": "Skorpo", "Sumari": "Sumari Bala",
    "Tallil": "Tallil Outskirts", "Yehorivka": "Yehorivka",
}


def read_url(url):
    request = urllib.request.Request(url, headers={"User-Agent": "SquadTools-OfflineMaps/1.0"})
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                return response.read()
        except Exception:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def parse_maps(source):
    # Deliberately exclude the mod catalog and commented retired maps.
    official = re.sub(r"//[^\n]*", "", source.split("// SPM", 1)[0])
    matches = list(re.finditer(r'name:\s*"([^\"]+)"', official))
    result = []
    for index, match in enumerate(matches):
        item = official[match.start():matches[index + 1].start() if index + 1 < len(matches) else len(official)]
        name = match.group(1)
        if name not in NAMES:
            raise ValueError("Unreviewed official map: " + name)
        path = re.search(r'mapURL:\s*"([^\"]+)"', item).group(1)
        corners = [list(map(float, re.search(r'corner' + str(i) + r':\s*\[([^\]]+)\]', item).group(1).split(","))) for i in (0, 1)]
        scale = list(map(float, re.search(r'landscapeScale:\s*\[([^\]]+)\]', item).group(1).split(",")))[2]
        offset_match = re.search(r'zOffset:\s*([-\d.]+)', item)
        result.append({"id": path.rstrip("/").split("/")[-1], "name": NAMES[name],
                       "path": path, "width": abs(corners[1][0] - corners[0][0]),
                       "height": abs(corners[1][1] - corners[0][1]), "heightScale": scale,
                       "heightOffset": float(offset_match.group(1)) if offset_match else 0})
    if len(result) != 25:
        raise ValueError("Expected the reviewed catalog of 25 official maps, got " + str(len(result)))
    # Belaya was retired from the current game. Its commented SDK minimap
    # bounds and historical base image remain useful, but the current source
    # does not provide a verified R/B height scale. Never invent one.
    legacy_match = re.search(r'name:\s*"Belaya".*?corner0:\s*\[([^\]]+)\].*?corner1:\s*\[([^\]]+)\]', source, re.S)
    if not legacy_match:
        raise ValueError("Missing reviewed legacy Belaya bounds")
    legacy_corners = [list(map(float, legacy_match.group(i).split(","))) for i in (1, 2)]
    result.append({"id": "belaya", "name": "Belaya Pass (Legacy)", "path": "/img/maps/belaya/",
                   "width": abs(legacy_corners[1][0] - legacy_corners[0][0]),
                   "height": abs(legacy_corners[1][1] - legacy_corners[0][1]),
                   "heightScale": 0, "heightOffset": 0, "legacy": True})
    return result


def fetch_map(map_data, output, force):
    identifier = map_data["id"]
    image_path = output / "images" / (identifier + ".jpg")
    height_path = output / "heights" / (identifier + ".png")
    if force or not image_path.exists():
        data = read_url(DATA_ORIGIN + map_data["path"] + "basemap.webp")
        with Image.open(io.BytesIO(data)) as image:
            image.convert("RGB").save(image_path.with_suffix(".jpg.part"), format="JPEG", quality=95, optimize=True)
        image_path.with_suffix(".jpg.part").replace(image_path)
    has_height = not map_data.get("legacy", False)
    if has_height and (force or not height_path.exists()):
        data = read_url(DATA_ORIGIN + map_data["path"] + "heightmap.png")
        # Decode before retaining: an HTML error response must never become a map.
        with Image.open(io.BytesIO(data)) as image:
            if image.format != "PNG" or image.mode not in ("RGB", "RGBA"):
                raise ValueError(identifier + ": expected RGB/RGBA height PNG")
            image.load()
        height_path.with_suffix(".png.part").write_bytes(data)
        height_path.with_suffix(".png.part").replace(height_path)
    with Image.open(image_path) as image:
        image.load()
        image_size = image.size
    height_size = None
    if has_height:
        with Image.open(height_path) as image:
            image.load()
            height_size = image.size
    return identifier, image_size, height_size


def asset_paths(maps):
    result = []
    identifiers = set()
    for item in maps:
        identifier = item["id"]
        if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]*", identifier) or identifier.lower() in identifiers:
            raise ValueError("Invalid or duplicate map identifier: " + identifier)
        identifiers.add(identifier.lower())
        result.append("images/" + identifier + ".jpg")
        if not item.get("legacy"):
            result.append("heights/" + identifier + ".png")
    return result


def read_catalog(output):
    try:
        return ET.parse(output / "catalog.xml").getroot()
    except (OSError, ET.ParseError):
        return None


def cache_is_valid(output, commit, maps):
    """A filename alone does not identify which metadata its pixels belong to."""
    try:
        catalog = read_catalog(output)
        if catalog is None or catalog.tag != "maps" or catalog.get("sourceCommit") != commit:
            return False
        entries = {entry.get("id"): entry for entry in catalog.findall("map")}
        if len(entries) != len(maps) or len(catalog.findall("map")) != len(maps):
            return False
        for item in maps:
            entry = entries.get(item["id"])
            if entry is None:
                return False
            for key in ("width", "height", "heightScale", "heightOffset"):
                if float(entry.get(key, "nan")) != float(item[key]):
                    return False
            if entry.get("image") != "images/" + item["id"] + ".jpg":
                return False
            expected_height = "" if item.get("legacy") else "heights/" + item["id"] + ".png"
            if entry.get("heightmap") != expected_height:
                return False
        hashes = {}
        for line in (output / "SHA256SUMS.txt").read_text(encoding="ascii").splitlines():
            match = re.fullmatch(r"([0-9a-fA-F]{64})  (.+)", line)
            if not match or match.group(2) in hashes:
                return False
            hashes[match.group(2)] = match.group(1).lower()
        if set(hashes) != set(asset_paths(maps)):
            return False
        for relative, expected_hash in hashes.items():
            path = output / relative
            if hashlib.sha256(path.read_bytes()).hexdigest() != expected_hash:
                return False
            with Image.open(path) as image:
                if relative.startswith("heights/"):
                    if image.format != "PNG" or image.mode not in ("RGB", "RGBA"):
                        return False
                elif image.format != "JPEG":
                    return False
                image.load()
        return True
    except (OSError, ValueError, SyntaxError):
        return False


def remove_work_directory(path, output, kind):
    # Delete only the particular sibling allocated for this run. Resolve again
    # so replacing it with a link cannot redirect cleanup outside that parent.
    expected_prefix = "." + output.name + "." + kind + "-"
    if path.is_symlink() or path.parent != output.parent or not path.name.startswith(expected_prefix) or path.resolve() != path:
        raise ValueError("Refusing to remove an unexpected map work directory: " + str(path))
    if path.exists():
        shutil.rmtree(path)


def new_work_directory(output, kind):
    return Path(tempfile.mkdtemp(prefix="." + output.name + "." + kind + "-", dir=output.parent)).resolve()


def publish_directory(staging, output):
    """Switch complete directories, restoring the old snapshot on rename failure."""
    backup = None
    if output.exists():
        backup = new_work_directory(output, "backup")
        backup.rmdir()  # Reserve a unique sibling name, then rename onto it.
        output.replace(backup)
    try:
        staging.replace(output)
    except BaseException:
        if backup is not None:
            try:
                backup.replace(output)
            except OSError as error:
                # Never discard the only complete old copy when an external
                # file lock also prevents rollback.
                raise RuntimeError("Map publication and rollback failed. The original maps remain at " + str(backup)) from error
        raise
    if backup is not None:
        remove_work_directory(backup, output, "backup")


def prepare_and_publish(output, commit, maps, source, height_source, force):
    asset_paths(maps)  # Validate identifiers before creating any work paths.
    if output == output.parent:
        raise ValueError("Map output cannot be a filesystem root.")
    if output.exists() and not output.is_dir():
        raise ValueError("Map output must be a directory: " + str(output))
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = new_work_directory(output, "staging")
    try:
        if output.exists():
            # Preserve application-maintained files and labels alongside the
            # assets. All downloads and generated files modify this copy only.
            shutil.copytree(output, staging, dirs_exist_ok=True)
        old_catalog = read_catalog(staging)
        labels = {} if old_catalog is None else {item.get("id"): item.get("displayName") for item in old_catalog.findall("map")}
        use_cache = not force and cache_is_valid(staging, commit, maps)
        for folder in (staging / "images", staging / "heights", staging / "source"):
            folder.mkdir(parents=True, exist_ok=True)
        (staging / "source" / "maps.js").write_text(source, encoding="utf-8")
        (staging / "source" / "squadHeightmaps.js").write_bytes(height_source)
        with ThreadPoolExecutor(max_workers=4) as executor:
            jobs = [executor.submit(fetch_map, item, staging, not use_cache) for item in maps]
            for job in as_completed(jobs):
                identifier, image_size, height_size = job.result()
                print(identifier + ": image " + str(image_size) + ", height " + str(height_size), flush=True)
        root = ET.Element("maps", {"sourceCommit": commit, "generated": date.today().isoformat(), "source": DATA_ORIGIN})
        for item in maps:
            attrs = {key: str(item[key]) for key in ("id", "name", "width", "height", "heightScale", "heightOffset")}
            attrs.update({"displayName": item["name"], "image": "images/" + item["id"] + ".jpg",
                          "heightmap": "" if item.get("legacy") else "heights/" + item["id"] + ".png"})
            if item.get("legacy"):
                attrs.update({"displayName": "贝拉亚（旧版） / Belaya", "legacy": "true"})
            if labels.get(item["id"]):
                attrs["displayName"] = labels[item["id"]]
            ET.SubElement(root, "map", attrs)
        ET.indent(root, space="  ")
        ET.ElementTree(root).write(staging / "catalog.xml", encoding="utf-8", xml_declaration=True)
        hashes = [hashlib.sha256((staging / relative).read_bytes()).hexdigest() + "  " + relative for relative in asset_paths(maps)]
        (staging / "SHA256SUMS.txt").write_text("\n".join(hashes) + "\n", encoding="ascii")
        if not cache_is_valid(staging, commit, maps):
            raise ValueError("Prepared map snapshot failed validation; existing maps were kept.")
        publish_directory(staging, output)
    finally:
        remove_work_directory(staging, output, "staging")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "assets" / "maps")
    parser.add_argument("--source-commit", default="master", help="Pinned SquadCalc Git commit; default resolves master once")
    parser.add_argument("--force", action="store_true", help="Redownload existing validated map assets")
    args = parser.parse_args()
    if not features.check("webp"):
        raise RuntimeError("This existing Pillow build has no WebP decoder. Install nothing automatically; select a Python environment with WebP support.")
    output = args.output.resolve()
    commit = args.source_commit
    if commit == "master":
        commit = json.loads(read_url("https://api.github.com/repos/" + REPO + "/commits/master"))["sha"]
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Use a full 40-character source commit")
    raw_origin = "https://raw.githubusercontent.com/" + REPO + "/" + commit
    source = read_url(raw_origin + "/src/data/maps.js").decode("utf-8")
    maps = parse_maps(source)
    height_source = read_url(raw_origin + "/src/js/squadHeightmaps.js")
    prepare_and_publish(output, commit, maps, source, height_source, args.force)
    print("Ready: " + str(len(maps)) + " offline maps. Source commit: " + commit, flush=True)


if __name__ == "__main__":
    main()
