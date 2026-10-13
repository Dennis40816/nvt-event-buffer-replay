"""Synthetic REST payloads for the approval rule; no GitHub access."""

from __future__ import annotations

import base64
import copy
from contextlib import redirect_stdout
import http.client
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
FIXTURE = Path(__file__).resolve().parent / "fixtures/approval"
spec = importlib.util.spec_from_file_location(
    "approval_check", ROOT / "scripts/approval_check.py")
approval = importlib.util.module_from_spec(spec)
spec.loader.exec_module(approval)
POLICY = json.loads((ROOT / ".github/approval-policy.json").read_text(
    encoding="utf-8"))
HEAD = "a" * 40
BASE = "b" * 40
OLD = "d" * 40
OWNER = {"login": "Dennis40816", "id": 146855708}
OTHER = {"login": "untrusted-reviewer", "id": 999}


class MemoryReader(approval.Reader):
    def __init__(self):
        super().__init__("Dennis40816/nvt-event-buffer-replay", 1, FIXTURE)
        self.paths = []
        self.payloads = {
            label: json.loads((FIXTURE / f"{label}.json").read_text(encoding="utf-8"))
            for label in ("pull", "base-ref", "compare", "files-1", "reviews-1")
        }

    def get(self, label, path, page=None):
        self.paths.append((label, path))
        name = f"{label}-{page}" if page is not None else label
        if name not in self.payloads:
            raise approval.InputError(f"missing fixture {name}")
        return copy.deepcopy(self.payloads[name])


def review(review_id, user=OWNER, state="APPROVED", commit=HEAD, body=""):
    return {
        "id": review_id,
        "user": user,
        "state": state,
        "submitted_at": f"2026-10-01T12:{review_id % 60:02d}:00Z",
        "commit_id": commit,
        "body": body,
    }


class ApprovalCheckTests(unittest.TestCase):
    def setUp(self):
        self.reader = MemoryReader()

    def result(self):
        return {name: (ok, reason) for name, ok, reason in
                approval.evaluate(self.reader, POLICY, None)}

    def owner_gate(self):
        self.reader.payloads["files-1"] = [
            {"filename": "src/View.cs", "status": "modified"}]

    def approve(self, commit=HEAD, review_id=101):
        self.reader.payloads["reviews-1"].append(
            review(review_id, commit=commit))

    def test_review_gated_pass(self):
        result = self.result()
        self.assertTrue(all(ok for ok, _ in result.values()))
        self.assertIn("review-gated", result["gate"][1])

    def test_owner_gated_pass(self):
        self.owner_gate()
        self.approve()
        self.assertTrue(all(ok for ok, _ in self.result().values()))

    def test_owner_gated_without_approval(self):
        self.owner_gate()
        self.assertFalse(self.result()["owner approval"][0])

    def test_approval_on_older_commit(self):
        self.owner_gate()
        self.approve(OLD)
        self.assertFalse(self.result()["owner approval"][0])

    def test_later_changes_requested(self):
        self.owner_gate()
        self.approve()
        self.reader.payloads["reviews-1"].append(
            review(102, state="CHANGES_REQUESTED"))
        self.assertFalse(self.result()["owner approval"][0])

    def test_later_comment_only_review_does_not_cancel_approval(self):
        self.owner_gate()
        self.approve()
        self.reader.payloads["reviews-1"].append(
            review(102, state="COMMENTED"))
        self.assertTrue(self.result()["owner approval"][0])

    def test_dismissed_owner_change_request_blocks_older_approval(self):
        self.owner_gate()
        self.approve()
        self.reader.payloads["reviews-1"].append(
            review(102, state="DISMISSED"))
        self.assertFalse(self.result()["owner approval"][0])

    def test_pending_owner_review_is_ignored(self):
        self.owner_gate()
        self.approve()
        pending = review(103, state="PENDING")
        del pending["submitted_at"]
        self.reader.payloads["reviews-1"].append(pending)
        self.assertTrue(self.result()["owner approval"][0])

    def test_review_record_for_older_head(self):
        self.reader.payloads["reviews-1"][0]["body"] = (
            f"Review record: {OLD} accept\nLimits: synthetic")
        self.assertFalse(self.result()["review record"][0])

    def test_review_record_rejects(self):
        self.reader.payloads["reviews-1"][0]["body"] = (
            f"Review record: {HEAD} reject\nFindings: P1")
        self.assertFalse(self.result()["review record"][0])

    def test_latest_allowed_record_rejects(self):
        self.reader.payloads["reviews-1"].append(
            review(101, state="COMMENTED",
                   body=f"Review record: {HEAD} reject"))
        self.assertFalse(self.result()["review record"][0])

    def test_near_miss_newer_record_blocks_older_accept(self):
        cases = (
            (f"Review record: {HEAD} reject   ", "reject"),
            (f"Review record: `{HEAD}` reject", "malformed"),
            (f"# Review\nReview record: {HEAD} reject", "malformed"),
        )
        for body, reason in cases:
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="COMMENTED", body=body))
                result = {name: (ok, detail) for name, ok, detail in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"][0])
                self.assertIn(reason, result["review record"][1])

    def test_pending_record_is_ignored_before_ordering(self):
        pending = review(101, state="PENDING",
                         body=f"Review record: {HEAD} reject")
        del pending["submitted_at"]
        self.reader.payloads["reviews-1"].append(pending)
        self.assertTrue(self.result()["review record"][0])

    def test_dismissed_or_change_request_record_cannot_accept(self):
        for state in ("DISMISSED", "CHANGES_REQUESTED"):
            with self.subTest(state=state):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state=state,
                           body=f"Review record: {HEAD} accept"))
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"])

    def test_record_order_uses_submission_time_then_id(self):
        for same_time in (False, True):
            with self.subTest(same_time=same_time):
                reader = MemoryReader()
                later = review(101, state="COMMENTED",
                               body=f"Review record: {HEAD} reject")
                if same_time:
                    later["submitted_at"] = reader.payloads["reviews-1"][0][
                        "submitted_at"]
                reader.payloads["reviews-1"].insert(0, later)
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"])

    def test_equal_time_records_order_by_id_in_either_list_order(self):
        for accept_id, reject_id, passes in ((9, 5, True), (5, 9, False)):
            for reverse in (False, True):
                with self.subTest(accept_id=accept_id, reject_id=reject_id,
                                  reverse=reverse):
                    reader = MemoryReader()
                    accept = review(accept_id, user=OWNER, state="COMMENTED",
                                    body=f"Review record: {HEAD} accept")
                    reject = review(reject_id, user=OWNER, state="COMMENTED",
                                    body=f"Review record: {HEAD} reject")
                    reject["submitted_at"] = accept["submitted_at"]
                    ordered = [accept, reject]
                    if reverse:
                        ordered.reverse()
                    reader.payloads["reviews-1"] = ordered
                    result = {name: ok for name, ok, _ in
                              approval.evaluate(reader, POLICY, None)}
                    self.assertEqual(result["review record"], passes)

    def test_any_later_mention_that_is_not_exact_blocks_older_accept(self):
        nbsp, zero_width = chr(0xA0), chr(0x200B)
        for body in (f"**Review record: {HEAD} reject**",
                     f"- Review record: {HEAD} reject",
                     f"## Review record: {HEAD} reject",
                     f"> Review record: {HEAD} reject",
                     f"1. Review record: {HEAD} reject",
                     f"(Review record: {HEAD} reject)",
                     f"| Review record: {HEAD} reject |",
                     f"<b>Review record: {HEAD} reject</b>",
                     f"review record: {HEAD} reject",
                     f"Review Record: {HEAD} reject",
                     f"Updated review record: {HEAD} reject",
                     f"Review  record: {HEAD} reject",
                     f"Review-record: {HEAD} reject",
                     f"Review{nbsp}record: {HEAD} reject",
                     f"{zero_width}Review record: {HEAD} reject",
                     f"Review record:\n{HEAD} reject",
                     "Review record: rejected",
                     "Review record: not accepted",
                     "Review record: declined, see findings",
                     f"Findings first.\nReview record: {HEAD} reject",
                     "Review record read, approving."):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="COMMENTED", body=body))
                result = {name: (ok, detail) for name, ok, detail in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"][0])
                self.assertIn("malformed", result["review record"][1])

    def test_html_decorated_later_record_blocks_older_accept(self):
        for body in (f"Review <b>record</b>: {HEAD} reject",
                     f"Review&nbsp;record: {HEAD} reject",
                     f"Review&#32;record: {HEAD} reject",
                     f"Review&#x20;record: {HEAD} reject",
                     f"Review<!-- x -->record: {HEAD} reject"):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="COMMENTED", body=body))
                result = {name: (ok, detail) for name, ok, detail in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"][0])
                self.assertIn("malformed", result["review record"][1])

    def test_invisible_and_markdown_word_splits_block_older_accept(self):
        splits = (
            "Re&shy;view record", "Re&#8203;view record",
            "Re\u00adview record", "Re\u200bview record",
            "Re\u200cview record", "Re\u200dview record",
            "Re\u2060view record", "Re\ufeffview record",
            "Re\u200eview record", "Re\u202eview record",
            "Review re**cord**", "Re_view_ record",
            "Re`view` record", "Re~view~ record",
            "Re<b>view</b> record", "Re<!-- x -->view record",
        )
        for prefix in splits:
            with self.subTest(prefix=prefix):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(review(
                    101, state="COMMENTED",
                    body=f"{prefix}: {HEAD} reject"))
                result = {name: (ok, detail) for name, ok, detail in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"][0])
                self.assertIn("malformed", result["review record"][1])

    def test_normalization_removes_every_format_character(self):
        import unicodedata
        format_chars = "".join(chr(codepoint) for codepoint in
                               range(0x110000)
                               if unicodedata.category(chr(codepoint)) == "Cf")
        self.assertEqual(
            approval.normalized_attempt_text(f"Re{format_chars}view record"),
            "Review record")

    def test_ordinary_prose_keeps_existing_attempt_result(self):
        for body, expected in (("preview recording", False),
                               ("reviewer recorded", True)):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(review(
                    101, state="COMMENTED", body=body))
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertEqual(result["review record"], expected)

    def test_comparison_text_is_fast_on_adversarial_bodies(self):
        for pattern in ("for(i<n; i++) ", "<a ", "<!--", "&"):
            with self.subTest(pattern=pattern):
                body = (pattern * (65536 // len(pattern) + 1))[:65536]
                started = time.perf_counter()
                comparison = approval.review_comparison_text(body)
                elapsed = time.perf_counter() - started
                self.assertEqual(comparison, body)
                self.assertLess(elapsed, 2.0, f"{pattern!r}: {elapsed:.3f}s")

    def test_html_parser_cannot_hide_raw_later_record(self):
        for body in (f"<!--\nReview record: {HEAD} reject\n-->",
                     '<a title="review record">x</a>',
                     f"<!--\nReview record: {HEAD} reject",
                     f'<a title="review record: {HEAD} reject'):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="COMMENTED", body=body))
                result = {name: (ok, detail) for name, ok, detail in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"][0])
                self.assertIn("malformed", result["review record"][1])

    def test_html_without_record_phrase_leaves_older_accept(self):
        for body in ("<b>Looks good</b>",
                     "Review &lt;b&gt;record&lt;/b&gt; was not a record"):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="COMMENTED", body=body))
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertTrue(result["review record"])

    def test_later_review_without_the_phrase_leaves_the_record(self):
        for body in ("", "Looks good, approving.", "Reviewed the findings."):
            with self.subTest(body=body):
                reader = MemoryReader()
                reader.payloads["reviews-1"].append(
                    review(101, state="APPROVED", body=body))
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertTrue(result["review record"])

    def test_exact_first_line_wins_over_later_lines_in_the_same_body(self):
        reader = MemoryReader()
        reader.payloads["reviews-1"].append(review(
            101, state="COMMENTED",
            body=f"\n  \nReview record: {HEAD} accept  \r\n"
                 f"Earlier round: **Review record: {OLD} reject**"))
        result = {name: ok for name, ok, _ in
                  approval.evaluate(reader, POLICY, None)}
        self.assertTrue(result["review record"])

    def test_summary_file_receives_the_result_lines(self):
        import tempfile
        with tempfile.TemporaryDirectory() as directory:
            summary = Path(directory) / "summary.md"
            with redirect_stdout(io.StringIO()) as output:
                code = approval.main([
                    "--repository", "Dennis40816/nvt-event-buffer-replay",
                    "--pull-request", "1", "--fixture", str(FIXTURE),
                    "--summary", str(summary)])
            self.assertEqual(code, 0)
            self.assertEqual(summary.read_text(encoding="utf-8"),
                             output.getvalue())

    def test_record_from_unallowed_identity(self):
        self.reader.payloads["reviews-1"][0]["user"] = OTHER
        self.assertFalse(self.result()["review record"][0])

    def test_identity_requires_matching_login_and_id(self):
        for user in ({"login": "not-the-bot", "id": 334370883},
                     {"login": "nfc-agent-dennis40816[bot]", "id": 999}):
            with self.subTest(user=user):
                reader = MemoryReader()
                reader.payloads["reviews-1"][0]["user"] = user
                result = {name: ok for name, ok, _ in
                          approval.evaluate(reader, POLICY, None)}
                self.assertFalse(result["review record"])

    def test_head_behind_base(self):
        self.reader.payloads["compare"]["behind_by"] = 1
        self.assertFalse(self.result()["up to date"][0])

    def test_modified_test_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "tests/new_case.cs", "status": "modified"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_added_test_is_review_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "tests/new_case.cs", "status": "added"}]
        self.assertIn("review-gated", self.result()["gate"][1])

    def test_renamed_test_file_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "docs/moved.txt", "previous_filename": "tests/a.txt",
             "status": "renamed"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_non_added_test_statuses_are_owner_gated(self):
        for status in ("removed", "copied", "changed"):
            with self.subTest(status=status):
                self.reader.payloads["files-1"] = [
                    {"filename": "tests/sample.json", "status": status}]
                self.assertIn("owner-gated", self.result()["gate"][1])

    def test_rename_into_tests_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "tests/new_name.txt", "previous_filename": "docs/old.txt",
             "status": "renamed"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_policy_checker_and_workflow_are_owner_gated(self):
        for path in (".github/approval-policy.json", "scripts/approval_check.py",
                     ".github/workflows/approval.yml"):
            with self.subTest(path=path):
                self.reader.payloads["files-1"] = [
                    {"filename": path, "status": "modified"}]
                self.assertIn("owner-gated", self.result()["gate"][1])

    def test_base_branch_main_is_owner_gated(self):
        self.reader.payloads["pull"]["base"]["ref"] = "main"
        self.reader.payloads["base-ref"]["ref"] = "refs/heads/main"
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_nested_tests_directory_build_props_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "a/b/tests/Directory.Build.props", "status": "added"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_differently_cased_nuget_config_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            {"filename": "build/NuGet.Config", "status": "added"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_more_than_one_page_of_changed_files(self):
        self.reader.payloads["files-1"] = [
            {"filename": f"docs/item-{index}.md", "status": "added"}
            for index in range(100)]
        self.reader.payloads["files-2"] = [
            {"filename": "nested/scripts/check.py", "status": "added"}]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_more_than_thirty_pages_fails_closed(self):
        for page in range(1, 31):
            self.reader.payloads[f"files-{page}"] = [
                {"filename": f"docs/{page}-{index}.md", "status": "added"}
                for index in range(100)]
        with self.assertRaisesRegex(approval.InputError, "exceeds 30 pages"):
            self.reader.files()

    def test_pull_base_sha_old_but_live_tip_is_behind(self):
        self.reader.payloads["pull"]["base"]["sha"] = OLD
        self.reader.payloads["compare"]["behind_by"] = 1
        self.assertFalse(self.result()["up to date"][0])
        self.assertIn(("compare", f"compare/{'b' * 40}...{HEAD}"),
                      self.reader.paths)

    def test_checked_out_base_differs_from_live_tip(self):
        self.reader.payloads["pull"]["base"]["sha"] = OLD
        with self.assertRaises(approval.InputError):
            approval.evaluate(self.reader, POLICY, OLD)

    def test_expected_head_mismatch_fails_all_parts(self):
        output = io.StringIO()
        with redirect_stdout(output):
            code = approval.main([
                "--repository", "Dennis40816/nvt-event-buffer-replay",
                "--pull-request", "1", "--fixture", str(FIXTURE),
                "--expected-head", OLD])
        self.assertEqual(code, 1)
        lines = output.getvalue().splitlines()
        self.assertEqual(len(lines), 4)
        self.assertTrue(all(line.startswith("- FAIL ") and
                            "live head differs from expected head" in line
                            for line in lines))

    def test_expected_head_environment(self):
        args = ["--repository", "Dennis40816/nvt-event-buffer-replay",
                "--pull-request", "1", "--fixture", str(FIXTURE)]
        for value, expected_code in ((OLD, 1), (HEAD, 0), ("", 0)):
            with self.subTest(value=value):
                output = io.StringIO()
                with mock.patch.dict(os.environ,
                                     {"APPROVAL_EXPECTED_HEAD": value}):
                    with redirect_stdout(output):
                        code = approval.main(args)
                self.assertEqual(code, expected_code)
                if value == OLD:
                    self.assertEqual(output.getvalue().count("- FAIL "), 4)
                    self.assertIn("live head differs from expected head",
                                  output.getvalue())
        with mock.patch.dict(os.environ, {}, clear=True):
            with redirect_stdout(io.StringIO()):
                self.assertEqual(approval.main(args), 0)

    def test_invalid_expected_head_environment_fails_closed(self):
        args = ["--repository", "Dennis40816/nvt-event-buffer-replay",
                "--pull-request", "1", "--fixture", str(FIXTURE)]
        for value in ("not-a-sha", "g" * 40):
            with self.subTest(value=value):
                output = io.StringIO()
                with mock.patch.dict(os.environ,
                                     {"APPROVAL_EXPECTED_HEAD": value}):
                    with redirect_stdout(output):
                        code = approval.main(args)
                self.assertEqual(code, 1)
                lines = output.getvalue().splitlines()
                self.assertEqual(len(lines), 4)
                self.assertTrue(all(line.startswith("- FAIL ") and
                                    "expected head SHA is not a full SHA" in line
                                    for line in lines))

    def test_explicit_expected_head_overrides_environment(self):
        args = ["--repository", "Dennis40816/nvt-event-buffer-replay",
                "--pull-request", "1", "--fixture", str(FIXTURE)]
        for environment, explicit, expected_code in (
                (OLD, HEAD, 0), (HEAD, OLD, 1)):
            with self.subTest(environment=environment, explicit=explicit):
                with mock.patch.dict(os.environ,
                                     {"APPROVAL_EXPECTED_HEAD": environment}):
                    with redirect_stdout(io.StringIO()):
                        code = approval.main(args + ["--expected-head", explicit])
                self.assertEqual(code, expected_code)

    def test_closed_or_merged_pull_fails_all_parts(self):
        for merged in (False, True):
            with self.subTest(merged=merged):
                self.reader.payloads["pull"]["state"] = "closed"
                self.reader.payloads["pull"]["merged"] = merged
                output = io.StringIO()
                with mock.patch.object(approval, "Reader", return_value=self.reader):
                    with redirect_stdout(output):
                        code = approval.main([
                            "--repository", "Dennis40816/nvt-event-buffer-replay",
                            "--pull-request", "1", "--fixture", str(FIXTURE)])
                self.assertEqual(code, 1)
                lines = output.getvalue().splitlines()
                self.assertEqual(len(lines), 4)
                self.assertTrue(all(line.startswith("- FAIL ") and
                                    "pull request is not open" in line
                                    for line in lines))

    def test_empty_checked_out_base_fails_closed(self):
        with self.assertRaisesRegex(approval.InputError, "full SHA"):
            approval.evaluate(self.reader, POLICY, "")

    def test_unavailable_live_base_tip_fails_closed(self):
        del self.reader.payloads["base-ref"]
        with self.assertRaises(approval.InputError):
            self.result()

    def test_main_exit_codes_and_exception_mapping(self):
        args = ["--repository", "Dennis40816/nvt-event-buffer-replay",
                "--pull-request", "1", "--fixture", str(FIXTURE)]
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(approval.main(args), 0)
        self.assertEqual(output.getvalue().count("- PASS "), 4)
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(approval.main(args + ["--checked-out-base", ""]), 1)
        self.assertEqual(output.getvalue().count("- FAIL "), 4)
        self.assertIn("input unavailable", output.getvalue())
        output = io.StringIO()
        with redirect_stdout(output):
            self.assertEqual(approval.main(args[:4]), 1)
        self.assertIn("--checked-out-base is required", output.getvalue())

    def test_http_protocol_error_becomes_input_error(self):
        reader = approval.Reader("Dennis40816/nvt-event-buffer-replay", 1, None)
        with mock.patch.object(approval.urllib.request, "urlopen",
                               side_effect=http.client.HTTPException("broken")):
            with self.assertRaisesRegex(approval.InputError, "GitHub API"):
                reader.pull()

    def test_contributing_patterns_equal_policy(self):
        text = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")
        section = text.split("## Who approves\n", 1)[1].split(
            "Everything else is review-gated", 1)[0]
        # Continuation lines are included in each bullet's full source slice.
        blocks = re.split(r"(?m)^- ", section)[1:]
        patterns = []
        for block in blocks:
            title = block.split(":", 1)[0]
            if title == "Existing tests" or block.startswith("Any merge into"):
                continue
            patterns.extend(re.findall(r"`([^`]+)`", block))
        self.assertEqual(patterns, POLICY["owner_gated_patterns"])
        self.assertIn("any status other than `A`", section)

    def test_codeowners_patterns_equal_policy(self):
        lines = (ROOT / ".github/CODEOWNERS").read_text(
            encoding="utf-8").splitlines()
        actual = [line.split() for line in lines if line and not line.startswith("#")]
        dropped = POLICY["agent_gated"]["released_patterns"]
        expected = [[pattern if pattern.startswith("*.") else f"**/{pattern}",
                     "@Dennis40816"]
                    for pattern in POLICY["owner_gated_patterns"]
                    if pattern not in dropped]
        # A pattern with no owner removes the code owner for the tier B ledgers.
        expected += [[f"**/{path}"] for path in POLICY["agent_gated"]["ledger_paths"]]
        self.assertEqual(actual, expected)
        self.assertIn("modified-versus-added", "\n".join(lines))

    def test_policy_metadata_matches_contract(self):
        self.assertEqual(POLICY["owner"],
                         {"login": "Dennis40816", "id": 146855708})
        self.assertEqual(POLICY["review_record_authors"], [
            {"login": "Dennis40816", "id": 146855708},
            {"login": "nfc-agent-dennis40816[bot]", "id": 334370883},
        ])
        for principal in [POLICY["owner"], *POLICY["review_record_authors"]]:
            self.assertIsInstance(principal["login"], str)
            self.assertIs(type(principal["id"]), int)
        self.assertIsInstance(POLICY["owner_gated_patterns"], list)
        self.assertEqual(POLICY["tests_non_added"], "tests/**")
        agent = POLICY["agent_gated"]
        self.assertIs(agent["enabled"], True)
        self.assertEqual(agent["declaration"], "Approval tier: B")
        self.assertEqual(agent["veto_label"], "needs-owner")
        self.assertEqual(agent["max_changed_lines"], 600)
        self.assertEqual(agent["allowed_paths"], ["src/**", "tests/**"])
        self.assertEqual(agent["ledger_paths"], [
            "eng/code-health/baseline.json",
            "eng/code-health/test-debt.json",
            "eng/file-size-baseline.json",
            "eng/architecture/command-entries.json",
        ])
        self.assertEqual(agent["released_patterns"], ["src/**"])
        self.assertIn("src/**", POLICY["owner_gated_patterns"])
        self.assertIn("tools/repo-checks/**", POLICY["owner_gated_patterns"])
        self.assertEqual(agent["extra_denied_paths"], [
            "*.user", "*.proj", "*.projitems", "*.fsproj", "*.vbproj",
            "*.shproj", "xunit.runner.json", "testconfig.json",
            "*.runsettings", "*.testsettings"])
        self.assertEqual(POLICY["owner_gated_base_branch"], "main")
        self.assertEqual(POLICY["required_check"], "build-and-test")
        self.assertEqual(POLICY["approval_status_context"],
                         "governance/approval-rule")

    def test_workflow_reports_approval_commit_status(self):
        workflow = (ROOT / ".github/workflows/approval.yml").read_text(
            encoding="utf-8")
        self.assertIn("# GitHub reads this workflow from the pull request's "
                      "merge ref.", workflow)
        self.assertRegex(workflow, r"(?m)^  statuses: write$")
        self.assertRegex(workflow, r"(?m)^  pull_request_review:$")
        self.assertRegex(workflow, r"(?m)^  cancel-in-progress: true$")
        for event, required in (
                ("pull_request_review", {"submitted", "edited", "dismissed"}),
                ("pull_request", {"opened", "synchronize", "reopened",
                                  "ready_for_review", "edited",
                                  "converted_to_draft"})):
            match = re.search(
                rf"(?m)^  {event}:\n    types: \[([^\]]+)\]", workflow)
            self.assertIsNotNone(match, event)
            actual = {item.strip() for item in match.group(1).split(",")}
            self.assertTrue(required <= actual, f"{event}: {required - actual}")
        self.assertIn(
            "  group: approval-${{ github.event.pull_request.number }}-"
            "${{ github.event.pull_request.head.sha }}", workflow)
        job_header = workflow.split("  approval:\n", 1)[1].split(
            "    steps:\n", 1)[0]
        self.assertNotRegex(job_header, r"(?m)^    if:")
        self.assertNotIn("|| true", workflow)
        self.assertNotIn("continue-on-error", workflow)
        self.assertNotIn("--expected-head", workflow)
        steps = re.findall(
            r"(?ms)^      - name: [^\n]+\n.*?(?=^      - name: |\Z)",
            workflow)

        def named(name):
            found = [step for step in steps
                     if step.startswith(f"      - name: {name}\n")]
            self.assertEqual(len(found), 1)
            return found[0]

        pending = named("Mark approval pending")
        checkout = named("Check out the base branch")
        check = named("Check approval")
        status = named("Report approval status")
        self.assertLess(workflow.index(pending), workflow.index(checkout))
        checkouts = [step for step in steps
                     if re.search(r"(?m)^        uses: actions/checkout@", step)]
        self.assertEqual(checkouts, [checkout])
        self.assertEqual(re.findall(r"(?m)^          ref: (.+)$", checkout),
                         ["${{ github.event.pull_request.base.ref }}"])
        self.assertRegex(check, r"(?m)^        id: approval_check$")
        self.assertRegex(check, r"(?m)^          APPROVAL_EXPECTED_HEAD: "
                         r"\$\{\{ github.event.pull_request.head.sha \}\}$")
        self.assertRegex(status,
                         r"(?m)^        if: \$\{\{ !cancelled\(\) \}\}$")
        self.assertRegex(status, r"(?m)^          APPROVAL_OUTCOME: "
                         r"\$\{\{ steps.approval_check.outcome \}\}$")
        for step in (pending, status):
            self.assertRegex(step, r"(?m)^          APPROVAL_HEAD_SHA: "
                             r"\$\{\{ github.event.pull_request.head.sha \}\}$")
            context = re.search(
                r"(?m)^          APPROVAL_STATUS_CONTEXT: (.+)$", step)
            self.assertIsNotNone(context)
            self.assertEqual(context.group(1),
                             POLICY["approval_status_context"])
            body = step.split("        run: |\n", 1)[1]
            self.assertNotIn("${{", body)
            self.assertIn(
                'gh api -X POST "repos/$GH_REPO/statuses/$APPROVAL_HEAD_SHA"',
                body)
            self.assertIn('-f "context=$APPROVAL_STATUS_CONTEXT"', body)
            self.assertIn('-f "target_url=$APPROVAL_RUN_URL"', body)
        self.assertRegex(pending,
                         r"(?m)^          APPROVAL_STATUS_STATE: pending$")
        self.assertIn('-f "state=$APPROVAL_STATUS_STATE"', pending)
        body = status.split("        run: |\n", 1)[1]
        self.assertRegex(body, r'if \[ "\$APPROVAL_OUTCOME" = success \]; then\n'
                         r'            state=success\n'
                         r'            description="\$APPROVAL_SUCCESS_DESCRIPTION"\n'
                         r'          else\n'
                         r'            state=failure\n'
                         r'            description="\$APPROVAL_FAILURE_DESCRIPTION"')
        self.assertEqual(re.findall(r"(?m)^            state=(\w+)$", body),
                         ["success", "failure"])
        self.assertIn("for attempt in 1 2 3; do", body)
        self.assertIn('-f "state=$state"', body)

    # Tier B (agent-gated).
    def tier_b(self, files, body="Approval tier: B\n\nIntent: refactor.",
               labels=None, record_tier=True):
        if record_tier:
            self.reader.payloads["reviews-1"][0]["body"] += "\nApproval tier: B"
        self.reader.payloads["files-1"] = files
        self.reader.payloads["pull"]["body"] = body
        self.reader.payloads["pull"]["labels"] = [
            {"name": name} for name in (labels or [])]

    @staticmethod
    def changed(path, status="modified", additions=5, deletions=2, **extra):
        return {"filename": path, "status": status, "additions": additions,
                "deletions": deletions, **extra}

    def assert_owner_gated(self, expect_reason=None):
        result = self.result()
        self.assertIn("owner-gated", result["gate"][1])
        self.assertFalse(result["owner approval"][0])
        if expect_reason is not None:
            self.assertIn(expect_reason, result["gate"][1])

    def test_tier_b_source_change_is_agent_gated(self):
        self.tier_b([self.changed("src/Nvt.Replay.Avalonia/View.cs")])
        result = self.result()
        self.assertTrue(all(ok for ok, _ in result.values()))
        self.assertIn("agent-gated", result["gate"][1])
        self.assertIn("not required for agent-gated", result["owner approval"][1])

    def test_tier_b_still_needs_a_review_record(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["reviews-1"] = []
        result = self.result()
        self.assertIn("owner-gated", result["gate"][1])
        self.assertFalse(result["review record"][0])
        self.assertFalse(result["owner approval"][0])

    def test_tier_b_without_declaration_is_owner_gated(self):
        self.tier_b([self.changed("src/View.cs")], body="Intent: refactor.")
        self.assert_owner_gated()
        self.assertNotIn("tier B not granted", self.result()["gate"][1])

    def test_tier_b_declaration_must_be_the_first_line(self):
        self.tier_b([self.changed("src/View.cs")],
                    body="Intent\n\nApproval tier: B")
        self.assert_owner_gated()

    def test_tier_b_declaration_must_be_exact(self):
        for body in ("Approval tier: B2", "approval tier: b",
                     "Approval tier: A", "Approval tier: B (proposed)"):
            with self.subTest(body=body):
                self.tier_b([self.changed("src/View.cs")], body=body)
                self.assert_owner_gated()

    def test_tier_b_leading_blank_lines_are_ignored(self):
        self.tier_b([self.changed("src/View.cs")],
                    body="\n  \nApproval tier: B  \nrest")
        self.assertIn("agent-gated", self.result()["gate"][1])

    def test_tier_b_denies_build_graph_files_even_under_src(self):
        for path in ("src/App/App.csproj", "src/Directory.Build.props",
                     "src/x/packages.lock.json", "src/nuget.config",
                     "Nvt.EventBufferReplay.sln", "src/.editorconfig",
                     "src/App/App.csproj.user", "src/App/Tweaks.proj",
                     "src/App/Shared.projitems", "src/App/App.fsproj",
                     "src/Directory.Build.targets", "src/Dir.rsp"):
            with self.subTest(path=path):
                self.tier_b([self.changed(path)])
                self.assert_owner_gated("denied path")

    def test_tier_b_denies_nested_governance_and_agent_files(self):
        for path in ("src/AGENTS.md", "src/CLAUDE.md", "src/App/CLAUDE.md",
                     "src/.claude/settings.json", "src/.codex/config.toml",
                     "src/.mcp.json", "src/.gitattributes", "src/.gitignore",
                     "src/App/.github/workflows/x.yml", "src/App/scripts/x.ps1",
                     "src/tools/repo-checks/repo-health.ps1",
                     "src/eng/x.json", "src/VERSION", "src/docs/adr/0001.md"):
            with self.subTest(path=path):
                self.tier_b([self.changed(path)])
                self.assert_owner_gated("denied path")

    def test_tools_repo_checks_is_owner_gated(self):
        self.reader.payloads["files-1"] = [
            self.changed("tools/repo-checks/repo-health.ps1")]
        self.assertIn("owner-gated", self.result()["gate"][1])

    def test_tier_b_ledger_names_match_exactly(self):
        for path in ("ENG/code-health/baseline.json",
                     "eng/Code-Health/baseline.json",
                     "eng/code-health/baseline.json.bak"):
            with self.subTest(path=path):
                self.tier_b([self.changed(path)])
                self.assert_owner_gated()

    def test_tier_b_needs_the_tier_line_in_the_review_record(self):
        self.tier_b([self.changed("src/View.cs")], record_tier=False)
        self.assert_owner_gated("no 'Approval tier: B' line")

    def test_tier_b_record_tier_line_must_be_exact(self):
        self.reader.payloads["reviews-1"][0]["body"] += (
            "\nApproval tier: B2\nnot Approval tier: B")
        self.tier_b([self.changed("src/View.cs")], record_tier=False)
        self.assert_owner_gated("no 'Approval tier: B' line")

    def size_ledger(self, base, head, base_extra=None):
        base_tree = "1" * 40
        head_tree = "2" * 40
        base_blob, head_blob = "3" * 40, "4" * 40
        for commit, tree, blob, files in (
                (BASE, base_tree, base_blob, base), (HEAD, head_tree, head_blob, head)):
            self.reader.payloads[f"commit-{commit}"] = {"tree": {"sha": tree}}
            self.reader.payloads[f"tree-{tree}"] = {
                "truncated": False,
                "tree": [] if files is None else [{
                    "path": "eng/file-size-baseline.json", "mode": "100644",
                    "type": "blob", "sha": blob}]}
            if files is not None:
                self.reader.payloads[f"blob-{blob}"] = {
                    "encoding": "base64", "sha": blob,
                    "content": base64.b64encode(json.dumps(
                        {"files": files}).encode()).decode()}

    def test_tier_b_size_ledger_may_shrink_or_drop_entries(self):
        self.tier_b([self.changed("src/View.cs"),
                     self.changed("eng/file-size-baseline.json")])
        self.size_ledger({"src/A.cs": 900, "src/B.cs": 850},
                         {"src/A.cs": 880})
        self.assertIn("agent-gated", self.result()["gate"][1])

    def test_tier_b_size_ledger_cannot_raise_a_value(self):
        self.tier_b([self.changed("src/View.cs"),
                     self.changed("eng/file-size-baseline.json")])
        self.size_ledger({"src/A.cs": 900}, {"src/A.cs": 901})
        self.assert_owner_gated("raises src/A.cs from 900 to 901")

    def test_tier_b_size_ledger_cannot_add_an_entry(self):
        self.tier_b([self.changed("src/View.cs"),
                     self.changed("eng/file-size-baseline.json")])
        self.size_ledger({"src/A.cs": 900}, {"src/A.cs": 900, "src/C.cs": 810})
        self.assert_owner_gated("adds an entry for src/C.cs")

    def test_tier_b_denies_test_runner_configuration(self):
        for path in ("tests/A.Tests/xunit.runner.json",
                     "tests/A.Tests/testconfig.json",
                     "tests/A.Tests/ci.runsettings",
                     "tests/A.Tests/old.testsettings"):
            with self.subTest(path=path):
                self.tier_b([self.changed("src/View.cs"),
                             self.changed(path, status="added")])
                self.assert_owner_gated("denied path")

    def test_tier_b_size_ledger_values_must_be_whole_numbers(self):
        self.tier_b([self.changed("eng/file-size-baseline.json")])
        self.size_ledger({"src/A.cs": 900}, {"src/A.cs": True})
        with self.assertRaises(approval.InputError):
            approval.evaluate(self.reader, POLICY, None)

    def test_tier_b_size_ledger_renamed_away_is_owner_gated(self):
        self.tier_b([self.changed(
            "src/ledger.json", status="renamed",
            previous_filename="eng/file-size-baseline.json")])
        self.size_ledger({"src/A.cs": 900}, None)
        self.assert_owner_gated()

    def test_tier_b_older_record_tier_line_does_not_count(self):
        self.reader.payloads["reviews-1"][0]["body"] += "\nApproval tier: B"
        newer = review(150, user={"login": "nfc-agent-dennis40816[bot]",
                                  "id": 334370883}, state="COMMENTED",
                       commit=HEAD,
                       body=f"Review record: {HEAD} accept\nFindings: none.")
        newer["submitted_at"] = "2026-10-01T13:00:00Z"
        self.reader.payloads["reviews-1"].append(newer)
        self.tier_b([self.changed("src/View.cs")], record_tier=False)
        self.assert_owner_gated("no 'Approval tier: B' line")

    def test_tier_b_size_ledger_cannot_be_added_or_removed(self):
        self.tier_b([self.changed("eng/file-size-baseline.json", status="added")])
        self.size_ledger(None, {"src/A.cs": 900})
        self.assert_owner_gated("added or removed")

    def test_tier_b_rejects_paths_outside_the_allowed_list(self):
        for path in (".github/workflows/ci.yml", ".github/approval-policy.json",
                     "scripts/verify.ps1", "eng/code-health/projects.props",
                     "eng/architecture/other.json", "docs/adr/0001.md",
                     "VERSION", "AGENTS.md", "CONTRIBUTING.md"):
            with self.subTest(path=path):
                self.tier_b([self.changed("src/View.cs"), self.changed(path)])
                self.assert_owner_gated()

    def test_tier_b_allowed_paths_are_anchored_at_the_root(self):
        for path in ("docs/src/guide.md", ".github/workflows/src/x.yml",
                     "tools/tests/new.cs"):
            with self.subTest(path=path):
                self.tier_b([self.changed(path, status="added")])
                result = self.result()
                self.assertNotIn("agent-gated", result["gate"][1])

    def test_tier_b_modified_or_deleted_tests_are_owner_gated(self):
        for status in ("modified", "removed", "renamed"):
            with self.subTest(status=status):
                self.tier_b([self.changed("tests/A.Tests/FooTests.cs",
                                          status=status,
                                          previous_filename="tests/A.Tests/Old.cs")])
                self.assert_owner_gated("file under tests")

    def test_tier_b_allows_added_tests(self):
        self.tier_b([self.changed("src/View.cs"),
                     self.changed("tests/A.Tests/NewTests.cs", status="added")])
        self.assertIn("agent-gated", self.result()["gate"][1])

    def test_tier_b_rename_checks_the_old_path(self):
        self.tier_b([self.changed("src/Moved.cs", status="renamed",
                                  previous_filename=".github/workflows/ci.yml")])
        self.assert_owner_gated()

    def test_tier_b_ledgers_do_not_count_toward_the_line_limit(self):
        self.tier_b([self.changed("src/View.cs", additions=400, deletions=100),
                     self.changed("eng/code-health/baseline.json",
                                  additions=3000, deletions=3000),
                     self.changed("eng/code-health/test-debt.json"),
                     self.changed("eng/file-size-baseline.json"),
                     self.changed("eng/architecture/command-entries.json")])
        self.size_ledger({"src/A.cs": 900}, {"src/A.cs": 900})
        result = self.result()
        self.assertIn("agent-gated", result["gate"][1])
        self.assertIn("500 changed line(s)", result["gate"][1])

    def test_tier_b_line_limit_is_inclusive(self):
        self.tier_b([self.changed("src/View.cs", additions=300, deletions=300)])
        self.assertIn("agent-gated", self.result()["gate"][1])
        self.tier_b([self.changed("src/View.cs", additions=300, deletions=301)])
        self.assert_owner_gated("exceed the limit of 600")

    def test_tier_b_line_count_sums_all_files(self):
        self.tier_b([self.changed("src/A.cs", additions=300, deletions=0),
                     self.changed("src/B.cs", additions=250, deletions=51)])
        self.assert_owner_gated("601 changed lines")

    def test_tier_b_missing_line_counts_fail_closed(self):
        self.tier_b([{"filename": "src/View.cs", "status": "modified"}])
        with self.assertRaises(approval.InputError):
            approval.evaluate(self.reader, POLICY, None)

    def test_tier_b_veto_label_returns_to_owner(self):
        for label in ("needs-owner", "Needs-Owner"):
            with self.subTest(label=label):
                self.tier_b([self.changed("src/View.cs")], labels=["bug", label])
                self.assert_owner_gated("label needs-owner")

    def test_tier_b_owner_change_request_returns_to_owner(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["reviews-1"].append(
            review(102, state="CHANGES_REQUESTED"))
        self.assert_owner_gated("changes_requested")

    def test_tier_b_later_owner_approval_is_agent_gated_again(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["reviews-1"].append(
            review(102, state="CHANGES_REQUESTED"))
        self.reader.payloads["reviews-1"].append(review(103))
        self.assertTrue(all(ok for ok, _ in self.result().values()))

    def test_tier_b_other_users_change_request_does_not_veto(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["reviews-1"].append(
            review(102, user=OTHER, state="CHANGES_REQUESTED"))
        self.assertIn("agent-gated", self.result()["gate"][1])

    def test_tier_b_into_main_is_owner_gated(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["pull"]["base"]["ref"] = "main"
        self.reader.payloads["base-ref"]["ref"] = "refs/heads/main"
        self.assert_owner_gated("base branch main")

    def test_tier_b_can_be_switched_off_in_policy(self):
        self.tier_b([self.changed("src/View.cs")])
        policy = copy.deepcopy(POLICY)
        policy["agent_gated"]["enabled"] = False
        result = {name: (ok, reason) for name, ok, reason in
                  approval.evaluate(self.reader, policy, None)}
        self.assertIn("owner-gated", result["gate"][1])
        self.assertFalse(result["owner approval"][0])

    def test_tier_b_head_behind_base_still_fails(self):
        self.tier_b([self.changed("src/View.cs")])
        self.reader.payloads["compare"]["behind_by"] = 1
        self.assertFalse(self.result()["up to date"][0])

    def test_tier_b_denied_reason_names_the_path_in_the_check_line(self):
        self.tier_b([self.changed("src/App/App.csproj")])
        self.assertIn("tier B not granted: denied path src/App/App.csproj",
                      self.result()["gate"][1])

    def test_contributing_documents_tier_b(self):
        text = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")
        agent = POLICY["agent_gated"]
        self.assertIn("Agent-gated (tier B)", text)
        self.assertIn(f"`{agent['declaration']}`", text)
        self.assertIn(f"`{agent['veto_label']}`", text)
        self.assertIn(f"at most\n  {agent['max_changed_lines']}", text)
        for path in agent["ledger_paths"]:
            self.assertIn(f"`{path}`", text)
        for pattern in agent["extra_denied_paths"]:
            self.assertIn(f"`{pattern}`", text)


if __name__ == "__main__":
    unittest.main()
