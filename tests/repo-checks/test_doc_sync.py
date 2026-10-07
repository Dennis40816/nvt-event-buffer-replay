# Copyright (c) 2026 Dennis Liu. All rights reserved.
"""Exercise documentation checks with synthetic committed repositories."""

from __future__ import annotations

import copy
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools" / "repo-checks"))
import doc_sync as sync


EMPTY_CONFIG = {
    "version": 1, "mappings": [], "bilingual": {"exclude": []},
    "moduleLists": {"roots": [], "lists": []},
}


class GitRepositoryTests(unittest.TestCase):
    def setUp(self) -> None:
        # Keep temporary repositories inside the authorized worktree.
        directory = Path(__file__).resolve().parent
        temporary = tempfile.TemporaryDirectory(dir=directory, prefix=".doc-sync-test-")
        self.root = Path(temporary.name).resolve()
        self.assertTrue(self.root.is_relative_to(directory))
        self.addCleanup(temporary.cleanup)
        self.env = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
                    "PYTHONIOENCODING": "utf-8",
                    "GIT_AUTHOR_DATE": "2020-10-06T10:00:00+08:00",
                    "GIT_COMMITTER_DATE": "2020-10-06T10:00:00+08:00"}
        self.git("init", "-q")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.invalid",
                 "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-qm", "start")
        self.base = self.git("rev-parse", "HEAD").strip()
        self.config = copy.deepcopy(EMPTY_CONFIG)

    def git(self, *args: str) -> str:
        return subprocess.run(["git", "-C", str(self.root), *args], env=self.env,
                              capture_output=True, check=True, encoding="utf-8").stdout

    def write(self, path: str, text: str = "synthetic\n") -> Path:
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
        return target

    def commit(self) -> str:
        self.git("add", "-A")
        self.git("-c", "user.name=t", "-c", "user.email=t@example.invalid",
                 "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-qm", "change")
        return self.git("rev-parse", "HEAD").strip()

    def cli(self, *args: str) -> subprocess.CompletedProcess[str]:
        config = self.write("policy.json", json.dumps(self.config))
        return subprocess.run(
            [sys.executable, "-B", str(Path(sync.__file__)), "--repo", str(self.root),
             "--config", str(config), "--base", self.base, *args],
            env=self.env, capture_output=True, encoding="utf-8",
        )


class DocSyncTests(GitRepositoryTests):
    def mapping(self) -> None:
        self.config["mappings"] = [{"name": "sample", "paths": ["src/{lib}/{module}/**"],
                                    "docs": ["docs/{module}.md", "docs/{module}.zh-TW.md"]}]

    def test_mapping_add_modify_delete_and_rename(self) -> None:
        self.mapping()
        for module in ("Edit", "Gone", "Old"):
            self.write(f"src/Lib/{module}/file.txt")
        self.base = self.commit()
        self.write("src/Lib/Edit/file.txt", "modified\n")
        self.write("src/Lib/Added/deep/file.txt")
        (self.root / "src/Lib/Gone/file.txt").unlink()
        self.git("mv", "src/Lib/Old", "src/Lib/New")
        self.commit()
        result = self.cli()
        self.assertEqual(0, result.returncode, result.stderr)
        for module in ("Edit", "Gone", "Old", "New", "Added"):
            self.assertIn(f"docs/{module}.md", result.stdout)
        self.assertIn("mapping=10", result.stdout)

    def test_mapping_documents_in_diff_and_deduplication(self) -> None:
        self.mapping()
        self.write("src/Lib/Item/one.txt")
        self.write("src/Lib/Item/two.txt")
        self.write("docs/Item.md")
        self.write("docs/Item.zh-TW.md")
        self.commit()
        self.assertIn("mapping=0", self.cli().stdout)
        self.write("src/Lib/Item/three.txt")
        self.write("src/Lib/Item/four.txt")
        self.base = self.git("rev-parse", "HEAD").strip()
        self.commit()
        self.assertIn("mapping=2", self.cli().stdout)

    def test_segment_patterns_and_zero_length_globstar(self) -> None:
        for pattern, path, expected in (
            ("src/*/Item/**", "src/Lib/Item/file.txt", True),
            ("src/*/Item/**", "src/Lib/Deep/Item/file.txt", False),
            ("src/**/file.txt", "src/file.txt", True),
            ("src/**/file.txt", "src/a/b/file.txt", True),
            ("src/**", "src", True),
            ("src/**/**", "src/deep/file.txt", True),
            ("**", "file.txt", True),
        ):
            with self.subTest(pattern=pattern, path=path):
                self.assertEqual(expected, bool(sync.path_pattern(pattern).fullmatch(path)))
        self.assertEqual({"lib": "Lib", "module": "Item"},
                         sync.path_pattern("src/{lib}/{module}/**").fullmatch("src/Lib/Item/file.txt").groupdict())

    def test_valid_exemption_each_separator_case_and_output(self) -> None:
        self.mapping()
        self.write("src/Lib/Item/file.txt")
        self.write("guide.md")
        self.write("guide.zh-TW.md")
        self.base = self.commit()
        self.write("src/Lib/Item/file.txt", "change\n")
        self.write("guide.md", "change\n")
        self.commit()
        for separator in ("—", "–", "-"):
            with self.subTest(separator=separator):
                body = self.write("body.txt", f"Summary\n dOcS: NoNe {separator} tests only\n")
                result = self.cli("--pr-body-file", str(body), "--mode", "enforce")
                self.assertEqual(1, result.returncode)
                self.assertIn("doc-sync exemption: mapping: tests only", result.stdout)
                self.assertIn("mapping=0, bilingual=1", result.stdout)

    def test_empty_exemption_each_separator_does_not_skip_mapping(self) -> None:
        self.mapping()
        self.write("src/Lib/Item/file.txt")
        self.commit()
        for separator in ("—", "–", "-"):
            with self.subTest(separator=separator):
                body = self.write("body.txt", f"Docs: none {separator} \t\n")
                result = self.cli("--pr-body-file", str(body))
                self.assertIn("reason is missing", result.stdout)
                self.assertIn("mapping=3", result.stdout)
                self.assertNotIn("doc-sync exemption:", result.stdout)

    def test_exemption_must_be_on_one_line(self) -> None:
        self.mapping()
        self.write("src/Lib/Item/file.txt")
        self.commit()
        body = self.write("body.txt", "Docs: none\n- next paragraph\n")
        self.assertIn("mapping=2", self.cli("--pr-body-file", str(body)).stdout)

    def test_bilingual_partner_at_base_on_divergent_history(self) -> None:
        self.write("guide.md")
        ancestor = self.commit()
        self.write("guide.zh-TW.md")
        self.base = self.commit()
        self.git("checkout", "--detach", ancestor)
        self.write("guide.md", "change\n")
        self.commit()
        self.assertIn("bilingual=1", self.cli().stdout)

    def test_bilingual_both_directions_base_only_and_exclusion(self) -> None:
        for path in ("a.md", "a.zh-TW.md", "b.md", "b.zh-TW.md", "skip/c.md", "skip/c.zh-TW.md"):
            self.write(path)
        self.base = self.commit()
        self.write("a.md", "modified\n")
        (self.root / "b.zh-TW.md").unlink()
        self.write("skip/c.zh-TW.md", "modified\n")
        self.config["bilingual"]["exclude"] = ["skip/**"]
        self.commit()
        result = self.cli()
        self.assertIn("a.md: companion did not change: a.zh-TW.md", result.stdout)
        self.assertIn("b.zh-TW.md: companion did not change: b.md", result.stdout)
        self.assertIn("bilingual=2", result.stdout)
        self.assertNotIn("skip/c", result.stdout)

    def test_bilingual_rename_old_and_new_companions(self) -> None:
        for path in ("old.md", "old.zh-TW.md", "new.zh-TW.md"):
            self.write(path)
        self.base = self.commit()
        self.git("mv", "old.md", "new.md")
        self.commit()
        self.assertIn("bilingual=2", self.cli().stdout)

    def test_module_lists_inventory_reverse_and_planned_modules(self) -> None:
        self.config["moduleLists"] = {
            "roots": ["src/Lib", "src/Ui"], "lists": [
                {"path": "list.md", "patterns": ["docs/{module}.md", "alternate/{module}.md"], "reverse": True},
                {"path": "plan.md", "patterns": ["src/{lib}/{module}/"], "reverse": False},
            ],
        }
        for path in ("src/Lib/One/file.txt", "src/Ui/Two/file.txt", "src/Lib/root.txt"):
            self.write(path)
        self.write("list.md", "docs/One.md alternate/Two.md docs/Stale.md\n")
        self.write("plan.md", "`src/Lib/One/` `src/Lib/Planned/`\n")
        self.base = self.commit()
        # Neither untracked content nor the absence of a diff changes the inventory.
        self.write("src/Lib/Untracked/file.txt")
        (self.root / "src/Lib/Empty").mkdir()
        result = self.cli()
        self.assertIn("plan.md: missing module Ui/Two", result.stdout)
        self.assertIn("list.md: referenced module folder does not exist: docs/Stale.md", result.stdout)
        self.assertIn("module-list=2", result.stdout)
        self.assertNotIn("Untracked", result.stdout)
        self.assertNotIn("Empty", result.stdout)
        self.assertNotIn("Planned", result.stdout)
        (self.root / "list.md").unlink()
        self.commit()
        self.assertIn("list document does not exist at head: list.md", self.cli().stdout)

    def test_shared_module_names_across_roots_are_valid_in_reverse_lists(self) -> None:
        self.config["moduleLists"] = {
            "roots": ["src/Lib", "src/Ui"],
            "lists": [{"path": "list.md", "patterns": ["docs/{module}.md"], "reverse": True}],
        }
        self.write("src/Lib/One/file.txt")
        self.write("src/Ui/Two/file.txt")
        self.write("list.md", "docs/One.md docs/Two.md\n")
        self.commit()
        self.assertIn("module-list=0", self.cli().stdout)

    def test_core_config_maps_module_files_but_not_library_root_files(self) -> None:
        core_config = Path(sync.__file__).with_name("doc-sync.core.json")
        self.config = json.loads(core_config.read_text(encoding="utf-8-sig"))
        self.write("src/Nvt.Core/Mod/a.cs")
        self.write("src/Nvt.Core/packages.lock.json")
        self.base = self.commit()
        self.write("src/Nvt.Core/packages.lock.json", "changed\n")
        self.commit()
        self.assertIn("mapping=0", self.cli().stdout)
        self.write("src/Nvt.Core/Mod/a.cs", "changed\n")
        self.commit()
        result = self.cli()
        self.assertIn("mapping=2", result.stdout)
        self.assertIn("docs/core/modules/Mod.zh-TW.md", result.stdout)

    def test_core_config_maps_fonts_layout_to_fonts_documents(self) -> None:
        core_config = Path(sync.__file__).with_name("doc-sync.core.json")
        self.config = json.loads(core_config.read_text(encoding="utf-8-sig"))
        self.write("src/Nvt.Core.Fonts/Assets/Face/a.ttf")
        self.write("src/Nvt.Core.Fonts/licenses/Face/LICENSE")
        self.write("src/Nvt.Core.Fonts/NvtCoreFonts.cs")
        self.write("docs/core/modules/Fonts.md")
        self.write("docs/core/modules/Fonts.zh-TW.md")
        self.base = self.commit()
        self.write("src/Nvt.Core.Fonts/Assets/Face/a.ttf", "changed\n")
        self.write("src/Nvt.Core.Fonts/licenses/Face/LICENSE", "changed\n")
        self.write("docs/core/modules/Fonts.md", "changed\n")
        self.write("docs/core/modules/Fonts.zh-TW.md", "changed\n")
        head = self.commit()
        self.assertIn("mapping=0", self.cli().stdout)
        self.base = head
        self.write("src/Nvt.Core.Fonts/NvtCoreFonts.cs", "changed\n")
        self.commit()
        result = self.cli()
        self.assertIn("mapping=2", result.stdout)
        self.assertIn("docs/core/modules/Fonts.zh-TW.md", result.stdout)

    def test_translated_module_links_are_valid_in_reverse_lists(self) -> None:
        self.config["moduleLists"] = {"roots": ["src/Lib"], "lists": [
            {"path": "list.zh-TW.md", "patterns": ["docs/{module}.zh-TW.md", "docs/{module}.md"], "reverse": True},
            {"path": "list.md", "patterns": ["docs/{module}.md"], "reverse": True},
        ]}
        self.write("src/Lib/One/file.txt")
        self.write("src/Lib/Two/file.txt")
        self.write("list.zh-TW.md", "docs/One.zh-TW.md docs/Two.md\n")
        self.write("list.md", "docs/One.md docs/Two.md [中文](docs/One.zh-TW.md)\n")
        self.commit()
        self.assertIn("module-list=0", self.cli().stdout)
        # Dotted names still count as module references.
        self.write("list.md", "docs/One.md docs/Two.md\n\ndocs/Gone.Name.md docs/Gone.Name.zh-TW.md\n")
        self.commit()
        result = self.cli()
        self.assertIn("::warning file=list.md,line=3::doc-sync module-list: list.md: referenced module folder "
                      "does not exist: docs/Gone.Name.md", result.stdout)
        self.assertIn("module-list=1", result.stdout)

    def test_nested_link_labels_and_escaped_brackets(self) -> None:
        self.write("page.md", "[see [details]](missing-nested.txt)\n![a [b] c](missing-image.png)\n"
                              "\\[example](escaped-missing.txt)\n\\![shown](missing-after-bang.txt)\n"
                              "[a [b [c]]](missing-deep.txt)\n\\\\[double](missing-double.txt)\n")
        self.commit()
        result = self.cli()
        self.assertIn("links=5", result.stdout)
        self.assertIn("line=5::doc-sync links: page.md: target does not exist at head: missing-deep.txt",
                      result.stdout)
        self.assertIn("line=6::doc-sync links: page.md: target does not exist at head: missing-double.txt",
                      result.stdout)
        self.assertIn("line=1::doc-sync links: page.md: target does not exist at head: missing-nested.txt",
                      result.stdout)
        self.assertIn("line=2::doc-sync links: page.md: target does not exist at head: missing-image.png",
                      result.stdout)
        self.assertIn("missing-after-bang.txt", result.stdout)
        self.assertNotIn("escaped-missing", result.stdout)

    def test_every_check_prints_a_located_annotation(self) -> None:
        self.mapping()
        self.config["moduleLists"] = {"roots": ["src/Lib"], "lists": [
            {"path": "list.md", "patterns": ["docs/{module}.md"], "reverse": False},
            {"path": "absent.md", "patterns": ["docs/{module}.md"], "reverse": False},
        ]}
        self.write("guide.zh-TW.md")
        self.write("list.md", "nothing listed\n")
        self.base = self.commit()
        self.write("src/Lib/Mod/a.cs")
        self.write("guide.md", "first\n[gone](gone.txt)\n")
        self.commit()
        body = self.write("body.md", "Docs: none —   \n")
        lines = self.cli("--pr-body-file", str(body)).stdout.splitlines()
        for expected in (
            "::warning::doc-sync mapping: Docs: none reason is missing",
            "::warning file=src/Lib/Mod/a.cs,line=1::doc-sync mapping: sample: required document did not change: "
            "docs/Mod.md",
            "::warning file=guide.md,line=1::doc-sync bilingual: guide.md: companion did not change: guide.zh-TW.md",
            "::warning file=list.md,line=1::doc-sync module-list: list.md: missing module Lib/Mod",
            "::warning file=absent.md,line=1::doc-sync module-list: list document does not exist at head: absent.md",
            "::warning file=guide.md,line=2::doc-sync links: guide.md: target does not exist at head: gone.txt",
        ):
            self.assertIn(expected, lines)

    def test_annotations_escape_properties_and_messages(self) -> None:
        self.write("a,b%.md", "text\n\n[x](missing%2525.txt)\n")
        self.commit()
        self.assertIn("::warning file=a%2Cb%25.md,line=3::doc-sync links: a,b%25.md: target does not exist "
                      "at head: missing%2525.txt", self.cli().stdout)
        self.assertEqual("a%3Ab", sync._escape("a:b", True))

    def test_links_normalization_images_references_code_and_external_targets(self) -> None:
        for path in ("assets/a b.txt", "assets/a(b).txt", "assets/a(b(c)).txt", "assets/file.txt"):
            self.write(path)
        self.write("docs/page.md", """[space](../assets/a%20b.txt#part?x)
[root](/assets/file.txt?query#anchor)
[directory](../assets/)
[nested](../assets/a(b(c)).txt)
[angle](<../assets/a b.txt> "title")
![image](../assets/missing.png)
[reference][id]
[id]: ../assets/missing.txt "title"
[valid]: ../assets/a(b).txt
[site](https://example.invalid/x)
[site](HTTP://example.invalid/x)
[mail](mailto:t@example.invalid)
[anchor](#title)
`[inline](inline-missing.txt)`
``[inline](other-missing.txt) `nested` ``
```markdown
[fenced](fence-missing.txt)
[id]: fence-ref-missing.txt
```
~~~~
![fenced](tilde-missing.txt)
~~~~
`../other-repository/file.txt`
![spaced]`inline code`(phantom.txt)
""")
        self.commit()
        result = self.cli()
        self.assertIn("links=2", result.stdout)
        self.assertIn("assets/missing.png", result.stdout)
        self.assertIn("assets/missing.txt", result.stdout)
        self.assertNotIn("code", result.stdout)

    def test_unchanged_markdown_links_to_deleted_and_renamed_paths(self) -> None:
        self.write("deleted.txt")
        self.write("old.txt", "rename content\n")
        self.write("page.md", "[deleted](deleted.txt) [renamed](old.txt)\n")
        self.base = self.commit()
        (self.root / "deleted.txt").unlink()
        self.git("mv", "old.txt", "new.txt")
        self.commit()
        result = self.cli()
        self.assertIn("links=2", result.stdout)
        self.assertIn("links to deleted or renamed path: deleted.txt", result.stdout)
        self.assertIn("links to deleted or renamed path: old.txt", result.stdout)

    def test_unchanged_markdown_links_to_a_folder_that_vanished(self) -> None:
        self.write("tools/a/one.txt")
        self.write("tools/a/two.txt")
        self.write("page.md", "[folder](tools/a/) [kept](tools/b/)\n")
        self.write("tools/b/keep.txt")
        self.base = self.commit()
        (self.root / "tools/a/one.txt").unlink()
        (self.root / "tools/a/two.txt").unlink()
        self.commit()
        result = self.cli()
        self.assertIn("links=1", result.stdout)
        self.assertIn("links to deleted or renamed path: tools/a", result.stdout)

    def test_all_links_and_selected_head_ignore_working_tree(self) -> None:
        self.write("page.md", "[missing](missing.txt)\n")
        head = self.base = self.commit()
        self.write("page.md", "clean\n")
        self.write("missing.txt")
        self.commit()
        self.assertIn("links=0", self.cli("--head", head).stdout)
        self.assertIn("links=1", self.cli("--head", head, "--all-links").stdout)

    def test_modes_usage_git_and_file_error_exit_codes(self) -> None:
        self.write("page.md", "[missing](missing.txt)\n")
        self.commit()
        warning, error = self.cli(), self.cli("--mode", "enforce")
        self.assertEqual(0, warning.returncode)
        self.assertIn("::warning file=page.md,line=1::doc-sync links:", warning.stdout)
        self.assertEqual(1, error.returncode)
        self.assertIn("::error file=page.md,line=1::doc-sync links:", error.stdout)
        self.base = self.git("rev-parse", "HEAD").strip()
        self.assertEqual(0, self.cli("--mode", "enforce").returncode)
        for mode in ("warn", "enforce"):
            for args in (("--mode", mode, "--head", "absent-ref"),
                         ("--mode", mode, "--pr-body-file", str(self.root / "absent.txt")),
                         ("--mode", mode, "--config", str(self.root / "absent.json"))):
                with self.subTest(args=args):
                    self.assertEqual(2, self.cli(*args).returncode)
        self.assertEqual(2, self.cli("--mode", "invalid").returncode)
        result = subprocess.run([sys.executable, "-B", str(Path(sync.__file__))],
                                env=self.env, capture_output=True)
        self.assertEqual(2, result.returncode)

    def test_config_bom_validation_unknown_keys_wrong_types_and_templates(self) -> None:
        path = self.write("validation.json", "\ufeff" + json.dumps(EMPTY_CONFIG))
        self.assertEqual(EMPTY_CONFIG, sync.load_config(path))
        invalid = [None, [], {**EMPTY_CONFIG, "extra": 1}, {**EMPTY_CONFIG, "version": True},
                   {**EMPTY_CONFIG, "version": 2}, {**EMPTY_CONFIG, "mappings": {}},
                   {**EMPTY_CONFIG, "bilingual": {"exclude": "**"}},
                   {**EMPTY_CONFIG, "bilingual": {"exclude": [], "extra": 1}},
                   {**EMPTY_CONFIG, "moduleLists": {"roots": [1], "lists": []}}]
        self.mapping()
        for key, value in (("name", 1), ("paths", [1]), ("docs", "a.md"), ("extra", True),
                           ("docs", ["docs/{unknown}.md"]), ("paths", ["src/{bad}.txt/**"])):
            config = copy.deepcopy(self.config)
            config["mappings"][0][key] = value
            invalid.append(config)
        for key, value in (("path", 1), ("patterns", [1]), ("reverse", "true"),
                           ("patterns", ["docs/{unknown}.md"]), ("extra", True)):
            config = copy.deepcopy(EMPTY_CONFIG)
            rule = {"path": "list.md", "patterns": ["docs/{module}.md"], "reverse": False}
            rule[key] = value
            config["moduleLists"]["lists"] = [rule]
            invalid.append(config)
        for config in invalid:
            with self.subTest(config=config):
                path.write_text(json.dumps(config), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "config"):
                    sync.load_config(path)
        self.config = {**EMPTY_CONFIG, "extra": 1}
        for mode in ("warn", "enforce"):
            result = self.cli("--mode", mode)
            self.assertEqual(2, result.returncode)
            self.assertIn("unknown keys: extra", result.stderr)
        path.write_text("{", encoding="utf-8")
        with self.assertRaises(ValueError):
            sync.load_config(path)


if __name__ == "__main__":
    unittest.main()
