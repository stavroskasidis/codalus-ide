#!/usr/bin/env python3
"""The rename from Daedalus Studio to Codalus IDE (M39, ADR 0056), kept for the record.

Run once from the repository's root on a clean tree: python eng/rename.py [--check]
- Paths: every tracked path with the old name moves (DaedalusStudio -> Codalus, daedalus-studio -> codalus,
  Daedalus -> Codalus, daedalus -> codalus).
- Text: the ordered replacements of RULES in every tracked text file, except the history (ADRs, checkpoints, the
  changelog's old entries, the name research) and the roadmap's M39 section, which records the old names.
- --check: list what still says daedalus outside the history (nothing, after the rename and its manual steps).
"""
import os
import re
import subprocess
import sys

# History keeps the old name as it was written.
EXCLUDED_PREFIXES = ("docs/adr/", "docs/checkpoints/")
EXCLUDED_FILES = {
    "CHANGELOG.md",
    "docs/release/name-conflicts.md",
    "docs/release/nuget-prefix-reservation.md",
    "eng/rename.py",
}

# Substrings that keep the old name: the maintainer's clones (folders on disk, not renamed by this).
PROTECTED = [
    "Projects/daedalus-studio",
    "Projects\\daedalus-studio",
    "Projects\\\\daedalus-studio",
]

# Ordered: the specific before the general. Each rule's test of what it must not touch is in test_rules().
RULES = [
    (r"stavroskasidis/daedalus-studio", "stavroskasidis/codalus-ide"),        # the repository
    (r"stavroskasidis\.com/daedalus-studio", "stavroskasidis.com/codalus-ide"),  # the site
    (r"stavroskasidis\.github\.io/daedalus-studio", "stavroskasidis.github.io/codalus-ide"),
    (r"daedalus-studio-bin", "codalus-bin"),                                    # the AUR package
    (r"DaedalusStudio", "Codalus"),                                             # namespaces, assemblies, ids
    (r"Daedalus Studio", "Codalus IDE"),                                        # the product
    (r"daedalus-studio", "codalus"),                                            # packages, folders, files
    (r"daedalusstudio", "codalus"),
    (r"DAEDALUS", "CODALUS"),                                                   # environment variables
    (r"Daedalus", "Codalus"),
    (r"daedalus", "codalus"),                                                   # the CLI, hosts, ids, methods
]

PATH_RULES = [
    ("DaedalusStudio", "Codalus"),
    ("daedalus-studio", "codalus"),
    ("Daedalus", "Codalus"),
    ("daedalus", "codalus"),
]


def replace_text(text):
    holders = {}
    for i, protected in enumerate(PROTECTED):
        holder = f"\x00PROTECTED{i}\x00"
        if protected in text:
            holders[holder] = protected
            text = text.replace(protected, holder)
    for pattern, replacement in RULES:
        text = re.sub(pattern, replacement, text)
    for holder, protected in holders.items():
        text = text.replace(holder, protected)
    return text


def replace_path(path):
    for old, new in PATH_RULES:
        path = path.replace(old, new)
    return path


def test_rules():
    cases = {
        "https://github.com/stavroskasidis/daedalus-studio/releases": "https://github.com/stavroskasidis/codalus-ide/releases",
        "https://stavroskasidis.com/daedalus-studio/download": "https://stavroskasidis.com/codalus-ide/download",
        "yay -S daedalus-studio-bin": "yay -S codalus-bin",
        "namespace DaedalusStudio.Workbench;": "namespace Codalus.Workbench;",
        "IDaedalusPlugin": "ICodalusPlugin",
        "Welcome to Daedalus Studio.": "Welcome to Codalus IDE.",
        "/opt/daedalus-studio/daedalus": "/opt/codalus/codalus",
        "DAEDALUS_LSHOST": "CODALUS_LSHOST",
        "daedalus/references": "codalus/references",
        ".daedalus/tasks.json": ".codalus/tasks.json",
        "daedalus-lshost": "codalus-lshost",
        "the clone is /Volumes/M2/Projects/daedalus-studio here": "the clone is /Volumes/M2/Projects/daedalus-studio here",
        "D:\\Projects\\daedalus-studio\\src": "D:\\Projects\\daedalus-studio\\src",
        "Codalus and Daedalus": "Codalus and Codalus",
    }
    for before, after in cases.items():
        got = replace_text(before)
        assert got == after, f"{before!r} -> {got!r}, not {after!r}"
    assert replace_path("src/DaedalusStudio.Shell/ShellHost.cs") == "src/Codalus.Shell/ShellHost.cs"
    assert replace_path("eng/packaging/linux/daedalus-studio.desktop") == "eng/packaging/linux/codalus.desktop"
    assert replace_path("assets/logo/png/daedalus-256.png") == "assets/logo/png/codalus-256.png"


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout.decode("utf-8")
    return [p for p in out.split("\0") if p]


def excluded(path):
    return path in EXCLUDED_FILES or path.startswith(EXCLUDED_PREFIXES)


def rename_roadmap(text):
    """The roadmap without its M39 section, which records the old and new names side by side."""
    start = text.find("### M39")
    if start < 0:
        return replace_text(text)
    end = text.find("\n### ", start + 1)
    end = len(text) if end < 0 else end
    return replace_text(text[:start]) + text[start:end] + replace_text(text[end:])


def main():
    test_rules()
    files = tracked()
    if "--check" in sys.argv:
        left = []
        for path in files:
            if excluded(path) or path == "docs/roadmap.md":
                continue
            if "daedalus" in path.lower():
                left.append(path + " (path)")
            try:
                with open(path, encoding="utf-8") as f:
                    text = f.read()
            except (UnicodeDecodeError, FileNotFoundError):
                continue
            for number, line in enumerate(text.splitlines(), 1):
                if "daedalus" in line.lower() and not any(p in line for p in PROTECTED):
                    left.append(f"{path}:{number}: {line.strip()[:140]}")
        print("\n".join(left) if left else "Nothing says daedalus outside the history.")
        return 1 if left else 0

    changed = 0
    for path in files:
        if excluded(path):
            continue
        # A few test files hold bytes that aren't UTF-8 on purpose: those are read and written as Latin-1.
        encoding = "utf-8"
        try:
            with open(path, encoding=encoding, newline="") as f:
                text = f.read()
        except UnicodeDecodeError:
            encoding = "latin-1"
            with open(path, encoding=encoding, newline="") as f:
                text = f.read()
        if "\0" in text:
            continue
        new = rename_roadmap(text) if path == "docs/roadmap.md" else replace_text(text)
        if new != text:
            with open(path, "w", encoding=encoding, newline="") as f:
                f.write(new)
            changed += 1
    print(f"text: {changed} files")

    moved = 0
    for path in files:
        if excluded(path):
            continue
        target = replace_path(path)
        if target != path:
            os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
            os.replace(path, target)
            moved += 1
    # Folders left empty by the moves.
    for root, dirs, entries in sorted(os.walk(".", topdown=False), key=lambda w: -len(w[0])):
        if ".git" in root.split(os.sep):
            continue
        if not os.listdir(root) and root != ".":
            os.rmdir(root)
    print(f"paths: {moved} files")
    subprocess.run(["git", "add", "-A"], check=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
