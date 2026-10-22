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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(__file__).resolve().parents[1] / "assets" / "maps")
    parser.add_argument("--source-commit", default="master", help="Pinned SquadCalc Git commit; default resolves master once")
    parser.add_argument("--force", action="store_true", help="Redownload existing validated map assets")
    args = parser.parse_args()
    if not features.check("webp"):
        raise RuntimeError("This existing Pillow build has no WebP decoder. Install nothing automatically; select a Python environment with WebP support.")
    output = args.output.resolve()
    for folder in (output, output / "images", output / "heights", output / "source"):
        folder.mkdir(parents=True, exist_ok=True)
    commit = args.source_commit
    if commit == "master":
        commit = json.loads(read_url("https://api.github.com/repos/" + REPO + "/commits/master"))["sha"]
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Use a full 40-character source commit")
    raw_origin = "https://raw.githubusercontent.com/" + REPO + "/" + commit
    source = read_url(raw_origin + "/src/data/maps.js").decode("utf-8")
    maps = parse_maps(source)
    (output / "source" / "maps.js").write_text(source, encoding="utf-8")
    (output / "source" / "squadHeightmaps.js").write_bytes(read_url(raw_origin + "/src/js/squadHeightmaps.js"))
    root = ET.Element("maps", {"sourceCommit": commit, "generated": date.today().isoformat(), "source": DATA_ORIGIN})
    # Only replace the catalog after every map has been downloaded and decoded.
    with ThreadPoolExecutor(max_workers=4) as executor:
        jobs = {executor.submit(fetch_map, item, output, args.force): item for item in maps}
        for job in as_completed(jobs):
            identifier, image_size, height_size = job.result()
            print(identifier + ": image " + str(image_size) + ", height " + str(height_size), flush=True)
    for item in maps:
        attrs = {key: str(item[key]) for key in ("id", "name", "width", "height", "heightScale", "heightOffset")}
        attrs.update({"displayName": item["name"], "image": "images/" + item["id"] + ".jpg",
                      "heightmap": "" if item.get("legacy") else "heights/" + item["id"] + ".png"})
        if item.get("legacy"):
            attrs.update({"displayName": "贝拉亚（旧版） / Belaya", "legacy": "true"})
        # Keep additional display labels supplied by the application maintainer.
        existing = output / "catalog.xml"
        if existing.exists():
            old = ET.parse(existing).getroot().find("map[@id='" + item["id"] + "']")
            if old is not None:
                attrs["displayName"] = old.get("displayName", attrs["displayName"])
        ET.SubElement(root, "map", attrs)
    ET.indent(root, space="  ")
    ET.ElementTree(root).write(output / "catalog.xml", encoding="utf-8", xml_declaration=True)
    hashes = []
    for item in maps:
        paths = ["images/" + item["id"] + ".jpg"]
        if not item.get("legacy"):
            paths.append("heights/" + item["id"] + ".png")
        for relative in paths:
            hashes.append(hashlib.sha256((output / relative).read_bytes()).hexdigest() + "  " + relative)
    (output / "SHA256SUMS.txt").write_text("\n".join(hashes) + "\n", encoding="ascii")
    print("Ready: " + str(len(maps)) + " offline maps. Source commit: " + commit, flush=True)


if __name__ == "__main__":
    main()
