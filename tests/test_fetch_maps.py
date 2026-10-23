"""Offline regression tests for complete, recoverable map updates."""

from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

from PIL import Image


PROJECT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("fetch_maps", PROJECT / "scripts" / "fetch-maps.py")
fetch_maps = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fetch_maps)
OLD_COMMIT = "a" * 40
NEW_COMMIT = "b" * 40


def png_bytes(color):
    buffer = io.BytesIO()
    Image.new("RGB", (8, 8), color).save(buffer, format="PNG")
    return buffer.getvalue()


def snapshot(directory):
    return {path.relative_to(directory).as_posix(): path.read_bytes()
            for path in directory.rglob("*") if path.is_file()}


class MapUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="squad-map-test-")
        self.addCleanup(self.temporary.cleanup)
        self.output = Path(self.temporary.name).resolve() / "maps"
        self.maps = [{"id": "example", "name": "Example", "path": "/img/maps/example/",
                      "width": 3000, "height": 3001, "heightScale": 2, "heightOffset": -10}]
        self.payloads = {"basemap.webp": png_bytes((40, 90, 120)),
                         "heightmap.png": png_bytes((20, 0, 30))}
        self.output.mkdir()
        (self.output / "README.md").write_text("Keep this local documentation.", encoding="utf-8")
        self.update()
        catalog = ET.parse(self.output / "catalog.xml")
        catalog.getroot().find("map").set("displayName", "本地名称 / Example")
        catalog.write(self.output / "catalog.xml", encoding="utf-8", xml_declaration=True)
        self.original = snapshot(self.output)

    def read_url(self, url):
        return self.payloads[url.rsplit("/", 1)[-1]]

    def update(self, commit=OLD_COMMIT, force=False, maps=None, reader=None):
        with patch.object(fetch_maps, "read_url", side_effect=reader or self.read_url) as request:
            with redirect_stdout(io.StringIO()):
                fetch_maps.prepare_and_publish(self.output, commit, maps or self.maps,
                                               "metadata " + commit, b"height decoder", force)
            return request

    def assert_no_work_directories(self):
        self.assertEqual([], list(self.output.parent.glob(".maps.*-*")))

    def test_valid_cache_does_not_download_and_preserves_local_files(self):
        request = self.update(reader=lambda url: self.fail("Unexpected download: " + url))
        request.assert_not_called()
        self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    def test_height_download_failure_keeps_entire_previous_snapshot(self):
        requests = []

        def reader(url):
            requests.append(url)
            if url.endswith("heightmap.png"):
                raise OSError("Simulated interrupted height download")
            return png_bytes((200, 140, 90))

        with self.assertRaisesRegex(OSError, "interrupted height"):
            self.update(force=True, reader=reader)
        self.assertEqual(2, len(requests))  # The new base image was already decoded and saved.
        self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    def test_invalid_height_response_keeps_previous_snapshot(self):
        self.payloads["heightmap.png"] = b"<html>server error</html>"
        with self.assertRaises(OSError):
            self.update(force=True)
        self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    def test_new_commit_redownloads_both_assets_without_force(self):
        self.payloads = {"basemap.webp": png_bytes((150, 80, 40)),
                         "heightmap.png": png_bytes((100, 0, 20))}
        request = self.update(commit=NEW_COMMIT)
        self.assertEqual(2, request.call_count)
        self.assertNotEqual(self.original["images/example.jpg"],
                            (self.output / "images/example.jpg").read_bytes())
        self.assertEqual(self.payloads["heightmap.png"], (self.output / "heights/example.png").read_bytes())
        self.assertTrue(fetch_maps.cache_is_valid(self.output, NEW_COMMIT, self.maps))
        self.assertEqual("本地名称 / Example", ET.parse(self.output / "catalog.xml").getroot().find("map").get("displayName"))
        self.assertEqual(self.original["README.md"], (self.output / "README.md").read_bytes())
        self.assert_no_work_directories()

    def test_changed_metadata_requires_fresh_assets(self):
        changed = [dict(self.maps[0], heightScale=4, width=4000)]
        self.assertFalse(fetch_maps.cache_is_valid(self.output, OLD_COMMIT, changed))
        self.assertEqual(2, self.update(maps=changed).call_count)
        self.assertTrue(fetch_maps.cache_is_valid(self.output, OLD_COMMIT, changed))

    def test_failed_new_commit_never_relabels_old_pixels(self):
        with self.assertRaisesRegex(OSError, "offline"):
            self.update(commit=NEW_COMMIT, reader=lambda url: self.raise_offline())
        self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    @staticmethod
    def raise_offline():
        raise OSError("offline")

    def test_hash_mismatch_is_repaired_with_download(self):
        path = self.output / "heights/example.png"
        path.write_bytes(png_bytes((1, 2, 3)))
        self.assertFalse(fetch_maps.cache_is_valid(self.output, OLD_COMMIT, self.maps))
        self.assertEqual(2, self.update().call_count)
        self.assertEqual(self.payloads["heightmap.png"], path.read_bytes())

    def test_matching_hash_does_not_allow_undecodable_image(self):
        path = self.output / "images/example.jpg"
        path.write_bytes(b"invalid image")
        checksums = [hashlib.sha256((self.output / relative).read_bytes()).hexdigest() + "  " + relative
                     for relative in fetch_maps.asset_paths(self.maps)]
        (self.output / "SHA256SUMS.txt").write_text("\n".join(checksums) + "\n", encoding="ascii")
        self.assertFalse(fetch_maps.cache_is_valid(self.output, OLD_COMMIT, self.maps))
        self.assertEqual(2, self.update().call_count)

    def test_failed_publication_rolls_back_previous_directory(self):
        original_replace = Path.replace

        def replace(path, target):
            if path.name.startswith(".maps.staging-") and target == self.output:
                raise PermissionError("Simulated publish lock")
            return original_replace(path, target)

        with patch.object(Path, "replace", replace):
            with self.assertRaisesRegex(PermissionError, "publish lock"):
                self.update(force=True)
        self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    def test_failed_rollback_retains_and_identifies_complete_backup(self):
        original_replace = Path.replace

        def replace(path, target):
            if target == self.output:
                raise PermissionError("Simulated publication and restore lock")
            return original_replace(path, target)

        with patch.object(Path, "replace", replace):
            with self.assertRaises(RuntimeError) as failure:
                self.update(force=True)
        backups = list(self.output.parent.glob(".maps.backup-*"))
        self.assertEqual(1, len(backups))
        self.assertIn(str(backups[0]), str(failure.exception))
        self.assertEqual(self.original, snapshot(backups[0]))
        self.assertEqual([], list(self.output.parent.glob(".maps.staging-*")))

    def test_first_download_failure_keeps_existing_documentation(self):
        empty = self.output.parent / "new-maps"
        empty.mkdir()
        (empty / "README.md").write_text("Keep me", encoding="utf-8")
        before = snapshot(empty)
        with patch.object(fetch_maps, "read_url", side_effect=OSError("offline")):
            with self.assertRaises(OSError):
                fetch_maps.prepare_and_publish(empty, OLD_COMMIT, self.maps, "source", b"source", False)
        self.assertEqual(before, snapshot(empty))
        self.assertEqual([], list(empty.parent.glob(".new-maps.*-*")))

    def test_legacy_map_needs_no_height_download(self):
        legacy = [dict(self.maps[0], legacy=True, heightScale=0)]
        request = self.update(maps=legacy)
        self.assertEqual(1, request.call_count)
        self.assertTrue(request.call_args.args[0].endswith("basemap.webp"))
        self.assertEqual("", ET.parse(self.output / "catalog.xml").getroot().find("map").get("heightmap"))
        self.assertTrue(fetch_maps.cache_is_valid(self.output, OLD_COMMIT, legacy))

    def test_invalid_or_duplicate_identifiers_leave_original_untouched(self):
        for maps in ([dict(self.maps[0], id="../outside")],
                     [self.maps[0], dict(self.maps[0], id="EXAMPLE")]):
            with self.subTest(maps=maps):
                with self.assertRaisesRegex(ValueError, "identifier"):
                    self.update(maps=maps)
                self.assertEqual(self.original, snapshot(self.output))
        self.assert_no_work_directories()

    def test_cleanup_refuses_unrelated_directory(self):
        unrelated = self.output.parent / "unrelated"
        unrelated.mkdir()
        (unrelated / "keep.txt").write_text("Untouched", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            fetch_maps.remove_work_directory(unrelated, self.output, "staging")
        self.assertEqual("Untouched", (unrelated / "keep.txt").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
