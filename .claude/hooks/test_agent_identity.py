#!/usr/bin/env python3
"""Tests for agent_identity.py. Run: python3 .claude/hooks/test_agent_identity.py"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HOOK = Path(__file__).with_name("agent_identity.py")
PROFILE_VAR = "AZURE_" + "CONFIG_DIR"  # split, so this file never trips the hook when Claude edits it


def run_hook(command: str, home: Path, profile: str | None) -> int:
    env = {k: v for k, v in os.environ.items() if k != PROFILE_VAR}
    env["HOME"] = str(home)
    if profile is not None:
        env[PROFILE_VAR] = profile
    event = json.dumps({"tool_input": {"command": command}})
    return subprocess.run([sys.executable, str(HOOK)], input=event, text=True, env=env, capture_output=True).returncode


class AgentIdentityTest(unittest.TestCase):
    def setUp(self):
        self.home = Path(tempfile.mkdtemp())
        self.agent = str(self.home / ".azure-agent")

    def assertBlocked(self, command, profile=None):
        self.assertEqual(run_hook(command, self.home, profile), 2, command)

    def assertAllowed(self, command, profile=None):
        self.assertEqual(run_hook(command, self.home, profile), 0, command)

    def test_az_without_the_agent_profile_is_blocked(self):
        self.assertBlocked("az account show")
        self.assertBlocked("az account show", profile=str(self.home / ".azure"))

    def test_az_with_the_agent_profile_runs(self):
        self.assertAllowed("az account show", profile=self.agent)
        self.assertAllowed("az account show", profile="~/.azure-agent")

    def test_az_is_found_anywhere_in_a_command(self):
        for command in ["echo hi && az rest --url x", "ls | az group list", "x=$(az account show)", "FOO=bar az group list"]:
            self.assertBlocked(command)

    def test_switching_profiles_is_blocked_even_with_the_agent_profile(self):
        self.assertBlocked(f"{PROFILE_VAR}=~/.azure az account show", profile=self.agent)
        self.assertBlocked(f"export {PROFILE_VAR}=/tmp/x", profile=self.agent)

    def test_other_commands_pass(self):
        for command in ["gh pr view 3", "ls azure-stuff; grep az file", "cat bootstrap/az.md", "lazy run"]:
            self.assertAllowed(command)


if __name__ == "__main__":
    unittest.main()
