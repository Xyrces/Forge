"""Check the text introduced by PR commits without checking out candidate files."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

from privacy_patterns import PrivacyPatterns


MAX_BLOB_BYTES = 16 * 1024 * 1024
TEXT_SUFFIXES = {".cs", ".css", ".csv", ".env", ".html", ".js", ".json", ".md", ".ps1",
                 ".py", ".razor", ".sh", ".sql", ".toml", ".ts", ".txt", ".xml", ".yaml", ".yml"}
HUNK = re.compile(rb"^@@ -[0-9]+(?:,[0-9]+)? \+([0-9]+)(?:,([0-9]+))? @@", re.MULTILINE)


class ScanError(Exception):
    pass


def git(*args):
    result = subprocess.run(["git", *args], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    if result.returncode:
        raise ScanError("Git could not read the complete comparison.")
    return result.stdout


def commit(ref):
    return git("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode("ascii").strip()


def policy_terms(required):
    try:
        values = json.loads(os.environ.get("FORGE_PRIVATE_TERMS", "[]"))
    except ValueError:
        raise ScanError("Invalid private identifier configuration.") from None
    if not isinstance(values, list) or any(
        not isinstance(v, str) or not v.strip() or v != v.strip() or not v.isprintable() for v in values
    ):
        raise ScanError("Invalid private identifier configuration.")
    if required and not values:
        raise ScanError("The repository's private identifier list is missing or empty.")
    return values


def changed_files(parent, revision):
    # Git emits NUL-delimited paths and blob IDs; names never become command arguments.
    args = ("diff-tree", "--no-commit-id", "-r", "--no-renames", "--no-abbrev", "--raw", "-z", "--diff-filter=AMT")
    fields = git(*args, parent, revision, "--") if parent else git(*args, "--root", revision, "--")
    fields = fields.split(b"\0")
    changes = {}
    for metadata, path in zip(fields[::2], fields[1::2]):
        mode_before, mode_after, old, new, operation = metadata.lstrip(b":").decode("ascii").split()
        old_blob = None if mode_before in ("000000", "160000") else old
        changes[path] = (old_blob, new if mode_after != "160000" else None, operation == "A")
    return changes


def read_text(oid, name=None):
    if oid is None:
        return ""
    if int(git("cat-file", "-s", oid)) > MAX_BLOB_BYTES:
        raise ScanError("A changed object exceeds the 16 MiB scan limit. Use Git LFS for large assets.")
    data = git("cat-file", "blob", oid)
    encoding = None
    if data.startswith((b"\xff\xfe\0\0", b"\0\0\xfe\xff")):
        encoding = "utf-32"
    elif data.startswith((b"\xff\xfe", b"\xfe\xff")):
        encoding = "utf-16"
    if encoding:
        try:
            return data.decode(encoding).replace("\r\n", "\n")
        except UnicodeError:
            raise ScanError("Invalid encoded text in a changed file.") from None
    if b"\0" in data[:8192]:
        if name and Path(name).suffix.lower() in TEXT_SUFFIXES:
            raise ScanError("A text file contains binary data. Use UTF-8 or BOM-marked Unicode.")
        return None
    try:
        return data.decode("utf-8-sig").replace("\r\n", "\n")
    except UnicodeDecodeError:
        # Existing runbooks include Windows-encoded text. ASCII markers survive single-byte decoding.
        return data.decode("cp1252", errors="replace").replace("\r\n", "\n")


def added_lines(old_text, new_text, numbered):
    """Retain only findings on lines introduced relative to this parent."""
    if not old_text:
        return numbered
    with tempfile.TemporaryDirectory() as tmp:
        before, after = Path(tmp, "before"), Path(tmp, "after")
        before.write_bytes(old_text.encode("utf-8"))
        after.write_bytes(new_text.encode("utf-8"))
        diff = subprocess.run(
            ["git", "diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color",
             "--text", "--unified=0", "--", str(before), str(after)],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
    if diff.returncode not in (0, 1):
        raise ScanError("Git could not compare changed text.")
    candidates = iter(numbered)
    item = next(candidates, None)
    result = []
    for match in HUNK.finditer(diff.stdout):
        start = int(match[1])
        end = start + int(match[2] or 1)
        while item and item[0] < end:
            if item[0] >= start:
                result.append(item)
            item = next(candidates, None)
    return result


def scan(base, head, terms, metadata=None):
    patterns = PrivacyPatterns(terms)
    private_identity = [re.compile(r"(?<!\w)" + re.escape(term) + r"(?!\w)", re.IGNORECASE) for term in terms]
    count = 0

    def report(location, labels):
        nonlocal count
        for label in labels:
            if count < 50:
                print(f"{location}: {label}")
            count += 1

    if metadata:
        try:
            fields = json.loads(Path(metadata).read_text(encoding="utf-8"))
        except (ValueError, UnicodeError):
            raise ScanError("Could not read the PR title and description.") from None
        if not isinstance(fields, dict) or not isinstance(fields.get("title"), str) or "body" not in fields:
            raise ScanError("Invalid PR title or description.")
        for key, label in (("title", "title"), ("body", "description")):
            value = fields[key]
            if key == "body" and value is None:
                continue
            if not isinstance(value, str):
                raise ScanError("Invalid PR title or description.")
            for line, content in enumerate(value.splitlines(), 1):
                report(f"PR {label}:{line}", patterns.findings(content))

    commits = git("rev-list", "--reverse", "--topo-order", head, "--not", base).decode("ascii").splitlines()
    for revision in commits:
        short = revision[:12]
        message = git("show", "-s", "--format=%B", revision).decode("utf-8", "replace")
        for line, value in enumerate(message.splitlines(), 1):
            report(f"{short} commit message:{line}", patterns.findings(value))
        identity = git("show", "-s", "--format=%an%n%ae%n%cn%n%ce", revision).decode("utf-8", "replace")
        for field in identity.splitlines():
            if any(pattern.search(field) for pattern in private_identity):
                report(f"{short} commit identity", ["private identifier"])
                break
        parent_ids = git("show", "-s", "--format=%P", revision).decode("ascii").split()
        parents = [changed_files(parent, revision) for parent in parent_ids or [None]]
        # A merge introduces a path or line only if no parent already had it.
        paths = set.intersection(*(set(parent) for parent in parents))
        for number, path in enumerate(sorted(paths), 1):
            changes = [parent[path] for parent in parents]
            name = path.decode("utf-8", "replace")
            name_labels = patterns.findings(name)
            safe_name = not name_labels and re.fullmatch(r"[A-Za-z0-9_./-]+", name)
            location = f"{short} " + (name if safe_name else f"[file {number}, name withheld]")
            if all(created for _, _, created in changes):
                report(location, name_labels)
            oid = changes[0][1]
            if oid is None or any(old == oid for old, _, _ in changes):
                continue
            text = read_text(oid, name)
            if text is None:
                continue
            numbered = []
            for line, value in enumerate(text.split("\n"), 1):
                labels = patterns.findings(value.rstrip("\r"))
                if labels:
                    numbered.append((line, labels))
            for old, _, _ in changes:
                if numbered and old:
                    numbered = added_lines(read_text(old), text, numbered)
            for line, labels in numbered:
                report(f"{location}:{line}", labels)
    if count:
        print(f"Found {count} privacy finding(s). Replace personal or deployment values with placeholders.")
        return 1
    print(f"Privacy check passed for {len(commits)} proposed commit(s).")
    return 0


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--require-private-terms", action="store_true")
    parser.add_argument("--pr-metadata", help="File containing JSON PR title and body fields")
    parser.add_argument("base")
    parser.add_argument("head")
    args = parser.parse_args()
    try:
        sys.exit(scan(commit(args.base), commit(args.head), policy_terms(args.require_private_terms), args.pr_metadata))
    except (ScanError, OSError) as exc:
        print(str(exc) if isinstance(exc, ScanError) else "Could not complete the privacy scan.", file=sys.stderr)
        sys.exit(2)
