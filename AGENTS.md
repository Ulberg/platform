# AGENTS.md

Instructions for every agent (and human) working in this repository. Read this file before doing anything. The plan for the whole system lives in `Ulberg/todo/PLAN.md` (sections 2, 5, 7, 9 and 12 cover this repo).

## 1. Prompt log (`.prompts/`) — mandatory

All prompts are stored in the `.prompts/` folder. It contains **everything ever written by anyone in this repo**: every user prompt and every final agent reply, from every person and every session. It is the project's long-term context and must be consulted for history before making decisions.

Rules:

- Store **only** user prompts and final replies. Never store chain-of-thought, reasoning, intermediate tool output or tool calls.
- One `.txt` file per message, never edited after it is written (append-only history).
- Path: `.prompts/<YYYY-MM-DD>/<HHMMSS>Z_<author>_<role>.txt`
  - time is UTC, `<author>` is the human who drove the session (lowercase handle), `<role>` is `user` or `assistant`.
- Each file starts with a header, a blank line, then the message verbatim:

  ```
  author: <handle>
  role: user | assistant
  timestamp: <ISO 8601 UTC>
  session: <session id or "unknown">
  ```

- **Claude Code writes the log automatically** with the hooks in `.claude/settings.json` (`.claude/hooks/prompt_log.py`). Don't also write it by hand in a Claude Code session, or the turn gets logged twice.
- Other agents and tools without these hooks must write the files themselves: the user prompt file when they receive a prompt, the assistant file when they give the final reply. If a turn is skipped, add it later instead of leaving a gap.
- CI checks the format (`.github/scripts/check_prompt_log.py`).
- Never put secrets, tokens or credentials in a prompt file. If a prompt contains one, redact it as `[REDACTED]` and tell the user.

## 2. Infrastructure- and AI-first

When developing in this project everything is infrastructure and AI-first. That means that before developing any features we always ensure the proper infrastructure is in place, an example is that we start the whole project by setting up CI/CD, access controls (both in infra and in application) before doing anything else. This includes things like tracing, telemetry, and logs. Also setting up A2A integrations should be a first-class citizen, meaning that we always implement it as an integrated part of each feature, taking into consideration if there are any security concerns by giving agents access to any given functionality. We also want to keep the application clean, so we always fix a problem before moving on to the next task. No broken-windows.

In practice, a change is not done until:

- It builds, is formatted, and all tests pass in CI.
- It is authorized (deny by default) both in infrastructure and in the application.
- It emits traces, metrics and structured logs that let an agent debug it end to end (frontend → backend → database → infrastructure).
- Agents with the right role can observe it (logs, metrics, traces, read-only access), with the security implications written down in the PR.
- No known warnings, failing checks or TODO-hacks are left behind. If you find a broken window, fix it (or stop and raise it) before continuing.

## 3. Working agreement

- This repo is the **platform**. Only infra developers may change it, and only platform leads may approve. Both are defined in `access/people.yaml`.
- Nobody applies infrastructure from a laptop. `pulumi up` runs only in CI, in the `dev` GitHub Environment, after a platform lead approves. Locally, use `pulumi preview` and the local `kind` platform.
- Applications declare their needs in `platform.yaml` in their own repo. This repo validates those requests against policy and decides how to fulfil them. Never give an application repo credentials or direct control.
- Ask before making unusual or hard-to-reverse choices. The project owner does not want to be surprised by awkward choices.
- **F# code follows [`docs/fsharp-style.md`](docs/fsharp-style.md)**, in every file: input → domain → action → report, no mutation, no `null`, parse-don't-validate. CI enforces what it can.
- No git pre-commit hooks. Every check runs in CI.
- Never commit secrets, private keys or third-party reference material.

## 4. Claude's Azure identity (D41)

Claude never uses your `az` login. Each infra developer's Claude has its own Entra account, `<name>-agent@<tenant>`, displayed as "Claude agent of <name> (<GitHub handle>)", with you as its manager and employee type `Agent` (`agents` in `access/people.yaml`). It is in the `platform-agents` group:

- **Azure (subscription):** Reader, Monitoring Reader, Log Analytics Reader, Cost Management Reader. **Entra:** Security Reader (sign-in and audit logs, Conditional Access). Nothing that writes, and no data-plane access to the Pulumi state or Key Vault.
- Never PIM-eligible, so your activations never give Claude more rights. The nightly standing-access check covers it.
- The same Conditional Access as you: a passkey, a fresh sign-in every 4 h for Azure tools and every 12 h otherwise.
- Its sign-ins and actions are logged under its own name, so "you did X" and "your Claude did X" stay distinguishable.
- `.claude/hooks/agent_identity.py` blocks `az` unless Claude Code runs with the agent's profile, and blocks commands that mention `AZURE_CONFIG_DIR`. It is a guardrail against mistakes; the agent's read-only roles are the real limit.

One-time setup, after the apply has created the account (direct links are in the PR that adds you to `agents`):

1. Assign the account an **Entra ID P2** licence.
2. Create a **Temporary Access Pass** for it.
3. In a private browser window, sign in as the agent at <https://aka.ms/mysecurityinfo> with the pass and **add a passkey**.
4. In your own terminal (not through Claude): `AZURE_CONFIG_DIR=~/.azure-agent az login --tenant <tenant-id>`, and sign in as the agent with its passkey.
5. Add `"env": {"AZURE_CONFIG_DIR": "<your home>/.azure-agent"}` to `~/.claude/settings.json` and restart Claude Code.

When Claude reports that the agent's session expired, repeat step 4.

Tests: `.claude/hooks/test_agent_identity.py` (CI, both repos); `.github/scripts/check_agent_access.sh` compares each agent's real rights with the above on every PR, on main and nightly; `scripts/agent-smoke-test.sh` is the manual test Claude runs as the agent (reads work, writes are refused).
