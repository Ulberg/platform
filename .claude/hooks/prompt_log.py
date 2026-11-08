#!/usr/bin/env python3
"""Claude Code hook that keeps the prompt log in `.prompts/` (see AGENTS.md §1).

  prompt_log.py user       UserPromptSubmit: writes <HHMMSS>Z_<author>_user.txt
  prompt_log.py assistant  Stop: writes the matching _assistant.txt

Only the user's prompt and the final reply text are stored. Thinking blocks,
tool calls, tool output and interim text between tool calls are never written.
Known secret formats are redacted. The hook never blocks Claude: any error is
reported on stderr and the hook exits 0.
"""

import datetime
import getpass
import json
import os
import re
import sys
from pathlib import Path

SECRET_PATTERNS = [
    re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----"),
    re.compile(r"\bgh[pousr]_[A-Za-z0-9]{36,}\b"),
    re.compile(r"\bgithub_pat_[A-Za-z0-9_]{40,}\b"),
    re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    re.compile(r"\bsk-ant-[A-Za-z0-9_-]{20,}"),
    re.compile(r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
    re.compile(r"https://start\.1password\.com/open/\S+"),
]


def redact(text: str) -> str:
    for pattern in SECRET_PATTERNS:
        text = pattern.sub("[REDACTED]", text)
    return text


def author() -> str:
    return (os.environ.get("PROMPT_AUTHOR") or getpass.getuser()).lower()


def project_dir(event: dict) -> Path:
    return Path(os.environ.get("CLAUDE_PROJECT_DIR") or event.get("cwd") or ".")


def state_file(root: Path, session: str) -> Path:
    return root / ".prompts" / ".state" / session


def write_message(root: Path, stamp: datetime.datetime, role: str, session: str, body: str) -> Path:
    day = root / ".prompts" / stamp.strftime("%Y-%m-%d")
    day.mkdir(parents=True, exist_ok=True)
    path = day / f"{stamp.strftime('%H%M%S')}Z_{author()}_{role}.txt"
    header = (
        f"author: {author()}\n"
        f"role: {role}\n"
        f"timestamp: {stamp.strftime('%Y-%m-%dT%H:%M:%SZ')}\n"
        f"session: {session}\n\n"
    )
    path.write_text(header + redact(body).rstrip() + "\n", encoding="utf-8")
    return path


def is_prompt(entry: dict) -> bool:
    """A real user prompt, as opposed to a tool result fed back to the model."""
    if entry.get("type") != "user" or entry.get("isMeta"):
        return False
    content = entry.get("message", {}).get("content")
    if isinstance(content, str):
        return True
    return any(block.get("type") == "text" for block in content or [])


def final_reply(transcript: Path) -> str:
    """Text of the last assistant message(s) after the last tool call of the turn."""
    entries = [json.loads(line) for line in transcript.read_text(encoding="utf-8").splitlines() if line.strip()]
    last_prompt = max((i for i, e in enumerate(entries) if is_prompt(e)), default=-1)
    tail: list[str] = []
    for entry in entries[last_prompt + 1 :]:
        content = entry.get("message", {}).get("content")
        if not isinstance(content, list):
            continue
        if entry.get("type") == "user":
            if any(block.get("type") == "tool_result" for block in content):
                tail = []
            continue
        if entry.get("type") != "assistant":
            continue
        for block in content:
            if block.get("type") == "tool_use":
                tail = []
            elif block.get("type") == "text" and block.get("text", "").strip():
                tail.append(block["text"])
    return "\n\n".join(tail)


def on_user(event: dict) -> None:
    root, session = project_dir(event), event.get("session_id", "unknown")
    stamp = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
    write_message(root, stamp, "user", session, event.get("prompt", ""))
    state = state_file(root, session)
    state.parent.mkdir(parents=True, exist_ok=True)
    state.write_text(stamp.isoformat(), encoding="utf-8")


def on_assistant(event: dict) -> None:
    root, session = project_dir(event), event.get("session_id", "unknown")
    state = state_file(root, session)
    if not state.exists():
        return  # no prompt recorded for this turn (e.g. hooks enabled mid-turn)
    stamp = datetime.datetime.fromisoformat(state.read_text(encoding="utf-8"))
    reply = event.get("last_assistant_message") or final_reply(Path(event["transcript_path"]))
    if reply.strip():
        write_message(root, stamp, "assistant", session, reply)


def main() -> None:
    role = sys.argv[1] if len(sys.argv) > 1 else ""
    try:
        event = json.load(sys.stdin)
        {"user": on_user, "assistant": on_assistant}[role](event)
    except Exception as error:  # never block Claude because logging failed
        print(f"prompt_log: {error}", file=sys.stderr)


if __name__ == "__main__":
    main()
