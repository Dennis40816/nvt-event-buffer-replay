"""Synthetic REST payloads for the approval rule; no GitHub access."""

from __future__ import annotations

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
        expected = [[pattern if pattern.startswith("*.") else f"**/{pattern}",
                     "@Dennis40816"]
                    for pattern in POLICY["owner_gated_patterns"]]
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


if __name__ == "__main__":
    unittest.main()
