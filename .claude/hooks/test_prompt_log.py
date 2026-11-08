#!/usr/bin/env python3
"""Tests for prompt_log.py. Run: python3 .claude/hooks/test_prompt_log.py"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HOOK = Path(__file__).with_name("prompt_log.py")


def run_hook(role: str, event: dict, root: Path) -> None:
    env = {**os.environ, "CLAUDE_PROJECT_DIR": str(root), "PROMPT_AUTHOR": "tester"}
    subprocess.run([sys.executable, str(HOOK), role], input=json.dumps(event), text=True, env=env, check=True)


def transcript(root: Path, entries: list) -> Path:
    path = root / "transcript.jsonl"
    path.write_text("\n".join(json.dumps(e) for e in entries), encoding="utf-8")
    return path


def log_files(root: Path) -> dict:
    return {p.name.split("_")[-1]: p.read_text() for p in (root / ".prompts").glob("*/*.txt")}


class PromptLogTest(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())

    def test_stores_prompt_and_only_final_reply_text(self):
        run_hook("user", {"session_id": "s1", "prompt": "build it"}, self.root)
        path = transcript(
            self.root,
            [
                {"type": "user", "message": {"content": "build it"}},
                {"type": "assistant", "message": {"content": [
                    {"type": "thinking", "thinking": "secret reasoning"},
                    {"type": "text", "text": "Working on it."},
                    {"type": "tool_use", "id": "t1", "name": "Bash", "input": {}},
                ]}},
                {"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t1", "content": "tool output"}]}},
                {"type": "assistant", "message": {"content": [{"type": "text", "text": "Done."}]}},
            ],
        )
        run_hook("assistant", {"session_id": "s1", "transcript_path": str(path)}, self.root)

        files = log_files(self.root)
        self.assertIn("role: user", files["user.txt"])
        self.assertTrue(files["user.txt"].endswith("\n\nbuild it\n"))
        reply = files["assistant.txt"]
        self.assertTrue(reply.endswith("\n\nDone.\n"))
        for leaked in ("secret reasoning", "Working on it.", "tool output"):
            self.assertNotIn(leaked, reply)

    def test_user_and_assistant_files_share_timestamp(self):
        run_hook("user", {"session_id": "s2", "prompt": "hi"}, self.root)
        path = transcript(self.root, [{"type": "assistant", "message": {"content": [{"type": "text", "text": "hello"}]}}])
        run_hook("assistant", {"session_id": "s2", "transcript_path": str(path)}, self.root)
        names = sorted(p.name for p in (self.root / ".prompts").glob("*/*.txt"))
        self.assertEqual(names[0].replace("assistant", "user"), names[1])

    def test_redacts_secrets(self):
        prompt = "key ghp_" + "a" * 36 + " link https://start.1password.com/open/i?a=X&v=Y"
        run_hook("user", {"session_id": "s3", "prompt": prompt}, self.root)
        stored = log_files(self.root)["user.txt"]
        self.assertNotIn("ghp_", stored)
        self.assertNotIn("1password.com", stored)
        self.assertEqual(stored.count("[REDACTED]"), 2)

    def test_never_fails_on_bad_input(self):
        env = {**os.environ, "CLAUDE_PROJECT_DIR": str(self.root)}
        result = subprocess.run([sys.executable, str(HOOK), "user"], input="not json", text=True, env=env)
        self.assertEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
