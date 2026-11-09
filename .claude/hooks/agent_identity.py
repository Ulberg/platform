#!/usr/bin/env python3
"""PreToolUse hook for Bash: Claude uses its own read-only Azure identity, never the developer's (D41).

Blocks a command that runs `az` unless Claude Code was started with AZURE_CONFIG_DIR pointing at the agent's
profile (~/.azure-agent), and blocks any command that sets AZURE_CONFIG_DIR itself. This keeps Claude off the
developer's `az` login by mistake. It is a guardrail, not a security boundary: the real limit is that the agent
account only has read roles (see AGENTS.md).
"""
import json
import os
import re
import sys

AGENT_PROFILE = os.path.realpath(os.path.expanduser("~/.azure-agent"))
SETUP = (
    "Set it up once (AGENTS.md, 'Claude's Azure identity'): sign the agent account in with "
    "`AZURE_CONFIG_DIR=~/.azure-agent az login --tenant <tenant>` in your own terminal, add "
    '"env": {"AZURE_CONFIG_DIR": "' + AGENT_PROFILE + '"} to ~/.claude/settings.json, and restart Claude Code.'
)
# `az` as a command word: at the start, or after a separator, a pipe, a subshell or `sudo`/`env`, optionally
# behind variable assignments (`FOO=bar az ...`).
AZ = re.compile(
    r"(?:^|[;&|(`]|\$\(|\b(?:sudo|env|exec|xargs)\s)\s*(?:\w+=\S*\s+)*az(?:\s|$)",
    re.MULTILINE,
)


def block(reason: str) -> None:
    print(reason, file=sys.stderr)
    sys.exit(2)


command = json.load(sys.stdin).get("tool_input", {}).get("command", "")
if "AZURE_CONFIG_DIR" in command:
    block("Claude must not choose its own Azure profile. " + SETUP)
if AZ.search(command):
    current = os.environ.get("AZURE_CONFIG_DIR", "")
    if not current or os.path.realpath(os.path.expanduser(current)) != AGENT_PROFILE:
        block("`az` runs only with Claude's agent profile, never your own login. " + SETUP)
