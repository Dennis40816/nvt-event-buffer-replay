# Copyright (c) 2026 Dennis Liu. All rights reserved.
"""Report whether a current WIP section records branch and pull-request state."""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone, tzinfo
from pathlib import Path

from doc_sync import Git, without_fences


MARKER = "以本節為準"


def current_section(text: str) -> tuple[str, int]:
    """Select the first marked heading through the next peer or ancestor."""
    lines = text.splitlines(keepends=True)
    visible = without_fences(text).splitlines(keepends=True)
    headings: list[tuple[int, int, str]] = []
    atx = r" {0,3}(#{1,6})(?:[ \t]+(.*?)|[ \t]*)$"
    underline = r" {0,3}(?:=+|-+)[ \t]*"
    rule = r" {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*"
    item = r" {0,3}(?:[-*+]|\d{1,9}[.)])[ \t]"
    for index, line in enumerate(visible):
        match = re.match(atx, line.rstrip("\r\n"))
        if match:
            headings.append((index, len(match[1]), (match[2] or "").rstrip("# \t")))
            continue
        previous = visible[index - 1].rstrip("\r\n") if index else ""
        # A setext underline needs paragraph text above it. After a heading, a list item,
        # another underline or a thematic break, a line of dashes is a thematic break.
        if (re.fullmatch(underline, line.rstrip("\r\n")) and previous.strip()
                and not any(re.match(pattern, previous) for pattern in (atx, item))
                and not any(re.fullmatch(pattern, previous) for pattern in (underline, rule))):
            headings.append((index - 1, 1 if line.lstrip().startswith("=") else 2, previous.strip()))
    marked = [heading for heading in headings if MARKER in heading[2]]
    if not marked:
        return "", 0
    start, level, _ = marked[0]
    end = next((index for index, other_level, _ in headings if index > start and other_level <= level), len(lines))
    return "".join(lines[start:end]), len(marked)


def section_time(section: str, head_time: datetime, zone: tzinfo | None = None) -> datetime | None:
    """Interpret unzoned WIP timestamps in `zone`, or in the head commit's UTC offset without one."""
    zone = zone or head_time.tzinfo
    times = []
    for match in re.finditer(r"(?<![\d-])(?:\d{4}-)?\d{2}-\d{2} \d{2}:\d{2}(?![\d:xX])", section):
        value = match[0]
        if len(value) == 11:
            value = f"{head_time.year}-{value}"
        try:
            times.append(datetime.strptime(value, "%Y-%m-%d %H:%M").replace(tzinfo=zone))
        except ValueError:
            continue
    return max(times) if times else None


def load_open_prs(path: Path) -> list[dict]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, list):
        raise ValueError("open-prs: expected an array")
    for item in value:
        if (not isinstance(item, dict) or type(item.get("number")) is not int
                or not isinstance(item.get("url"), str) or not item["url"].strip()):
            raise ValueError("open-prs: each entry needs an integer number and a nonempty URL")
    return value


def check_handoff(wip: str, repo: Path, branch: str = "HEAD", open_prs: list[dict] | None = None,
                  now: datetime | None = None, zone: tzinfo | None = None) -> tuple[list[str], int]:
    git = Git(repo)
    sha = git.commit(branch)
    head_time = datetime.fromisoformat(git.run("show", "-s", "--format=%cI", sha).strip())
    now = now or datetime.now(timezone.utc)
    # WIP files record the writer's clock time, so read them in this computer's zone by default.
    zone = zone or datetime.now().astimezone().tzinfo
    section, count = current_section(wip)
    findings = []
    if not count:
        findings.append(f"no heading contains {MARKER}")
    timestamp = section_time(section, head_time, zone)
    if timestamp is None:
        findings.append("current section has no recognized timestamp")
    elif head_time > timestamp:
        findings.append(f"branch head time {head_time.isoformat()} is later than section time {timestamp.isoformat()}")
    # A copied clock that runs ahead makes a section look newer than it is.
    elif timestamp > now + timedelta(minutes=10):
        findings.append(f"section time {timestamp.isoformat()} is later than the current time {now.isoformat()}")
    prefixes = re.findall(r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{7,40}(?![0-9A-Fa-f])", section)
    if not any(sha.startswith(prefix.lower()) for prefix in prefixes):
        findings.append(f"current section does not mention head SHA {sha}")
    for pr in open_prs or []:
        # A URL prefix alone must not count as the full URL; markup or CJK punctuation may follow it.
        if not re.search(re.escape(pr["url"]) + r"(?![0-9A-Za-z_/-])", section):
            findings.append(f"open pull request #{pr['number']} is missing: {pr['url']}")
    return findings, count


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wip", required=True, type=Path)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--branch", default="HEAD")
    parser.add_argument("--open-prs", type=Path)
    parser.add_argument("--utc-offset", help="zone of WIP times, such as +08:00; defaults to this computer's zone")
    args = parser.parse_args(argv)
    try:
        zone = None
        if args.utc_offset:
            offset = re.fullmatch(r"([+-])(\d{2}):(\d{2})", args.utc_offset)
            if not offset:
                raise ValueError("--utc-offset: expected +HH:MM or -HH:MM")
            minutes = int(offset[2]) * 60 + int(offset[3])
            zone = timezone(timedelta(minutes=-minutes if offset[1] == "-" else minutes))
        wip = args.wip.read_text(encoding="utf-8-sig")
        prs = load_open_prs(args.open_prs) if args.open_prs else []
        findings, count = check_handoff(wip, args.repo, args.branch, prs, zone=zone)
    except (OSError, ValueError, UnicodeError) as error:
        print(f"handoff-check error: {error}", file=sys.stderr)
        return 2
    for finding in findings:
        print(f"handoff-check finding: {finding}")
    print(f"handoff-check summary: findings={len(findings)}, marked-headings={count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
