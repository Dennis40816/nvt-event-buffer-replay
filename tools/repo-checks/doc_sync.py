# Copyright (c) 2026 Dennis Liu. All rights reserved.
"""Check documentation against a caller-selected Git diff and policy."""

from __future__ import annotations

import argparse
import json
import os
import posixpath
import re
import subprocess
import sys
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import unquote


CHECKS = ("mapping", "bilingual", "module-list", "links")
CAPTURE = re.compile(r"\{([A-Za-z_]\w*)\}")


@dataclass(frozen=True)
class Finding:
    check: str
    message: str
    path: str | None = None
    line: int | None = None


class Git:
    """Read committed paths and contents without consulting the working tree."""

    def __init__(self, root: Path) -> None:
        self.root = root

    def run(self, *args: str) -> str:
        result = subprocess.run(
            ["git", "-C", str(self.root), *args], capture_output=True,
            env={**os.environ, "GIT_NO_LAZY_FETCH": "1"},
        )
        if result.returncode:
            raise ValueError("git: " + result.stderr.decode("utf-8", errors="replace").strip())
        return result.stdout.decode("utf-8-sig")

    def commit(self, ref: str) -> str:
        return self.run("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").strip()

    def files(self, ref: str) -> set[str]:
        return set(filter(None, self.run("ls-tree", "-r", "--name-only", "-z", ref).split("\0")))

    def read(self, ref: str, path: str) -> str:
        return self.run("show", f"{ref}:{path}")


def changed_paths(git: Git, base: str, head: str) -> tuple[set[str], set[str]]:
    """Return changed paths and old paths removed by deletions or renames."""
    fields = git.run("diff", "--name-status", "-z", "-M", f"{base}...{head}").split("\0")
    changed: set[str] = set()
    removed: set[str] = set()
    index = 0
    while index < len(fields) - 1:
        status, path = fields[index:index + 2]
        changed.add(path)
        index += 2
        if status.startswith(("R", "C")):
            changed.add(fields[index])
            index += 1
        if status.startswith(("D", "R")):
            removed.add(path)
    return changed, removed


def path_pattern(pattern: str) -> re.Pattern[str]:
    """Compile segment globs, including zero-segment globstars and captures."""
    parts = []
    for part in pattern.split("/"):
        if part != "**" or not parts or parts[-1] != "**":
            parts.append(part)
    expression = ""
    names: set[str] = set()
    for index, part in enumerate(parts):
        separator = "/" if index and parts[index - 1] != "**" else ""
        if part == "**":
            if index == len(parts) - 1:
                expression += "(?:/[^/]+)*" if index else "(?:[^/]+(?:/[^/]+)*)?"
            else:
                expression += separator + "(?:[^/]+/)*"
            continue
        match = CAPTURE.fullmatch(part)
        if match:
            name = match[1]
            expression += separator + (f"(?P={name})" if name in names else f"(?P<{name}>[^/]+)")
            names.add(name)
        else:
            expression += separator + ("[^/]+" if part == "*" else re.escape(part))
    return re.compile(expression)


def _object(value: object, keys: set[str], location: str) -> dict:
    if not isinstance(value, dict):
        raise ValueError(f"config {location}: expected an object")
    unknown, missing = value.keys() - keys, keys - value.keys()
    if unknown:
        raise ValueError(f"config {location}: unknown keys: {', '.join(sorted(unknown))}")
    if missing:
        raise ValueError(f"config {location}: missing keys: {', '.join(sorted(missing))}")
    return value


def _strings(value: object, location: str) -> list[str]:
    if not isinstance(value, list) or any(not isinstance(item, str) or not item.strip() for item in value):
        raise ValueError(f"config {location}: expected an array of nonempty strings")
    return value


def _path(value: str, location: str) -> None:
    if value.startswith("/") or "\\" in value or ":" in value or ".." in value.split("/"):
        raise ValueError(f"config {location}: expected a repository-relative /-separated path")


def _template(value: str, location: str) -> set[str]:
    if any(char in CAPTURE.sub("", value) for char in "{}"):
        raise ValueError(f"config {location}: expected plain {{name}} substitutions")
    return set(CAPTURE.findall(value))


def load_config(path: Path) -> dict:
    """Load the closed version-one schema, accepting a UTF-8 BOM."""
    config = _object(json.loads(path.read_text(encoding="utf-8-sig")),
                     {"version", "mappings", "bilingual", "moduleLists"}, "root")
    if type(config["version"]) is not int or config["version"] != 1:
        raise ValueError("config version: expected integer 1")
    if not isinstance(config["mappings"], list):
        raise ValueError("config mappings: expected an array")
    for index, entry in enumerate(config["mappings"]):
        location = f"mappings[{index}]"
        rule = _object(entry, {"name", "paths", "docs"}, location)
        if not isinstance(rule["name"], str) or not rule["name"].strip():
            raise ValueError(f"config {location}.name: expected a nonempty string")
        patterns = _strings(rule["paths"], location + ".paths")
        docs = _strings(rule["docs"], location + ".docs")
        for document in docs:
            _path(document, location + ".docs")
            _template(document, location + ".docs")
        for pattern in patterns:
            _path(pattern, location + ".paths")
            names = _template(pattern, location + ".paths")
            if any("{" in part and not CAPTURE.fullmatch(part) for part in pattern.split("/")):
                raise ValueError(f"config {location}.paths: captures must occupy a whole segment")
            path_pattern(pattern)
            for document in docs:
                if not set(CAPTURE.findall(document)) <= names:
                    raise ValueError(f"config {location}.docs: invalid capture template {document}")
    bilingual = _object(config["bilingual"], {"exclude"}, "bilingual")
    for pattern in _strings(bilingual["exclude"], "bilingual.exclude"):
        _path(pattern, "bilingual.exclude")
        path_pattern(pattern)
    modules = _object(config["moduleLists"], {"roots", "lists"}, "moduleLists")
    for root in _strings(modules["roots"], "moduleLists.roots"):
        _path(root, "moduleLists.roots")
        if root.endswith("/") or any(char in root for char in "*{}"):
            raise ValueError("config moduleLists.roots: expected literal folder paths without a trailing /")
    if not isinstance(modules["lists"], list):
        raise ValueError("config moduleLists.lists: expected an array")
    for index, entry in enumerate(modules["lists"]):
        location = f"moduleLists.lists[{index}]"
        # reverse is optional and defaults to false.
        if isinstance(entry, dict) and "reverse" not in entry:
            entry = {**entry, "reverse": False}
            modules["lists"][index] = entry
        rule = _object(entry, {"path", "patterns", "reverse"}, location)
        if not isinstance(rule["path"], str) or not rule["path"].strip():
            raise ValueError(f"config {location}.path: expected a nonempty string")
        _path(rule["path"], location + ".path")
        if type(rule["reverse"]) is not bool:
            raise ValueError(f"config {location}.reverse: expected a boolean")
        for pattern in _strings(rule["patterns"], location + ".patterns"):
            _path(pattern, location + ".patterns")
            if "{module}" not in pattern:
                raise ValueError(f"config {location}.patterns: missing {{module}}")
            if not _template(pattern, location + ".patterns") <= {"lib", "module"}:
                raise ValueError(f"config {location}.patterns: invalid template {pattern}")
    return config


def mapping_findings(changed: set[str], config: dict, body: str) -> tuple[list[Finding], str | None]:
    exemption = re.search(r"^[ \t]*Docs:[ \t]*none[ \t]*[—–-]([^\r\n]*)\r?$", body, re.IGNORECASE | re.MULTILINE)
    findings: list[Finding] = []
    if exemption:
        reason = exemption[1].strip()
        if reason:
            return [], reason
        findings.append(Finding("mapping", "Docs: none reason is missing"))
    # Each required document remembers the first changed source that required it.
    required: dict[tuple[str, str], str] = {}
    for rule in config["mappings"]:
        for pattern in rule["paths"]:
            matcher = path_pattern(pattern)
            for path in sorted(changed):
                match = matcher.fullmatch(path)
                if match:
                    for doc in rule["docs"]:
                        required.setdefault((rule["name"], doc.format(**match.groupdict())), path)
    for (name, document), source in sorted(required.items()):
        if document not in changed:
            findings.append(Finding("mapping", f"{name}: required document did not change: {document}", source))
    return findings, None


def bilingual_findings(changed: set[str], existing: set[str], excludes: list[str]) -> list[Finding]:
    patterns = [path_pattern(pattern) for pattern in excludes]
    findings = []
    for path in sorted(changed):
        if not path.endswith(".md"):
            continue
        partner = path[:-9] + ".md" if path.endswith(".zh-TW.md") else path[:-3] + ".zh-TW.md"
        if any(pattern.fullmatch(item) for pattern in patterns for item in (path, partner)):
            continue
        if partner in existing and partner not in changed:
            findings.append(Finding("bilingual", f"{path}: companion did not change: {partner}", path))
    return findings


def module_findings(git: Git, head: str, files: set[str], config: dict) -> list[Finding]:
    modules = set()
    for root in config["roots"]:
        for path in files:
            if path.startswith(root + "/"):
                parts = path[len(root) + 1:].split("/")
                if len(parts) > 1:
                    modules.add((root.rsplit("/", 1)[-1], parts[0]))
    findings = []
    for rule in config["lists"]:
        document = rule["path"]
        if document not in files:
            findings.append(Finding("module-list", f"list document does not exist at head: {document}", document))
            continue
        text = git.read(head, document)
        valid = {pattern.format(lib=lib, module=module)
                 for lib, module in modules for pattern in rule["patterns"]}
        for lib, module in sorted(modules):
            if not any(pattern.format(lib=lib, module=module) in text for pattern in rule["patterns"]):
                findings.append(Finding("module-list", f"{document}: missing module {lib}/{module}", document))
        if rule["reverse"]:
            stale: dict[str, int] = {}
            for root in config["roots"]:
                for pattern in rule["patterns"]:
                    template = pattern.format(lib=root.rsplit("/", 1)[-1], module="{module}")
                    # A module name may hold dots, but {module}.md must not read Name.zh-TW.md as module Name.zh-TW.
                    expression = re.escape(template).replace(re.escape("{module}"),
                                                             r"([^/\s`\[\]()<>]+)(?<!\.zh-TW)")
                    for match in re.finditer(expression, text):
                        if match[0] not in valid:
                            stale.setdefault(match[0], text.count("\n", 0, match.start()) + 1)
            findings.extend(Finding("module-list", f"{document}: referenced module folder does not exist: {target}",
                                    document, line) for target, line in sorted(stale.items()))
    return findings


def without_fences(text: str) -> str:
    """Mask fenced blocks while preserving line positions."""
    result = []
    fence = ""
    for line in text.splitlines(keepends=True):
        match = re.match(r" {0,3}(`{3,}|~{3,})(.*)", line)
        if fence:
            result.append("\n" if line.endswith("\n") else "")
            if match and match[1][0] == fence[0] and len(match[1]) >= len(fence) and not match[2].strip():
                fence = ""
        elif match and (match[1][0] != "`" or "`" not in match[2]):
            fence = match[1]
            result.append("\n" if line.endswith("\n") else "")
        else:
            result.append(line)
    return "".join(result)


def link_targets(text: str) -> list[tuple[str, int]]:
    """Read inline destinations and reference definitions outside code, with their line numbers."""
    text = without_fences(text)
    text = re.sub(r"(?<![\\`])(?P<ticks>`+)(?!`).*?(?<!`)(?P=ticks)(?!`)",
                  lambda match: re.sub(r"[^\n]", " ", match[0]), text, flags=re.DOTALL)
    starts = [(match.start(), match.end())
              for match in re.finditer(r"^ {0,3}\[[^\]\n]+\]:[ \t]*", text, re.MULTILINE)]
    starts.extend(_inline_link_starts(text))
    targets = []
    for begin, start in sorted(starts):
        index = start
        line = text.count("\n", 0, begin) + 1
        if text[start:start + 1] == "<":
            end = text.find(">", start + 1)
            if end != -1:
                targets.append((text[start + 1:end], line))
            continue
        depth = 0
        while index < len(text):
            char = text[index]
            if char == "\\" and index + 1 < len(text):
                index += 2
                continue
            if char.isspace() or (char == ")" and not depth):
                break
            depth += (char == "(") - (char == ")")
            index += 1
        if index > start:
            targets.append((re.sub(r"\\([\\() ])", r"\1", text[start:index]), line))
    return targets


def _escaped(text: str, index: int) -> bool:
    """Return whether an odd number of backslashes precedes the character."""
    count = 0
    while index - count > 0 and text[index - count - 1] == "\\":
        count += 1
    return count % 2 == 1


def _inline_link_starts(text: str) -> list[tuple[int, int]]:
    """Find `[label](` with balanced, unescaped brackets; return the label and destination offsets."""
    starts = []
    for opening in (index for index, char in enumerate(text) if char == "[" and not _escaped(text, index)):
        depth, cursor = 0, opening
        while cursor < len(text):
            if text[cursor] == "\\":
                cursor += 2
                continue
            depth += (text[cursor] == "[") - (text[cursor] == "]")
            if not depth:
                break
            cursor += 1
        if text[cursor + 1:cursor + 2] == "(":
            destination = cursor + 2
            while text[destination:destination + 1] in (" ", "\t", "\n"):
                destination += 1
            starts.append((opening, destination))
    return starts


def link_findings(git: Git, head: str, files: set[str], changed: set[str],
                  removed: set[str], all_links: bool) -> list[Finding]:
    existing = files | {parent for path in files for parent in _parents(path)}
    # A link to a folder breaks when its last file is removed, so removed folders count too.
    removed = removed | {parent for path in removed for parent in _parents(path) if parent not in existing}
    findings = []
    for path in sorted(files if all_links or removed else files & changed):
        if not path.lower().endswith((".md", ".markdown")):
            continue
        seen = set()
        for target, line in link_targets(git.read(head, path)):
            if target.startswith("#") or re.match(r"(?:https?|mailto):", target, re.IGNORECASE):
                continue
            target = unquote(re.split(r"[#?]", target, maxsplit=1)[0])
            if not target:
                continue
            resolved = posixpath.normpath(target.lstrip("/") if target.startswith("/")
                                         else posixpath.join(posixpath.dirname(path), target))
            if resolved in seen:
                continue
            seen.add(resolved)
            if resolved in removed:
                findings.append(Finding("links", f"{path}: links to deleted or renamed path: {resolved}", path, line))
            elif (all_links or path in changed) and resolved not in existing and resolved != ".":
                findings.append(Finding("links", f"{path}: target does not exist at head: {resolved}", path, line))
    return findings


def _parents(path: str) -> list[str]:
    parts = path.split("/")
    return ["/".join(parts[:index]) for index in range(1, len(parts))]


def check_repository(repo: Path, config: dict, base: str, head: str = "HEAD",
                     pr_body: str = "", all_links: bool = False) -> tuple[list[Finding], str | None]:
    """Return deterministic findings and the accepted mapping exemption reason."""
    git = Git(repo)
    base, head = git.commit(base), git.commit(head)
    changed, removed = changed_paths(git, base, head)
    files = git.files(head)
    findings, reason = mapping_findings(changed, config, pr_body)
    findings.extend(bilingual_findings(changed, files | git.files(base), config["bilingual"]["exclude"]))
    findings.extend(module_findings(git, head, files, config["moduleLists"]))
    findings.extend(link_findings(git, head, files, changed, removed, all_links))
    return findings, reason


def _escape(value: str, property_value: bool = False) -> str:
    """Escape GitHub Actions workflow command data."""
    value = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    return value.replace(":", "%3A").replace(",", "%2C") if property_value else value


def annotation(severity: str, finding: Finding) -> str:
    """Format one finding as a GitHub Actions annotation that also reads as plain text.

    A file-wide finding points at line 1. Only the missing exemption reason has no location,
    because the PR body is not a repository file.
    """
    message = _escape(f"doc-sync {finding.check}: {finding.message}")
    if finding.path is None:
        return f"::{severity}::{message}"
    return f"::{severity} file={_escape(finding.path, True)},line={finding.line or 1}::{message}"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--base", required=True)
    parser.add_argument("--head", default="HEAD")
    parser.add_argument("--pr-body-file", type=Path)
    parser.add_argument("--mode", choices=("warn", "enforce"), default="warn")
    parser.add_argument("--all-links", action="store_true")
    args = parser.parse_args(argv)
    try:
        config = load_config(args.config)
        body = args.pr_body_file.read_text(encoding="utf-8-sig") if args.pr_body_file else ""
        findings, reason = check_repository(args.repo, config, args.base, args.head, body, args.all_links)
    except (OSError, ValueError, UnicodeError, re.error) as error:
        print(f"doc-sync error: {error}", file=sys.stderr)
        return 2
    if reason:
        print(f"::notice::{_escape(f'doc-sync exemption: mapping: {reason}')}")
    severity = "warning" if args.mode == "warn" else "error"
    for finding in findings:
        print(annotation(severity, finding))
    counts = Counter(finding.check for finding in findings)
    print("doc-sync summary: " + ", ".join(f"{check}={counts[check]}" for check in CHECKS))
    return int(args.mode == "enforce" and bool(findings))


if __name__ == "__main__":
    sys.exit(main())
