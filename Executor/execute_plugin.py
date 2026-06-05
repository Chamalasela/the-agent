#!/usr/bin/env python3
"""
Executes a Claude Code prompt in an isolated workspace.

Reads configuration from environment variables injected by the control plane:
  TENANT_ID            - identifies the tenant
  EXECUTION_ID         - unique ID for this execution
  WORK_DIR             - absolute path to the workspace (repo worktree or empty directory)
  CLAUDE_CODE_PLUGINS  - JSON array of {"plugin-name", "marketplace"?} descriptors
  PROMPT               - fully-interpolated Claude Code prompt to execute
  ANTHROPIC_API_KEY    - Anthropic API key (read automatically by the SDK)

Writes a structured JSON envelope to stdout (see `build_output`).
All progress/debug output goes to stderr so it does not pollute the result stream.
"""
import os
import re
import sys
import json
import time
import asyncio
import traceback

from claude_agent_sdk import (
    query,
    ClaudeAgentOptions,
    AssistantMessage,
    UserMessage,
    SystemMessage,
    ResultMessage,
)


# ── Logging ──────────────────────────────────────────────────────────────────

_start_time = time.monotonic()


def log(msg: str) -> None:
    elapsed = time.monotonic() - _start_time
    print(f"[executor +{elapsed:6.1f}s] {msg}", file=sys.stderr)


def log_separator(label: str) -> None:
    log(f"── {label} {'─' * max(0, 60 - len(label))}")


# ── Helpers ──────────────────────────────────────────────────────────────────

def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise EnvironmentError(f"Required environment variable '{name}' is missing or empty.")
    return value


def parse_plugins(raw: str) -> list[dict]:
    try:
        plugins = json.loads(raw)
        return [p for p in plugins if isinstance(p, dict)]
    except json.JSONDecodeError:
        return []


def parse_tool_list(raw: str | None) -> list[str]:
    """Split a comma-separated XIANIX-*-TOOLS env into a clean tool-name list."""
    if not raw:
        return []
    return [t.strip() for t in raw.split(",") if t.strip()]


def parse_int_env(name: str) -> int | None:
    """Read a positive int env var; returns None when unset, empty, or non-positive/invalid."""
    raw = os.environ.get(name)
    if not raw:
        return None
    try:
        value = int(raw.strip())
    except ValueError:
        log(f"WARNING: {name}='{raw}' is not an integer — ignoring.")
        return None
    return value if value > 0 else None


def parse_float_env(name: str) -> float | None:
    """Read a positive float env var; returns None when unset, empty, or non-positive/invalid."""
    raw = os.environ.get(name)
    if not raw:
        return None
    try:
        value = float(raw.strip())
    except ValueError:
        log(f"WARNING: {name}='{raw}' is not a number — ignoring.")
        return None
    return value if value > 0 else None


# ── Session reuse (opt-in) ─────────────────────────────────────────────────────
# Back-to-back runs against the same conversation (e.g. PR re-reviews on `synchronize`)
# can resume the prior Claude Code session instead of rebuilding context from scratch.
# The session id is persisted on the tenant volume keyed by repo + PR/issue/work-item, and
# Claude's session history is persisted via CLAUDE_CONFIG_DIR (set in run_prompt.sh). Off by
# default (XIANIX_RESUME_SESSIONS) and always best-effort — a failed resume falls back to a
# fresh run so re-reviews can never be broken by a stale/missing session.

_SESSION_DIR = "/workspace/repo/xianix-sessions"


def session_key(inputs_raw: str | None) -> str | None:
    """Stable per-conversation key (repo + PR/issue/work-item) for session resume, or None."""
    if not inputs_raw:
        return None
    try:
        data = json.loads(inputs_raw)
    except (json.JSONDecodeError, TypeError):
        return None
    if not isinstance(data, dict):
        return None

    repo = data.get("repository-name") or data.get("repository-url") or ""
    item = (data.get("pr-number") or data.get("issue-number")
            or data.get("work-item-id") or data.get("workitem-id") or "")
    if not repo or not item:
        return None
    return re.sub(r"[^A-Za-z0-9._-]", "_", f"{repo}#{item}")


def read_prior_session(key: str) -> str | None:
    try:
        with open(os.path.join(_SESSION_DIR, key), encoding="utf-8") as fh:
            return fh.read().strip() or None
    except OSError:
        return None


def persist_session(key: str, session_id: str | None) -> None:
    if not session_id:
        return
    try:
        os.makedirs(_SESSION_DIR, exist_ok=True)
        with open(os.path.join(_SESSION_DIR, key), "w", encoding="utf-8") as fh:
            fh.write(session_id)
    except OSError as exc:
        log(f"WARNING: could not persist session id for '{key}': {exc}")


async def collect_messages(prompt: str, options: ClaudeAgentOptions) -> dict:
    """Runs one query and collects the assistant output / tool uses / usage into a dict."""
    text_blocks: list[str] = []
    tool_uses: list[dict] = []
    models_seen: set[str] = set()
    result_message: ResultMessage | None = None
    turn_count = 0

    async for message in query(prompt=prompt, options=options):
        if isinstance(message, AssistantMessage):
            turn_count += 1
            log(f"[turn {turn_count}] assistant")
            process_assistant_message(message, text_blocks, tool_uses, models_seen)

        elif isinstance(message, UserMessage):
            log(f"[turn {turn_count}] tool_result")
            process_user_message(message)

        elif isinstance(message, SystemMessage):
            log("[system]")
            process_system_message(message)

        elif isinstance(message, ResultMessage):
            result_message = message
            log_separator("Result")
            process_result_message(message)

    return {
        "text_blocks": text_blocks,
        "tool_uses": tool_uses,
        "models_seen": models_seen,
        "result_message": result_message,
        "turn_count": turn_count,
    }


def plugin_names(plugins: list[dict]) -> list[str]:
    return [p.get("plugin-name", "<unknown>") for p in plugins]


def extract_usage(result_message: ResultMessage | None) -> dict:
    if result_message is None:
        return {}
    usage = getattr(result_message, "usage", None)
    return usage if isinstance(usage, dict) else {}


def truncate(text: str, max_len: int = 300) -> str:
    text = text.strip()
    if len(text) <= max_len:
        return text
    return text[:max_len] + f"... ({len(text)} chars total)"


def format_tool_input(name: str, tool_input: dict | str) -> str:
    """Extract the most meaningful part of a tool invocation for logging."""
    if isinstance(tool_input, str):
        return truncate(tool_input, 200)
    if not isinstance(tool_input, dict):
        return str(tool_input)[:200]

    if name in ("Bash", "bash"):
        return tool_input.get("command", str(tool_input))[:300]
    if name in ("Read", "read_file"):
        return tool_input.get("file_path") or tool_input.get("path", str(tool_input))
    if name in ("Write", "write_file", "Edit", "edit_file"):
        path = tool_input.get("file_path") or tool_input.get("path", "?")
        return f"{path}"
    if name in ("Search", "Grep", "grep", "search"):
        pattern = tool_input.get("pattern") or tool_input.get("query", "")
        path = tool_input.get("path") or tool_input.get("directory", "")
        return f"'{pattern}' in {path}" if path else f"'{pattern}'"

    return truncate(json.dumps(tool_input, default=str), 200)


# ── Message processing ───────────────────────────────────────────────────────

def process_assistant_message(
    message: AssistantMessage,
    text_blocks: list[str],
    tool_uses: list[dict],
    models_seen: set[str] | None = None,
) -> None:
    model = getattr(message, "model", None)
    if model:
        log(f"  model: {model}")
        if models_seen is not None:
            models_seen.add(model)

    for block in message.content:
        block_type = type(block).__name__

        if hasattr(block, "thinking"):
            thinking = getattr(block, "thinking", "")
            if thinking:
                log(f"  thinking: {truncate(thinking, 200)}")

        elif hasattr(block, "text"):
            text = block.text.strip()
            if text:
                text_blocks.append(text)
                preview = truncate(text, 150)
                log(f"  text: {preview}")

        elif hasattr(block, "name"):
            tool_input = getattr(block, "input", {})
            formatted = format_tool_input(block.name, tool_input)
            tool_uses.append({
                "tool": block.name,
                "input_preview": str(tool_input)[:200],
            })
            log(f"  ▶ {block.name}: {formatted}")

        else:
            log(f"  {block_type}: {str(block)[:150]}")


def process_user_message(message: UserMessage) -> None:
    content = message.content if isinstance(message.content, list) else [message.content]
    for block in content:
        if isinstance(block, str):
            if block.strip():
                log(f"  user: {truncate(block, 150)}")
            continue

        block_type = type(block).__name__

        is_error = getattr(block, "is_error", False)
        tool_use_id = getattr(block, "tool_use_id", None)

        if hasattr(block, "content"):
            result_content = block.content
            if isinstance(result_content, list):
                result_text = " ".join(
                    getattr(b, "text", str(b)) for b in result_content
                )
            else:
                result_text = str(result_content)

            status = "✗ error" if is_error else "✓"
            log(f"  {status} result: {truncate(result_text, 200)}")

        elif block_type not in ("str",):
            log(f"  {block_type}: {str(block)[:150]}")


def process_system_message(message: SystemMessage) -> None:
    data = getattr(message, "data", None) or {}

    session_id = getattr(message, "session_id", None) or data.get("session_id")
    if session_id:
        log(f"  session: {session_id}")

    model = getattr(message, "model", None) or data.get("model")
    if model:
        log(f"  model: {model}")

    mcp_servers = getattr(message, "mcp_servers", None) or data.get("mcp_servers")
    if mcp_servers:
        log(f"  mcp_servers: {mcp_servers}")

    content = getattr(message, "content", None)
    if content and isinstance(content, str):
        log(f"  system: {truncate(content, 200)}")


def process_result_message(message: ResultMessage) -> None:
    usage = extract_usage(message)
    cost = getattr(message, "total_cost_usd", None)
    cost_str = f"${cost:.4f}" if cost is not None else "n/a"

    log(
        f"  subtype={message.subtype} cost={cost_str} "
        f"tokens(in={usage.get('input_tokens', 0)} "
        f"out={usage.get('output_tokens', 0)} "
        f"cache_read={usage.get('cache_read_input_tokens', 0)} "
        f"cache_create={usage.get('cache_creation_input_tokens', 0)})"
    )

    model_usage = getattr(message, "model_usage", None) or getattr(message, "modelUsage", None)
    if isinstance(model_usage, dict) and model_usage:
        for model_name, stats in model_usage.items():
            log(f"  model_usage[{model_name}]: {stats}")


# ── Output ───────────────────────────────────────────────────────────────────

def build_output(
    *,
    tenant_id: str,
    execution_id: str,
    plugins: list[dict],
    status: str,
    result: str | None = None,
    tool_uses: list[dict] | None = None,
    duration_seconds: float | None = None,
    cost_usd: float | None = None,
    session_id: str | None = None,
    usage: dict | None = None,
    error: str | None = None,
    error_traceback: str | None = None,
    models: list[str] | None = None,
) -> dict:
    """
    Consistent JSON envelope for both success and error cases.
    The C# consumer reads: status, result, cost_usd, session_id,
    input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens.
    """
    usage = usage or {}
    return {
        "tenant_id": tenant_id,
        "execution_id": execution_id,
        "plugins": plugin_names(plugins),
        "status": status,
        "result": result,
        "tool_uses": tool_uses,
        "duration_seconds": round(duration_seconds, 2) if duration_seconds else None,
        "cost_usd": cost_usd,
        "session_id": session_id,
        "models": models,
        "input_tokens": usage.get("input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "cache_read_tokens": usage.get("cache_read_input_tokens"),
        "cache_creation_tokens": usage.get("cache_creation_input_tokens"),
        "error": error,
        "error_traceback": error_traceback,
    }


def emit(output: dict) -> None:
    json.dump(output, sys.stdout)
    print(file=sys.stdout)
    sys.stdout.flush()


# ── Main ─────────────────────────────────────────────────────────────────────

async def main() -> None:
    tenant_id    = require_env("TENANT_ID")
    execution_id = os.environ.get("EXECUTION_ID", "unknown")
    prompt       = require_env("PROMPT")
    work_dir     = os.environ.get("WORK_DIR", "/workspace")
    plugins      = parse_plugins(os.environ.get("CLAUDE_CODE_PLUGINS", "[]"))

    # ── Cost-control levers (all optional; injected by the control plane) ─────
    # Primary model: XIANIX_MODEL (first-class rules.json `model`) wins, then a raw
    # ANTHROPIC_MODEL passthrough (the zero-code with-envs path), else the SDK default.
    model            = os.environ.get("XIANIX_MODEL") or os.environ.get("ANTHROPIC_MODEL") or None
    # Per-execution max-turns wins; otherwise fall back to the host-wide backstop (off unless set).
    max_turns        = parse_int_env("XIANIX_MAX_TURNS") or parse_int_env("XIANIX_DEFAULT_MAX_TURNS")
    allowed_tools    = parse_tool_list(os.environ.get("XIANIX_ALLOWED_TOOLS"))
    disallowed_tools = parse_tool_list(os.environ.get("XIANIX_DISALLOWED_TOOLS"))
    max_budget_usd   = parse_float_env("XIANIX_MAX_BUDGET_USD")

    # Route Claude Code's background/side-query work (titles, summaries, etc.) to a cheap
    # Haiku-class model unless the operator already pinned one. ANTHROPIC_DEFAULT_HAIKU_MODEL
    # is the current control; ANTHROPIC_SMALL_FAST_MODEL is its deprecated alias, set too for
    # older CLI builds. setdefault keeps any explicit override intact.
    os.environ.setdefault("ANTHROPIC_DEFAULT_HAIKU_MODEL", "claude-haiku-4-5")
    os.environ.setdefault("ANTHROPIC_SMALL_FAST_MODEL", "claude-haiku-4-5")

    log_separator("Configuration")
    log(f"tenant={tenant_id} execution={execution_id}")
    log(f"work_dir={work_dir}")
    log(f"plugins={plugin_names(plugins)}")
    log(f"ANTHROPIC_API_KEY={'set' if os.environ.get('ANTHROPIC_API_KEY') else 'MISSING'}")
    log(f"model={model or '(sdk default)'} max_turns={max_turns or '(none)'} "
        f"allowed_tools={allowed_tools or '(all)'} disallowed_tools={disallowed_tools or '(none)'} "
        f"max_budget_usd={max_budget_usd if max_budget_usd is not None else '(none)'}")
    log(f"haiku_model={os.environ.get('ANTHROPIC_DEFAULT_HAIKU_MODEL')}")

    log_separator("Prompt")
    log(f"prompt_length={len(prompt)} chars, {len(prompt.splitlines())} lines")
    print("┌──────────────────────── PROMPT ────────────────────────", file=sys.stderr)
    for line in prompt.splitlines() or [""]:
        print(f"│ {line}", file=sys.stderr)
    print("└────────────────────────────────────────────────────────", file=sys.stderr)
    sys.stderr.flush()

    # Build options from only the levers that were actually set, so an unset lever falls back
    # to the SDK's own default rather than forcing an empty/zero value onto it.
    option_kwargs: dict = {
        "cwd": work_dir,
        "permission_mode": "bypassPermissions",
    }
    if model:
        option_kwargs["model"] = model
    if max_turns is not None:
        option_kwargs["max_turns"] = max_turns
    if allowed_tools:
        option_kwargs["allowed_tools"] = allowed_tools
    if disallowed_tools:
        option_kwargs["disallowed_tools"] = disallowed_tools
    if max_budget_usd is not None:
        option_kwargs["max_budget_usd"] = max_budget_usd

    # Opt-in session resume: only when explicitly enabled and a prior session exists for this
    # conversation (repo + PR/issue). Best-effort — a resume failure retries a fresh run.
    resume_enabled = os.environ.get("XIANIX_RESUME_SESSIONS", "").strip().lower() in ("1", "true", "yes")
    skey = session_key(os.environ.get("XIANIX_INPUTS")) if resume_enabled else None
    prior_sid = read_prior_session(skey) if skey else None
    log(f"session_resume={'on' if resume_enabled else 'off'} key={skey or '(none)'} "
        f"prior_session={prior_sid or '(none)'}")

    log_separator("Execution")

    if prior_sid:
        try:
            log(f"Resuming session {prior_sid}.")
            collected = await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs, resume=prior_sid))
        except Exception as exc:  # noqa: BLE001 — resume must never harden into a hard failure
            log(f"WARNING: session resume failed ({type(exc).__name__}: {exc}) — retrying fresh.")
            collected = await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs))
    else:
        collected = await collect_messages(prompt, ClaudeAgentOptions(**option_kwargs))

    text_blocks    = collected["text_blocks"]
    tool_uses      = collected["tool_uses"]
    models_seen    = collected["models_seen"]
    result_message = collected["result_message"]
    turn_count     = collected["turn_count"]

    if skey:
        persist_session(skey, getattr(result_message, "session_id", None))

    duration = time.monotonic() - _start_time
    usage = extract_usage(result_message)

    log_separator("Summary")
    log(f"turns={turn_count} text_blocks={len(text_blocks)} tool_uses={len(tool_uses)} duration={duration:.1f}s")
    if models_seen:
        log(f"models={sorted(models_seen)}")

    emit(build_output(
        tenant_id=tenant_id,
        execution_id=execution_id,
        plugins=plugins,
        status="completed",
        result="\n\n".join(text_blocks) if text_blocks else None,
        tool_uses=tool_uses or None,
        duration_seconds=duration,
        cost_usd=getattr(result_message, "total_cost_usd", None),
        session_id=getattr(result_message, "session_id", None),
        usage=usage,
        models=sorted(models_seen) if models_seen else None,
    ))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except BaseException as e:  # noqa: BLE001
        duration = time.monotonic() - _start_time
        log(f"fatal: {type(e).__name__}: {e} (after {duration:.1f}s)")

        plugins = parse_plugins(os.environ.get("CLAUDE_CODE_PLUGINS", "[]"))
        emit(build_output(
            tenant_id=os.environ.get("TENANT_ID", "unknown"),
            execution_id=os.environ.get("EXECUTION_ID", "unknown"),
            plugins=plugins,
            status="error",
            duration_seconds=duration,
            error=f"{type(e).__name__}: {e}",
            error_traceback=traceback.format_exc(),
        ))
        sys.exit(1)
