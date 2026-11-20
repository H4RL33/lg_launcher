import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))

from import_optiscaler_catalog import build_catalog, parse_wiki, validate_catalog


REVISION = "a" * 40
FIXTURE = Path(__file__).with_name("fixtures") / "optiscaler-compatibility.md"


class WikiParserTests(unittest.TestCase):
    def setUp(self):
        self.markdown = FIXTURE.read_text(encoding="utf-8")

    def test_reads_primary_table_and_preserves_safe_wiki_links(self):
        entries, counts = parse_wiki(self.markdown)
        by_title = {entry["title"]: entry for entry in entries}

        self.assertEqual(list(counts.values()), [8, 2, 1])
        self.assertEqual(len(entries), 10)
        self.assertNotIn("Template game", by_title)
        self.assertEqual(by_title["First Game"], {
            "title": "First Game", "steamId": 0, "status": "Reported",
            "page": "First-Game-(2026)", "inputs": "DLSS, XeSS", "note": ""
        })
        self.assertEqual(by_title["Second Game"]["page"], "Second-Game-Δ")
        self.assertEqual(by_title["Store Game"]["steamId"], 12345)
        self.assertEqual(by_title["Untrusted Page"]["page"], "")
        self.assertEqual(by_title["Encoded Path"]["page"], "")

    def test_caveats_are_advisory_notes(self):
        entries, _ = parse_wiki(self.markdown)
        by_title = {entry["title"]: entry for entry in entries}

        self.assertEqual(by_title["Incompatible"]["status"], "Notes")
        self.assertIn("not working", by_title["Incompatible"]["note"])
        self.assertEqual(by_title["Different OS"]["status"], "Notes")
        self.assertIn("Linux only.", by_title["Different OS"]["note"])
        self.assertEqual(by_title["Third Party"]["status"], "Notes")
        self.assertIn("Requires the Example Mod.", by_title["Third Party"]["note"])
        self.assertEqual(by_title["Engine Game"]["status"], "Notes")
        self.assertIn("Luma", by_title["Engine Game"]["note"])

    def test_merges_a_title_across_sections_without_losing_caveats(self):
        entries, _ = parse_wiki(self.markdown)
        matches = [entry for entry in entries if entry["title"] == "Online Match"]

        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0]["status"], "Notes")
        self.assertEqual(matches[0]["inputs"], "DLSS, FSR3.1")
        self.assertIn("Online only Read the notes.", matches[0]["note"])
        self.assertIn("Networking caveat | see the notes.", matches[0]["note"])

    def test_rejects_missing_supplement(self):
        with self.assertRaisesRegex(ValueError, "Luma Unreal Engine"):
            parse_wiki(self.markdown.split("## Luma Unreal Engine")[0])

    def test_rejects_an_incompatible_table_header(self):
        with self.assertRaisesRegex(ValueError, "header"):
            parse_wiki(self.markdown.replace("| OptiPatcher <br>Support |", ""))

    def test_rejects_an_unknown_compatibility_marker(self):
        with self.assertRaisesRegex(ValueError, "compatibility marker"):
            parse_wiki(self.markdown.replace("| ❌ |", "| MAYBE |"))

    def test_rejects_a_table_interrupted_by_a_blank_line(self):
        with self.assertRaisesRegex(ValueError, "table row"):
            parse_wiki(self.markdown.replace("| Online Match | ✅ | DLSS", "\n| Online Match | ✅ | DLSS"))


class CatalogPublicationTests(unittest.TestCase):
    def setUp(self):
        self.entries, _ = parse_wiki(FIXTURE.read_text(encoding="utf-8"))

    def test_bootstraps_a_versioned_advisory_document(self):
        payload = json.loads(build_catalog(self.entries, REVISION))

        self.assertEqual(set(payload), {"schemaVersion", "version", "revision", "entries"})
        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["version"], 1)
        self.assertEqual(payload["revision"], REVISION)
        self.assertEqual(payload["entries"], self.entries)

    def test_keeps_previous_bytes_when_only_wiki_revision_changes(self):
        previous = build_catalog(self.entries, REVISION)

        self.assertEqual(build_catalog(self.entries, "b" * 40, previous), previous)

    def test_versions_only_changed_entries_forward(self):
        previous = build_catalog(self.entries, REVISION)
        changed = [dict(entry) for entry in self.entries]
        changed[0]["note"] = "Revised advisory"

        payload = json.loads(build_catalog(changed, "b" * 40, previous))

        self.assertEqual(payload["version"], 2)
        self.assertEqual(payload["revision"], "b" * 40)
        self.assertEqual(payload["entries"], changed)

    def test_rejects_large_title_loss_even_when_replaced_by_new_titles(self):
        previous = build_catalog(self.entries, REVISION)
        changed = [dict(entry, title="Replacement " + str(index))
                   for index, entry in enumerate(self.entries)]
        with self.assertRaisesRegex(ValueError, "removes"):
            build_catalog(changed, REVISION, previous)

    def test_rejects_invalid_document_limits_and_fields(self):
        document = json.loads(build_catalog(self.entries, REVISION))
        for replacement in (
            {"page": "Route%2FPath"}, {"page": "Title²"}, {"page": "Title𠀀"},
            {"note": "a" * 4097}, {"title": "🧪" * 129},
            {"steamId": True}, {"status": "Blocked"}, {"recipe": {}}
        ):
            with self.subTest(replacement=list(replacement)):
                malformed = dict(document, entries=[dict(self.entries[0], **replacement)])
                with self.assertRaises(ValueError):
                    validate_catalog(malformed)

    def test_rejects_document_over_two_mibibytes(self):
        oversized = [dict(self.entries[0], title=f"Game {index}", note="a" * 4096)
                     for index in range(510)]
        with self.assertRaisesRegex(ValueError, "2 MiB"):
            build_catalog(oversized, REVISION)

    def test_increments_a_version_larger_than_signed_32_bits(self):
        previous = json.loads(build_catalog(self.entries, REVISION))
        previous["version"] = 2_147_483_648
        changed = [dict(entry, note="Revised advisory") for entry in self.entries]

        updated = json.loads(build_catalog(changed, REVISION, json.dumps(previous).encode("utf-8")))

        self.assertEqual(updated["version"], 2_147_483_649)


if __name__ == "__main__":
    unittest.main()
