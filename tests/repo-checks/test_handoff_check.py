# Copyright (c) 2026 Dennis Liu. All rights reserved.
"""Exercise report-only handoff checks with synthetic WIP and Git history."""

from __future__ import annotations

import json
import subprocess
import sys
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from test_doc_sync import GitRepositoryTests
import handoff_check as handoff


class HandoffCheckTests(GitRepositoryTests):
    def report(self, text: str, *args: str) -> subprocess.CompletedProcess[str]:
        wip = self.write("wip.md", text)
        # Pin the WIP zone so the result does not depend on the test computer's zone.
        return subprocess.run([sys.executable, "-B", str(Path(handoff.__file__)),
                               "--wip", str(wip), "--repo", str(self.root), "--utc-offset", "+08:00", *args],
                              env=self.env, capture_output=True, encoding="utf-8")

    def test_first_marked_heading_includes_children_and_stops_at_peer(self) -> None:
        text = ("# Archive\n2020-10-06 12:00\n## Current 以本節為準\n2020-10-06 08:00\n"
                f"### Detail\n2020-10-06 11:00 `{self.base}`\n"
                "## Later 以本節為準\n2020-10-06 13:00\n")
        section, count = handoff.current_section(text)
        self.assertEqual(2, count)
        self.assertIn("### Detail", section)
        self.assertNotIn("Archive", section)
        self.assertNotIn("Later", section)
        result = self.report(text)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("findings=0, marked-headings=2", result.stdout)

    def test_ancestor_boundary_missing_marker_and_body_marker(self) -> None:
        section, count = handoff.current_section("### Current 以本節為準\nbody\n## End\nother\n")
        self.assertEqual("### Current 以本節為準\nbody\n", section)
        self.assertEqual(1, count)
        result = self.report(f"# Ordinary\n以本節為準\n2020-10-06 11:00 {self.base}\n")
        self.assertEqual(0, result.returncode)
        self.assertIn("no heading contains 以本節為準", result.stdout)
        self.assertIn("marked-headings=0", result.stdout)

    def test_fenced_heading_is_ignored_and_setext_heading_is_supported(self) -> None:
        text = ("```markdown\n# 以本節為準\n```\nCurrent 以本節為準\n===\n"
                f"2020-10-06 11:00 {self.base}\nEnd\n===\n")
        section, count = handoff.current_section(text)
        self.assertEqual(1, count)
        self.assertTrue(section.startswith("Current"))
        self.assertNotIn("End", section)
        self.assertIn("findings=0", self.report(text).stdout)

    def test_thematic_break_after_heading_or_list_item_keeps_the_section(self) -> None:
        text = (f"## Current 以本節為準\n### Details\n---\n---\n- item\n---\n***\n---\n"
                f"2020-10-06 11:00 {self.base}\n## End\n")
        section, count = handoff.current_section(text)
        self.assertEqual(1, count)
        self.assertIn(self.base, section)
        self.assertNotIn("End", section)
        self.assertIn("findings=0", self.report(text).stdout)

    def test_section_time_ahead_of_the_clock_is_reported(self) -> None:
        zone = timezone(timedelta(hours=8))
        text = f"# 以本節為準\n10-06 12:20 {self.base}\n"
        for now, expected in (("2020-10-06T12:00:00+08:00", 1), ("2020-10-06T12:20:00+08:00", 0)):
            with self.subTest(now=now):
                findings, _ = handoff.check_handoff(text, self.root, now=datetime.fromisoformat(now), zone=zone)
                self.assertEqual(expected, sum("later than the current time" in item for item in findings))

    def test_wip_times_use_the_given_zone_even_when_the_head_commit_is_utc(self) -> None:
        # A GitHub merge commit records UTC; the WIP still records the writer's clock.
        self.env["GIT_COMMITTER_DATE"] = "2020-10-06T03:00:00+00:00"
        head = self.commit()
        text = f"# 以本節為準\n10-06 11:59 {head}\n"
        findings, _ = handoff.check_handoff(text, self.root, now=datetime.fromisoformat("2020-10-06T04:00:00+00:00"),
                                            zone=timezone(timedelta(hours=8)))
        self.assertEqual([], findings)
        result = self.report(text, "--utc-offset", "8")
        self.assertEqual(2, result.returncode)
        self.assertIn("expected +HH:MM", result.stderr)

    def test_both_timestamp_forms_latest_and_short_form_head_year(self) -> None:
        head = datetime.fromisoformat("2020-10-06T10:00:00+08:00")
        self.assertEqual(datetime.fromisoformat("2020-10-06T11:00:00+08:00"),
                         handoff.section_time("2020-10-06 08:00; 10-06 11:00; 09:xx", head))
        # A minute written as Mx is not a time; it must not hide a stale section.
        self.assertEqual(datetime.fromisoformat("2020-10-06T11:00:00+08:00"),
                         handoff.section_time("10-06 11:00; 10-06 11:2x; 10-06 12:xx", head))
        for timestamp in ("2020-10-06 11:00", "10-06 11:00"):
            with self.subTest(timestamp=timestamp):
                result = self.report(f"# 以本節為準\n{timestamp} {self.base}\n")
                self.assertIn("findings=0", result.stdout)
        result = self.report(f"# 以本節為準\n10-06 09:00 {self.base}\n")
        self.assertIn("is later than section time", result.stdout)
        self.assertIn("findings=1", result.stdout)

    def test_unrecognized_or_invalid_timestamp(self) -> None:
        for timestamp in ("09:xx", "10-06 09:xx", "2026-99-99 99:99", "2026/10/06 11:00"):
            with self.subTest(timestamp=timestamp):
                result = self.report(f"# 以本節為準\n{timestamp} {self.base}\n")
                self.assertIn("no recognized timestamp", result.stdout)

    def test_sha_prefix_matching_and_missing_sha(self) -> None:
        for size in (7, 12, 40):
            with self.subTest(size=size):
                result = self.report(f"# 以本節為準\n10-06 11:00 `{self.base[:size].upper()}`\n")
                self.assertIn("findings=0", result.stdout)
        for mention in ("", self.base[:6], self.base + "a", "0" * 7):
            with self.subTest(mention=mention):
                result = self.report(f"# 以本節為準\n10-06 11:00 {mention}\n")
                self.assertIn("does not mention head SHA", result.stdout)

    def test_branch_selection_uses_selected_commit(self) -> None:
        old = self.base
        self.write("file.txt")
        self.commit()
        text = f"# 以本節為準\n10-06 11:00 {old}\n"
        self.assertIn("findings=0", self.report(text, "--branch", old).stdout)
        self.assertIn("does not mention head SHA", self.report(text).stdout)

    def test_open_pr_full_url_comparison_uses_only_current_section(self) -> None:
        urls = [f"https://example.invalid/pull/{number}" for number in (1, 2, 3, 4, 5)]
        prs = self.write("prs.json", json.dumps([{"number": n, "url": url}
                                                 for n, url in zip((1, 2, 3, 4, 5), urls)]))
        text = (f"# Archive\n{urls[1]}\n# 以本節為準\n10-06 11:00 {self.base}\n"
                f"[first]({urls[0]})\n{urls[2]}0\n**{urls[3]}**\n{urls[4]}，下一步\n# End\n{urls[2]}\n")
        result = self.report(text, "--open-prs", str(prs))
        self.assertEqual(0, result.returncode)
        self.assertNotIn("request #1", result.stdout)
        self.assertIn(f"request #2 is missing: {urls[1]}", result.stdout)
        self.assertIn(f"request #3 is missing: {urls[2]}", result.stdout)
        self.assertNotIn("request #4", result.stdout)
        self.assertNotIn("request #5", result.stdout)
        self.assertIn("findings=2", result.stdout)

    def test_report_never_changes_input_and_file_git_usage_errors_exit_two(self) -> None:
        text = "# 以本節為準\n09:xx\n"
        before = self.git("status", "--porcelain")
        self.assertEqual(0, self.report(text).returncode)
        self.assertEqual(text, (self.root / "wip.md").read_text(encoding="utf-8"))
        self.assertEqual(before + "?? wip.md\n", self.git("status", "--porcelain"))
        for args in (("--branch", "absent-ref"),
                     ("--wip", str(self.root / "absent.md")),
                     ("--open-prs", str(self.root / "absent.json"))):
            with self.subTest(args=args):
                self.assertEqual(2, self.report(text, *args).returncode)
        for value in ({}, [1], [{"number": True, "url": "x"}], [{"number": 1}],
                      [{"number": 1, "url": ""}]):
            with self.subTest(value=value):
                path = self.write("prs.json", json.dumps(value))
                self.assertEqual(2, self.report(text, "--open-prs", str(path)).returncode)
        path = self.write("prs.json", "{")
        self.assertEqual(2, self.report(text, "--open-prs", str(path)).returncode)
        result = subprocess.run([sys.executable, "-B", str(Path(handoff.__file__))],
                                env=self.env, capture_output=True)
        self.assertEqual(2, result.returncode)


if __name__ == "__main__":
    unittest.main()
