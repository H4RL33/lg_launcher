"""Build the launcher advisory document from a committed OptiScaler wiki checkout."""

import argparse
import html
import json
from pathlib import Path
import re
import subprocess
import unicodedata
from urllib.parse import unquote, urlsplit


WIKI_FILE = "Compatibility-List.md"
WIKI_PATH = "/optiscaler/optiscaler/wiki/"
PRIMARY = "Compatibility List"
THIRD_PARTY = "Upscaler mods support"
LUMA = "Luma Unreal Engine"
MINIMUM_ROWS = {PRIMARY: 600, THIRD_PARTY: 16, LUMA: 19}
PRIMARY_COLUMNS = ("game", "compatibility", "upscaler inputs", "optipatcher support", "notes", "images")
SUPPLEMENT_COLUMNS = ("game", "compatibility", "upscaler inputs", "notes", "images")
ADVISORIES = {
    THIRD_PARTY: "The wiki lists this game under third-party upscaler mods.",
    LUMA: "The wiki lists this game under the Luma Unreal Engine mod.",
}
MARKERS = {
    "✅": "", "✔": "", "❌": "The wiki reports this game as not working.",
    "⛔": "The wiki reports this game as not working.",
    "➖": "The wiki reports limited platform compatibility.",
    "⚠": "The wiki reports a compatibility caveat.",
}
MARKDOWN_LINK = re.compile(r"\[([^\]\n]+)\]\((<?(?:[^()<>\s]|\([^()\n]*\))+>?)\)")
TABLE_PIPE = re.compile(r"(?<!\\)\|")
REVISION = re.compile(r"[a-fA-F0-9]{40}")
MAX_BYTES = 2 * 1024 * 1024
MAX_INTEGER = 2_147_483_647
ENTRY_LENGTHS = {"title": 256, "page": 160, "inputs": 256, "note": 4096}
ENTRY_KEYS = {*ENTRY_LENGTHS, "status", "steamId"}


def plain_text(markdown):
    text = MARKDOWN_LINK.sub(r"\1", markdown)
    text = re.sub(r"<[a-zA-Z/][^<>]*>", " ", text)
    text = re.sub(r"(\*\*|__|~~)(\S.*?)\1|(?<!\w)(\*|_)(\S.*?)\3(?!\w)",
                  lambda match: match[2] or match[4], text)
    text = re.sub(r"(?<!\\)\x60([^\x60\n]+)\x60", r"\1", text)
    text = re.sub(r"\\([\\|[\]*_])", r"\1", text)
    return " ".join(html.unescape(text).split())


def table_cells(line):
    cells = TABLE_PIPE.split(line.strip())
    if cells[0] == "":
        cells = cells[1:]
    if cells and cells[-1] == "":
        cells = cells[:-1]
    return [cell.strip() for cell in cells]


def page_slug_is_valid(page):
    return page not in (".", "..") and all(
        character in "-_.'&()‐–" or character.isalnum() for character in page
    )


def game_link(target):
    try:
        url = urlsplit(target.strip("<>"))
        if url.scheme or url.netloc:
            if url.scheme != "https" or url.query:
                return "", 0
            if url.netloc.lower() == "store.steampowered.com":
                steam_id = re.match(r"/app/([0-9]{1,10})(?:/|$)", url.path)
                if steam_id and 0 < int(steam_id[1]) <= MAX_INTEGER:
                    return "", int(steam_id[1])
            if url.netloc.lower() != "github.com" or not url.path.lower().startswith(WIKI_PATH):
                return "", 0
            path = url.path[len(WIKI_PATH):]
        else:
            if url.query:
                return "", 0
            path = url.path
        page = unquote(path, errors="strict")
        return (page if page_slug_is_valid(page) else ""), 0
    except ValueError:
        return "", 0


def title_key(title):
    title = unicodedata.normalize("NFKC", title.replace("™", "").replace("®", ""))
    return "".join(character for character in title if character.isalnum()).upper()


def parse_entry(cells, section, line_number):
    title, marker, inputs = cells[:3]
    marker = plain_text(marker).replace("\ufe0f", "")
    if marker not in MARKERS:
        raise ValueError(f"Unrecognized compatibility marker at line {line_number}.")
    link = MARKDOWN_LINK.fullmatch(title)
    page, steam_id = game_link(link[2]) if link else ("", 0)
    note = " ".join(part for part in (
        MARKERS[marker], ADVISORIES.get(section, ""), plain_text(cells[-2])
    ) if part)
    return {
        "title": plain_text(title), "steamId": steam_id, "status": "Notes" if note else "Reported",
        "page": page, "inputs": plain_text(inputs), "note": note,
    }


def add_entry(entries, entry):
    key = title_key(entry["title"])
    if not key:
        raise ValueError("A compatibility entry is missing an identifiable game title.")
    previous = entries.get(key)
    if previous is None:
        entries[key] = entry
        return
    if previous["steamId"] and entry["steamId"] and previous["steamId"] != entry["steamId"]:
        raise ValueError("Matching game titles have conflicting Steam IDs.")
    previous["steamId"] = previous["steamId"] or entry["steamId"]
    previous["page"] = previous["page"] or entry["page"]
    upscalers = previous["inputs"].split(", ") + entry["inputs"].split(", ")
    previous["inputs"] = ", ".join(dict.fromkeys(value for value in upscalers if value))
    if entry["note"] and entry["note"] != previous["note"]:
        previous["note"] = " ".join(filter(None, (previous["note"], entry["note"])))
        previous["status"] = "Notes"


def parse_wiki(markdown):
    markdown = re.sub(r"<!--.*?-->", lambda match: "\n" * match[0].count("\n"), markdown, flags=re.S)
    entries = {}
    counts = {}
    pending_header = None
    section = PRIMARY
    in_table = False
    for line_number, line in enumerate(markdown.splitlines(), 1):
        cells = table_cells(line)
        if pending_header:
            if len(cells) != len(pending_header) or any(
                not re.fullmatch(r":?-{3,}:?", cell) for cell in cells
            ):
                raise ValueError(f"Invalid {section} table separator at line {line_number}.")
            pending_header = None
            in_table = True
            continue
        if line.startswith("## "):
            heading = plain_text(line[3:])
            section = heading if heading in ADVISORIES else PRIMARY
        if not cells or len(cells) == 1:
            in_table = False
            continue
        columns = PRIMARY_COLUMNS if section == PRIMARY else SUPPLEMENT_COLUMNS
        if plain_text(cells[0]).casefold() == "game":
            header = tuple(plain_text(cell).casefold() for cell in cells)
            if section in counts or header != columns:
                raise ValueError(f"Unexpected {section} table header at line {line_number}.")
            counts[section] = 0
            pending_header = columns
        elif in_table:
            # GFM fills a missing last cell and ignores an extra screenshot after an empty Images cell.
            if len(cells) == len(columns) - 1:
                cells.append("")
            if len(cells) == len(columns) + 1 and not cells[-2] and MARKDOWN_LINK.fullmatch(cells[-1]):
                cells.pop()
            if len(cells) != len(columns):
                raise ValueError(f"Invalid {section} table row at line {line_number}.")
            add_entry(entries, parse_entry(cells, section, line_number))
            counts[section] += 1
        elif plain_text(cells[1]).replace("\ufe0f", "") in MARKERS:
            raise ValueError(f"Unexpected compatibility table row at line {line_number}.")
    for expected in MINIMUM_ROWS:
        if not counts.get(expected):
            raise ValueError(f"Missing {expected} table header or entries.")
    return [entries[key] for key in sorted(entries)], counts


def validate_catalog(document):
    if (not isinstance(document, dict) or set(document) != {"schemaVersion", "version", "revision", "entries"}
            or type(document["schemaVersion"]) is not int or document["schemaVersion"] != 1
            or type(document["version"]) is not int or not 1 <= document["version"] <= MAX_INTEGER
            or not isinstance(document["revision"], str) or not REVISION.fullmatch(document["revision"])
            or not isinstance(document["entries"], list) or not 1 <= len(document["entries"]) <= 10_000):
        raise ValueError("Invalid catalog schema, version, revision, or entry count.")

    seen_titles = set()
    seen_steam_ids = set()
    for entry in document["entries"]:
        if (not isinstance(entry, dict) or set(entry) != ENTRY_KEYS
                or entry["status"] not in ("Reported", "Notes")
                or type(entry["steamId"]) is not int or not 0 <= entry["steamId"] <= MAX_INTEGER):
            raise ValueError("Invalid catalog entry fields.")
        for field, limit in ENTRY_LENGTHS.items():
            value = entry[field]
            if (not isinstance(value, str) or len(value.encode("utf-16-le")) > 2 * limit
                    or any(unicodedata.category(character) == "Cc" for character in value)):
                raise ValueError(f"Invalid catalog entry {field}.")
        key = title_key(entry["title"])
        if not key or not page_slug_is_valid(entry["page"]) or key in seen_titles:
            raise ValueError("Invalid or duplicate catalog entry title or wiki page.")
        seen_titles.add(key)
        steam_id = entry["steamId"]
        if steam_id:
            if steam_id in seen_steam_ids:
                raise ValueError("Duplicate catalog Steam ID.")
            seen_steam_ids.add(steam_id)


def read_catalog(payload):
    if len(payload) > MAX_BYTES:
        raise ValueError("Catalog exceeds the 2 MiB limit.")
    document = json.loads(payload.decode("utf-8"))
    validate_catalog(document)
    return document


def build_catalog(entries, revision, previous=None):
    document = {"schemaVersion": 1, "version": 1, "revision": revision, "entries": entries}
    validate_catalog(document)
    if previous is not None:
        old_document = read_catalog(previous)
        old_entries = {title_key(entry["title"]): entry for entry in old_document["entries"]}
        new_entries = {title_key(entry["title"]): entry for entry in entries}
        if new_entries == old_entries:
            return previous
        if len(old_entries.keys() - new_entries.keys()) * 10 > len(old_entries):
            raise ValueError("Update removes more than 10% of the published game titles.")
        document["version"] = old_document["version"] + 1
        if document["version"] > MAX_INTEGER:
            raise ValueError("Catalog version is out of range.")

    lines = ",\n".join("    " + json.dumps(entry, ensure_ascii=False, separators=(",", ":")) for entry in entries)
    payload = (
        '{\n  "schemaVersion": 1,\n'
        f'  "version": {document["version"]},\n  "revision": "{revision}",\n'
        f'  "entries": [\n{lines}\n  ]\n}}\n'
    ).encode("utf-8")
    if len(payload) > MAX_BYTES:
        raise ValueError("Catalog exceeds the 2 MiB limit.")
    return payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wiki-dir", type=Path, required=True, help="Official OptiScaler wiki Git checkout")
    parser.add_argument("--output", type=Path, required=True, help="Destination for the validated catalog")
    parser.add_argument("--previous", type=Path, help="Current published document for a forward update")
    args = parser.parse_args()
    try:
        git = ["git", "-C", str(args.wiki_dir)]
        revision = subprocess.check_output(git + ["rev-parse", "--verify", "HEAD"]).decode("ascii").strip()
        if not REVISION.fullmatch(revision):
            raise ValueError("Expected a 40-character wiki Git revision.")
        markdown = subprocess.check_output(git + ["show", f"{revision}:{WIKI_FILE}"]).decode("utf-8")
        entries, counts = parse_wiki(markdown)
        for section, minimum in MINIMUM_ROWS.items():
            if counts[section] < minimum:
                raise ValueError(f"{section} has only {counts[section]} rows. Expected at least {minimum}.")
        previous = args.previous.read_bytes() if args.previous is not None else None
        payload = build_catalog(entries, revision, previous)
        args.output.write_bytes(payload)
        version = json.loads(payload)["version"]
        print(f"Wiki rows: {counts}. Catalog version {version} has {len(entries)} entries.")
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{parser.prog}: {error}\n")


if __name__ == "__main__":
    main()
