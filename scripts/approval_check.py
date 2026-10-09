"""Check the live pull request against the base branch's approval policy.

The workflow explicitly checks out the pull request's base branch, so this
script and .github/approval-policy.json come from the base, never the
PR head. It passes the checked-out SHA, which must equal the live branch tip.
"""

from __future__ import annotations

import argparse
import base64
import difflib
import fnmatch
import html
import http.client
import json
import os
from pathlib import Path
import re
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request


ROOT = Path(__file__).resolve().parent.parent
SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
RECORD = re.compile(r"Review record: ([0-9a-fA-F]{40}) (accept|reject)\Z")
# Fail closed: any review from an allowed identity that mentions a review
# record anywhere is a record. Unless its first non-empty line is exact, it is
# a malformed one, so a later decorated or reworded reject can never be skipped
# in favour of an older accept. The cost is that prose mentioning the phrase
# needs a fresh, well-formed record after it. Test the raw body as well as
# visible HTML text and its normalized form so normalization can only add
# attempts, never remove them.
ATTEMPT = re.compile(r"review[\W_]*record", re.IGNORECASE)
MARKDOWN_MARKERS = frozenset("*_~`")
PER_PAGE = 100
MAX_PAGES = 30


class InputError(Exception):
    """An unavailable or malformed input; approval fails closed."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise InputError(message)


def sha(value: object, label: str) -> str:
    require(isinstance(value, str) and SHA.fullmatch(value) is not None,
            f"{label} is not a full SHA")
    return value.lower()


def load_json(data: bytes, label: str) -> object:
    try:
        return json.loads(data)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise InputError(f"{label} is invalid JSON: {error}") from error


class Reader:
    """The same REST payloads, from GitHub or numbered fixture files."""

    def __init__(self, repository: str, number: int, fixture: Path | None):
        require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository)
                is not None, "invalid repository")
        require(number > 0, "pull request number must be positive")
        self.repository = repository
        self.number = number
        self.fixture = fixture
        self.api = os.environ.get("GITHUB_API_URL", "https://api.github.com").rstrip("/")

    def get(self, label: str, path: str, page: int | None = None) -> object:
        if self.fixture is not None:
            name = f"{label}-{page}.json" if page is not None else f"{label}.json"
            try:
                data = (self.fixture / name).read_bytes()
            except OSError as error:
                raise InputError(f"missing fixture {name}: {error}") from error
            return load_json(data, name)
        joiner = "&" if "?" in path else "?"
        suffix = f"{joiner}per_page={PER_PAGE}&page={page}" if page is not None else ""
        url = f"{self.api}/repos/{self.repository}/{path}{suffix}"
        headers = {"Accept": "application/vnd.github+json",
                   "X-GitHub-Api-Version": "2022-11-28",
                   "User-Agent": "nvt-approval-check"}
        token = os.environ.get("GITHUB_TOKEN")
        if token:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(url, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return load_json(response.read(), label)
        except (urllib.error.URLError, OSError, http.client.HTTPException) as error:
            raise InputError(f"GitHub API {label} failed: {error}") from error

    def pages(self, label: str, path: str) -> list[dict]:
        result: list[dict] = []
        for page in range(1, MAX_PAGES + 1):
            payload = self.get(label, path, page)
            require(isinstance(payload, list), f"{label} page {page} is malformed")
            items = payload
            require(all(isinstance(item, dict) for item in items),
                    f"{label} page {page} has malformed items")
            result.extend(items)
            if len(items) < PER_PAGE:
                return result
        raise InputError(f"{label} exceeds {MAX_PAGES} pages")

    def pull(self) -> dict:
        pull = self.get("pull", f"pulls/{self.number}")
        require(isinstance(pull, dict), "pull request is malformed")
        require(pull.get("number") == self.number, "wrong pull request number")
        return pull

    def compare(self, base: str, head: str, label: str = "compare") -> dict:
        value = self.get(label, f"compare/{base}...{head}")
        require(isinstance(value, dict) and type(value.get("behind_by")) is int,
                "compare response is malformed")
        return value

    def tree(self, commit: str) -> dict[str, dict]:
        value = self.get(f"commit-{commit}", f"git/commits/{commit}")
        require(isinstance(value, dict) and isinstance(value.get("tree"), dict),
                "carryover commit has no tree")
        tree_sha = sha(value["tree"].get("sha"), "carryover tree SHA")
        value = self.get(f"tree-{tree_sha}", f"git/trees/{tree_sha}?recursive=1")
        require(isinstance(value, dict) and value.get("truncated") is False
                and isinstance(value.get("tree"), list),
                "carryover tree is unavailable or truncated")
        result = {}
        for item in value["tree"]:
            require(isinstance(item, dict) and isinstance(item.get("path"), str)
                    and isinstance(item.get("mode"), str)
                    and isinstance(item.get("type"), str),
                    "carryover tree entry is malformed")
            require(item["path"] not in result, "duplicate carryover tree path")
            sha(item.get("sha"), "carryover tree entry SHA")
            result[item["path"]] = item
        return result

    def text(self, blob: str) -> str:
        value = self.get(f"blob-{blob}", f"git/blobs/{blob}")
        require(isinstance(value, dict) and value.get("encoding") == "base64"
                and isinstance(value.get("content"), str)
                and value.get("sha") == blob, "carryover blob is malformed")
        return base64.b64decode("".join(value["content"].split()),
                                validate=True).decode("utf-8")

    def branch_tip(self, branch: str) -> str:
        encoded = urllib.parse.quote(branch, safe="/")
        value = self.get("base-ref", f"git/ref/heads/{encoded}")
        require(isinstance(value, dict) and value.get("ref") == f"refs/heads/{branch}",
                "base branch ref is malformed")
        target = value.get("object")
        require(isinstance(target, dict) and target.get("type") == "commit",
                "base branch does not name a commit")
        return sha(target.get("sha"), "live base SHA")

    def files(self) -> list[dict]:
        return self.pages("files", f"pulls/{self.number}/files")

    def reviews(self) -> list[dict]:
        return self.pages("reviews", f"pulls/{self.number}/reviews")


def path_matches(path: str, pattern: str) -> bool:
    parts = path.replace("\\", "/").casefold().split("/")
    needle = pattern.casefold()
    return any(fnmatch.fnmatchcase("/".join(parts[index:]), needle)
               for index in range(len(parts)))


def identity(review: dict, principal: dict) -> bool:
    user = review.get("user")
    return (isinstance(user, dict) and user.get("id") == principal["id"]
            and isinstance(user.get("login"), str)
            and user["login"].casefold() == principal["login"].casefold())


def review_order(review: dict) -> tuple[str, int]:
    submitted = review.get("submitted_at")
    require(isinstance(submitted, str), "review has no submitted_at")
    review_id = review.get("id")
    require(type(review_id) is int, "review has no numeric id")
    return submitted, review_id


def review_comparison_text(body: str) -> str:
    """Strip complete HTML tags and comments in one pass, keeping references.

    An unfinished tag or comment remains text, so ambiguous markup cannot
    hide a record attempt. A '<' inside a tag ends that candidate tag.
    """
    parts: list[str] = []
    start = cursor = 0
    length = len(body)
    while cursor < length:
        if body[cursor] != "<":
            cursor += 1
            continue
        tag_start = cursor
        if body.startswith("<!--", cursor):
            end = body.find("-->", cursor + 4)
            if end < 0:
                break
            parts.append(body[start:tag_start])
            cursor = end + 3
            start = cursor
            continue
        cursor += 1
        if cursor < length and body[cursor] == "/":
            cursor += 1
        if cursor >= length or not body[cursor].isascii() or not body[cursor].isalpha():
            continue
        cursor += 1
        while cursor < length and body[cursor].isascii() and (
                body[cursor].isalnum() or body[cursor] in "-.:_"):
            cursor += 1
        if cursor >= length:
            break
        if not (body[cursor].isspace() or body[cursor] in "/>"):
            continue
        quote = None
        while cursor < length:
            char = body[cursor]
            if quote is not None:
                if char == quote:
                    quote = None
            elif char in "\"'":
                quote = char
            elif char == "<":
                break
            elif char == ">":
                parts.append(body[start:tag_start])
                cursor += 1
                start = cursor
                break
            cursor += 1
        else:
            break
    parts.append(body[start:])
    # Decode only after parsing: &lt;b&gt; is visible literal text, not a tag.
    return html.unescape("".join(parts))


def normalized_attempt_text(text: str) -> str:
    """Remove only invisible format characters and Markdown word markers."""
    return "".join(char for char in text if char not in MARKDOWN_MARKERS
                   and unicodedata.category(char) != "Cf")


def classify(policy: dict, base_ref: str, files: list[dict]) -> tuple[str, str]:
    owner_branch = policy.get("owner_gated_base_branch")
    if owner_branch is not None and base_ref.casefold() == owner_branch.casefold():
        return "owner-gated", f"base branch {base_ref}"
    tests_non_added = policy.get("tests_non_added")
    for item in files:
        name, status = item.get("filename"), item.get("status")
        require(isinstance(name, str) and isinstance(status, str),
                "changed file is malformed")
        paths = [name]
        if status == "renamed":
            previous = item.get("previous_filename")
            require(isinstance(previous, str), "renamed file has no previous_filename")
            paths.append(previous)
        for path in paths:
            if any(path_matches(path, pattern)
                   for pattern in policy["owner_gated_patterns"]):
                return "owner-gated", f"owner-gated path {path}"
            if (status != "added" and tests_non_added is not None
                    and path_matches(path, tests_non_added)):
                return "owner-gated", f"{status} file under tests: {path}"
    return "review-gated", "no owner-gated path or base branch"


def net_changes(comparison: dict) -> dict[str, tuple[str, str, str | None]]:
    files = comparison.get("files")
    # GitHub caps this list at 300 without reporting the omitted file count.
    require(isinstance(files, list) and len(files) < 300
            and not comparison.get("truncated"),
            "carryover comparison is unavailable or may be truncated")
    result = {}
    for item in files:
        require(isinstance(item, dict) and isinstance(item.get("filename"), str)
                and isinstance(item.get("status"), str),
                "carryover changed file is malformed")
        previous = item.get("previous_filename")
        require(previous is None or isinstance(previous, str),
                "carryover previous filename is malformed")
        name = item["filename"]
        require(name not in result, "duplicate carryover changed path")
        result[name] = (item["status"], sha(item.get("sha"), "carryover blob SHA"),
                        previous)
    return result


def carryover_lines(text: str) -> tuple[list[str], list[bool]]:
    """Mark unsafe lines using the entire file, not a possibly partial patch."""
    require(re.search(r"`{3,}|~{3,}", text) is None,
            "carryover file contains forbidden content: fence-like text")
    lines = text.splitlines(keepends=True)
    unsafe = []
    markup = None
    quote = None
    elements = []
    previous = ""
    for line in lines:
        html_line = bool(elements) or markup is not None or "<" in line or ">" in line
        cursor = 0
        while cursor < len(line):
            char = line[cursor]
            if markup == "comment":
                if line.startswith("-->", cursor):
                    markup = None
                    cursor += 3
                    continue
            elif markup == "tag":
                if quote is not None:
                    if char == quote:
                        quote = None
                elif char in "\"'":
                    quote = char
                elif char == ">":
                    markup = None
            elif line.startswith("<!--", cursor):
                markup = "comment"
                cursor += 4
                continue
            elif char == "<":
                tag = re.match(r"</?([A-Za-z][A-Za-z0-9:._-]*)(?=[\s/>]|$)", line[cursor:])
                if tag is not None and not tag.group(1).endswith(":"):
                    name = tag.group(1).casefold()
                    if tag.group().startswith("</"):
                        if elements and elements[-1] == name:
                            elements.pop()
                    elif name not in ("area", "base", "br", "col", "embed", "hr", "img",
                                      "input", "link", "meta", "param", "source", "track", "wbr"):
                        # Treat non-void self-closing tags conservatively as open.
                        elements.append(name)
                markup = "tag"
            cursor += 1
        visible = html.unescape(line)
        url = re.search(r"[a-z][a-z0-9+.-]*:(?=\S)|www\."
                        r"|\]\s*\(|^\s*\[[^\]]+\]:", visible, re.IGNORECASE)
        link = any(char in visible or char in previous for char in "[]<(")
        link = link or previous.rstrip().endswith(":")
        unsafe.append(html_line or url is not None
                      or link
                      or any(unicodedata.category(char) == "Cf" for char in visible))
        if visible.strip():
            previous = visible
    return lines, unsafe


def carryover_result(reader: Reader, policy: dict, accepted: str, head: str,
                     base: str, comparison: dict) -> tuple[bool, str]:
    allowlist = policy["review_carryover"].get("allowlist")
    require(isinstance(allowlist, list)
            and all(isinstance(pattern, str) and pattern for pattern in allowlist),
            "carryover allowlist is malformed")
    old = net_changes(reader.compare(base, accepted, "compare-accepted"))
    new = net_changes(comparison)
    old_tree, new_tree = reader.tree(accepted), reader.tree(head)
    paths = old.keys() | new.keys()
    changed = {path for path in paths if old.get(path) != new.get(path)}
    # Blob SHAs do not encode mode. A mode-only change must not bypass D.
    for path in paths:
        before, after = old_tree.get(path), new_tree.get(path)
        for item, entry in ((old.get(path), before), (new.get(path), after)):
            if item is not None:
                require((entry is None if item[0] == "removed" else
                         entry is not None and entry["sha"] == item[1]),
                        f"carryover comparison and tree disagree: {path}")
        if before and after and (before["mode"], before["type"]) != (
                after["mode"], after["type"]):
            changed.add(path)
    require(len(changed) <= 5, "carryover exceeds 5 files")
    total = 0
    for path in sorted(changed):
        require(any(path_matches(path, pattern) for pattern in allowlist)
                and not any(path_matches(path, pattern)
                            for pattern in policy["owner_gated_patterns"]),
                f"carryover path is not eligible: {path}")
        require(Path(path).suffix.casefold() in (".md", ".txt"),
                f"carryover path is not .md or .txt: {path}")
        require(all(item is None or item[0] in ("modified", "added")
                    for item in (old.get(path), new.get(path))),
                f"carryover status is not modified or added: {path}")
        before, after = old_tree.get(path), new_tree.get(path)
        require(after is not None and after["type"] == "blob"
                and after["mode"] == "100644"
                and (before is None or (before["type"] == "blob"
                                        and before["mode"] == after["mode"])),
                f"carryover file is deleted, special, or changes mode: {path}")
        require(before is None or not path_matches(path, policy["tests_non_added"]),
                f"carryover modifies an owner-gated test: {path}")
        old_lines, old_unsafe = carryover_lines(reader.text(before["sha"]) if before else "")
        new_lines, new_unsafe = carryover_lines(reader.text(after["sha"]))
        for tag, start, end, new_start, new_end in difflib.SequenceMatcher(
                None, old_lines, new_lines, autojunk=False).get_opcodes():
            if tag == "equal":
                continue
            total += end - start + new_end - new_start
            require(total <= 40, "carryover exceeds 40 added/deleted lines")
            require(not any(old_unsafe[start:end]) and not any(new_unsafe[new_start:new_end]),
                    f"carryover changes forbidden content: {path}")
    if changed:
        base_tree = reader.tree(base)
        for path in sorted(changed):
            entry = base_tree.get(path)
            if entry is not None:
                require(entry["type"] == "blob" and entry["mode"] == "100644",
                        f"carryover base file is special: {path}")
                carryover_lines(reader.text(entry["sha"]))
    return True, (f"accept carried over from {accepted}; {len(changed)} file(s), "
                  f"{total} added/deleted line(s); files: {json.dumps(sorted(changed))}")


def record_result(reviews: list[dict], policy: dict, head: str,
                  reader: Reader | None = None, base: str | None = None,
                  net_comparison: dict | None = None) -> tuple[bool, str]:
    records = []
    for review in reviews:
        if not any(identity(review, author)
                   for author in policy["review_record_authors"]):
            continue
        if review.get("state") == "PENDING":
            continue
        body = review.get("body") or ""
        require(isinstance(body, str), "review body is malformed")
        lines = body.splitlines()
        first = next((line.rstrip() for line in lines if line.strip()), "")
        comparison = review_comparison_text(body)
        if (ATTEMPT.search(body) or ATTEMPT.search(comparison)
                or ATTEMPT.search(normalized_attempt_text(comparison))):
            records.append((review_order(review), review,
                            RECORD.fullmatch(first)))
    if not records:
        return False, "no allowed pull request review has a review record"
    _, review, match = max(records, key=lambda entry: entry[0])
    if review.get("state") in ("DISMISSED", "CHANGES_REQUESTED"):
        return False, "latest review record was dismissed or requests changes"
    if match is None:
        return False, "latest review record line is malformed"
    if match.group(1).lower() != head:
        setting = policy.get("review_carryover")
        if (match.group(2) == "accept" and isinstance(setting, dict)
                and setting.get("enabled") is True and reader is not None
                and base is not None and net_comparison is not None):
            try:
                return carryover_result(reader, policy, match.group(1).lower(),
                                        head, base, net_comparison)
            except (InputError, KeyError, TypeError, ValueError) as error:
                return False, f"accept carryover unavailable: {error}"
        return False, "latest review record names an older or different head"
    if match.group(2) != "accept":
        return False, "latest review record says reject"
    return True, "latest allowed review record accepts the current head"


def owner_result(reviews: list[dict], owner: dict, head: str) -> tuple[bool, str]:
    decisions = [review for review in reviews if identity(review, owner)
                 and review.get("state") in
                 ("APPROVED", "CHANGES_REQUESTED", "DISMISSED")]
    if not decisions:
        return False, "owner has no approval or change request"
    latest = max(decisions, key=review_order)
    if latest["state"] != "APPROVED":
        return False, f"owner's latest decision is {latest['state'].lower()}"
    if not isinstance(latest.get("commit_id"), str) or latest["commit_id"].lower() != head:
        return False, "owner approval is for an older or different head"
    return True, "owner's latest decision approves the current head"


def evaluate(reader: Reader, policy: dict,
             checked_out_base: str | None,
             expected_head: str | None = None) -> list[tuple[str, bool, str]]:
    pull = reader.pull()
    require(pull.get("state") == "open", "pull request is not open")
    base, head = pull.get("base"), pull.get("head")
    require(isinstance(base, dict) and isinstance(head, dict),
            "pull request has no base or head")
    head_sha = sha(head.get("sha"), "head SHA")
    if expected_head is not None:
        require(head_sha == sha(expected_head, "expected head SHA"),
                "live head differs from expected head")
    base_ref = base.get("ref")
    require(isinstance(base_ref, str) and base_ref, "base ref is missing")
    base_sha = reader.branch_tip(base_ref)
    if checked_out_base is not None:
        require(base_sha == sha(checked_out_base, "checked-out base SHA"),
                "base moved since checkout; rerun with current base policy")
    comparison = reader.compare(base_sha, head_sha)
    behind = comparison["behind_by"]
    require(behind >= 0, "negative behind_by")
    gate, gate_reason = classify(policy, base_ref, reader.files())
    reviews = reader.reviews()
    record_ok, record_reason = record_result(reviews, policy, head_sha,
                                           reader, base_sha, comparison)
    if gate == "owner-gated":
        owner_ok, owner_reason = owner_result(reviews, policy["owner"], head_sha)
        if policy.get("owner_requires_review_record", True) is False:
            record_ok, record_reason = True, "not required for owner-gated pull request"
    else:
        owner_ok, owner_reason = True, "not required for review-gated pull request"
    if reader.fixture is None:
        live = reader.pull()
        live_base, live_head = live.get("base"), live.get("head")
        require(isinstance(live_base, dict) and isinstance(live_head, dict)
                and live.get("state") == "open"
                and live_base.get("ref") == base_ref
                and str(live_head.get("sha") or "").lower() == head_sha
                and reader.branch_tip(base_ref) == base_sha,
                "pull request moved during evaluation; rerun")
    return [
        ("up to date", behind == 0,
         "head contains current base" if behind == 0 else f"head is {behind} commit(s) behind base"),
        ("gate", True, f"{gate}: {gate_reason}"),
        ("review record", record_ok, record_reason),
        ("owner approval", owner_ok, owner_reason),
    ]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repository", required=True)
    parser.add_argument("--pull-request", required=True, type=int)
    parser.add_argument("--fixture", type=Path)
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--policy", type=Path)
    parser.add_argument("--checked-out-base")
    parser.add_argument("--expected-head",
                        default=os.environ.get("APPROVAL_EXPECTED_HEAD") or None)
    args = parser.parse_args(argv)
    try:
        require(args.fixture is not None or args.checked_out_base is not None,
                "--checked-out-base is required for live checks")
        policy = load_json((args.policy or ROOT / ".github/approval-policy.json").read_bytes(),
                           "approval policy")
        require(isinstance(policy, dict), "approval policy is malformed")
        results = evaluate(Reader(args.repository, args.pull_request, args.fixture),
                           policy, args.checked_out_base, args.expected_head)
    except (InputError, OSError, KeyError, TypeError, ValueError) as error:
        results = [(part, False, f"input unavailable: {error}") for part in
                   ("up to date", "gate", "review record", "owner approval")]
    lines = [f"- {'PASS' if ok else 'FAIL'} {part}: {reason}"
             for part, ok, reason in results]
    output = "\n".join(lines) + "\n"
    print(output, end="")
    if args.summary:
        try:
            with args.summary.open("a", encoding="utf-8") as stream:
                stream.write(output)
        except OSError as error:
            print(f"summary write failed: {error}", file=sys.stderr)
            return 1
    return 0 if all(ok for _, ok, _ in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
