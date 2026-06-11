#!/usr/bin/env python3
"""Qavren Swarm ephemeral coding agent.

Runs inside a disposable Docker container. Lifecycle:
  1. Copy the read-only /workspace mount into a writable /work git repo.
  2. Ingest the full codebase as context (CRLF normalized to LF).
  3. Ask the configured model backend for SEARCH/REPLACE edits.
  4. Apply edits (LF-only writes), run tests if present.
  5. Print the git diff to stdout, wrapped in sentinels, plus a JSON result line.

The host orchestrator parses ONLY the sentinel-delimited diff and the final
QAVREN_RESULT line; everything else here goes to stderr and is ignored.

Provider strategy (env QAVREN_PROVIDER):
  - "anthropic": native Anthropic SDK with extended thinking.
  - "openai":    any OpenAI-compatible endpoint. Also serves the host-side
                 Claude Code broker and local models (Ollama/LM Studio/vLLM) —
                 the container cannot tell them apart.
"""

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

WORKSPACE = Path("/workspace")   # read-only bind mount of the host workspace
# Writable copy lives under /tmp so the container can run as a non-root user.
WORK = Path(os.environ.get("QAVREN_WORK_DIR", "/tmp/qavren-work"))
TEST_TIMEOUT = int(os.environ.get("QAVREN_TEST_TIMEOUT", "300"))
# Max chars of file content inlined into the prompt. Protects small local-model context
# windows from silent overflow; omitted files are listed in a manifest (never dropped silently).
CONTEXT_BUDGET = int(os.environ.get("QAVREN_CONTEXT_BUDGET", "200000"))

# Per-job nonce (from the host) makes the output envelope unforgeable: the model never sees
# QAVREN_NONCE, so injected task text cannot fake the diff markers / result line.
_NONCE = os.environ.get("QAVREN_NONCE", "")
_SUFFIX = f":{_NONCE}" if _NONCE else ""
DIFF_START = f"===QAVREN_DIFF_START{_SUFFIX}==="
DIFF_END = f"===QAVREN_DIFF_END{_SUFFIX}==="
RESULT_PREFIX = f"QAVREN_RESULT{_SUFFIX}="

# Directories never worth feeding to the model or diffing.
SKIP_DIRS = {
    ".git", "node_modules", "bin", "obj", "dist", "build", "out",
    "__pycache__", ".venv", "venv", ".mypy_cache", ".pytest_cache",
    ".next", ".nuxt", "target", ".gradle", ".idea", ".vs", "coverage",
}
# Hard cap on a single file we will inline as context (bytes). The model context
# window is large, but a 50 MB minified vendor blob is never useful signal.
MAX_FILE_BYTES = 1_000_000


def log(*args: object) -> None:
    """Diagnostics go to stderr so stdout stays a clean diff channel."""
    print("[agent]", *args, file=sys.stderr, flush=True)


def run(cmd: list[str], cwd: Path | None = None, env: dict | None = None,
        timeout: int | None = None) -> subprocess.CompletedProcess:
    return subprocess.run(
        cmd, cwd=cwd, env=env, capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout,
    )


# --------------------------------------------------------------------------- #
# 1. Isolate workspace into a writable git repo
# --------------------------------------------------------------------------- #

def isolate_workspace() -> None:
    if not WORKSPACE.exists():
        raise RuntimeError("/workspace mount missing")
    if WORK.exists():
        shutil.rmtree(WORK)
    shutil.copytree(
        WORKSPACE, WORK,
        ignore=shutil.ignore_patterns(*SKIP_DIRS),
        symlinks=False,
    )
    env = {**os.environ, "GIT_TERMINAL_PROMPT": "0"}
    run(["git", "init", "-q"], cwd=WORK, env=env)
    run(["git", "config", "user.email", "agent@qavren.local"], cwd=WORK, env=env)
    run(["git", "config", "user.name", "Qavren Agent"], cwd=WORK, env=env)
    run(["git", "config", "core.autocrlf", "false"], cwd=WORK, env=env)
    run(["git", "add", "-A"], cwd=WORK, env=env)
    run(["git", "commit", "-q", "-m", "baseline", "--allow-empty"], cwd=WORK, env=env)


# --------------------------------------------------------------------------- #
# 2. Context gathering
# --------------------------------------------------------------------------- #

def is_binary(path: Path) -> bool:
    try:
        with path.open("rb") as fh:
            chunk = fh.read(8192)
        return b"\x00" in chunk
    except OSError:
        return True


def read_text_lf(path: Path) -> str:
    """Read a file as text, normalizing CRLF/CR to LF (for matching only)."""
    raw = path.read_bytes()
    return raw.decode("utf-8", errors="replace").replace("\r\n", "\n").replace("\r", "\n")


def detect_newline(path: Path) -> str:
    """Sniff a file's existing line ending so writes can preserve it."""
    try:
        return "\r\n" if b"\r\n" in path.read_bytes() else "\n"
    except OSError:
        return "\n"


def write_preserving(path: Path, content_lf: str, newline: str) -> None:
    """Write LF-internal content back using the file's original line ending. Writing bytes
    (not text mode) guarantees no extra translation, so a CRLF file stays CRLF and only the
    genuinely changed lines appear in the diff."""
    data = content_lf.replace("\n", "\r\n") if newline == "\r\n" else content_lf
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as fh:
        fh.write(data.encode("utf-8"))


def gather_context(task: str) -> str:
    files: list[tuple[Path, Path, int]] = []
    for path in sorted(WORK.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(WORK)
        if any(part in SKIP_DIRS for part in rel.parts):
            continue
        try:
            size = path.stat().st_size
        except OSError:
            continue
        files.append((rel, path, size))

    # Inline the files most likely relevant to the task first, so a tight budget is spent
    # on what matters when a big repo can't fit a small model's window.
    task_low = task.lower()

    def relevance(item: tuple[Path, Path, int]) -> int:
        rel = item[0]
        if rel.name.lower() in task_low:
            return 2
        if rel.stem and rel.stem.lower() in task_low:
            return 1
        return 0

    files.sort(key=lambda it: (-relevance(it), str(it[0])))

    parts: list[str] = []
    manifest: list[str] = []
    used = 0
    for rel, path, size in files:
        if size > MAX_FILE_BYTES or is_binary(path):
            manifest.append(f"{rel.as_posix()} ({size}B, binary/large)")
            continue
        body = read_text_lf(path)
        # Always inline at least the top-ranked file; otherwise stop at the budget.
        if used > 0 and used + len(body) > CONTEXT_BUDGET:
            manifest.append(f"{rel.as_posix()} ({len(body)}B, omitted: context budget)")
            continue
        parts.append(f"=== FILE: {rel.as_posix()} ===\n{body}")
        used += len(body)

    out = "\n\n".join(parts)
    if manifest:
        out += ("\n\n=== OMITTED FILES (not inlined — request explicitly if needed) ===\n"
                + "\n".join(manifest))
        log(f"context: inlined {len(parts)} files ({used} chars), {len(manifest)} omitted (budget {CONTEXT_BUDGET})")
    return out


# --------------------------------------------------------------------------- #
# 3. Prompt construction
# --------------------------------------------------------------------------- #

SYSTEM_INSTRUCTIONS = """You are a senior software engineer making a precise, minimal edit to a codebase.

Output ONLY edit blocks in this exact format (one per change), nothing else — no prose,
no explanations, no fenced code wrappers around the blocks:

FILE: relative/path/to/file.ext
<<<<<<< SEARCH
<exact existing lines to find>
=======
<replacement lines>
>>>>>>> REPLACE

Rules:
- SEARCH must match the current file content EXACTLY (whitespace included).
- To create a NEW file, leave the SEARCH section empty.
- Keep each edit as small as possible; emit multiple blocks for multiple regions.
- Use LF line endings only. Paths are relative to the repository root.
- Do not touch files unrelated to the task."""


def build_prompt(task: str, context: str) -> str:
    return (
        f"# Task\n{task}\n\n"
        f"# Repository (full source follows)\n{context}\n\n"
        "# Now produce the SEARCH/REPLACE edit blocks."
    )


# --------------------------------------------------------------------------- #
# 4. Model backends
# --------------------------------------------------------------------------- #

def call_anthropic(user_content: str) -> str:
    import anthropic

    model = os.environ.get("ANTHROPIC_MODEL", "claude-sonnet-4-6")
    budget = int(os.environ.get("THINKING_BUDGET", "8000"))
    # Hard SDK requirement: max_tokens must exceed the thinking budget.
    max_tokens = budget + int(os.environ.get("QAVREN_MAX_TOKENS", "8192"))

    client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY from env
    kwargs: dict = {
        "model": model,
        "max_tokens": max_tokens,
        "system": SYSTEM_INSTRUCTIONS,
        "messages": [{"role": "user", "content": user_content}],
    }
    if budget > 0:
        kwargs["thinking"] = {"type": "enabled", "budget_tokens": budget}
    log(f"anthropic call model={model} thinking={budget} max_tokens={max_tokens}")
    resp = client.messages.create(**kwargs)
    return "".join(block.text for block in resp.content if block.type == "text")


def call_openai(user_content: str) -> str:
    from openai import OpenAI

    base_url = os.environ.get("OPENAI_BASE_URL")
    api_key = os.environ.get("OPENAI_API_KEY", "local")
    model = os.environ.get("OPENAI_MODEL", "gpt-4o-mini")
    max_tokens = int(os.environ.get("QAVREN_MAX_TOKENS", "8192"))

    client = OpenAI(base_url=base_url, api_key=api_key)
    log(f"openai-compat call base_url={base_url} model={model}")
    resp = client.chat.completions.create(
        model=model,
        max_tokens=max_tokens,
        messages=[
            {"role": "system", "content": SYSTEM_INSTRUCTIONS},
            {"role": "user", "content": user_content},
        ],
    )
    return resp.choices[0].message.content or ""


def call_model(user_content: str) -> str:
    provider = os.environ.get("QAVREN_PROVIDER", "openai").lower()
    if provider == "anthropic":
        return call_anthropic(user_content)
    return call_openai(user_content)


# --------------------------------------------------------------------------- #
# 5. SEARCH/REPLACE applier
# --------------------------------------------------------------------------- #

BLOCK_RE = re.compile(
    r"FILE:\s*(?P<path>.+?)\s*\n"
    r"<{7}\s*SEARCH\s*\n"
    r"(?P<search>.*?)"
    r"\n={7}\s*\n"
    r"(?P<replace>.*?)"
    r"\n>{7}\s*REPLACE",
    re.DOTALL,
)


def normalize_rel(raw: str) -> Path:
    p = raw.strip().strip("`").replace("\\", "/")
    for prefix in ("/work/", "./", "/"):
        if p.startswith(prefix):
            p = p[len(prefix):]
    return Path(p)


def fuzzy_replace(current: str, search: str, replace: str) -> str | None:
    """Whitespace-tolerant fallback, used only after an exact match fails — the dominant
    failure mode for weaker local models is the right lines with the wrong leading indentation.
    Match the SEARCH block against consecutive file lines ignoring each line's leading/trailing
    whitespace, then splice in REPLACE re-indented to the file's actual indentation. Returns the
    updated content (LF) or None if no whitespace-insensitive match is found."""
    cur = current.split("\n")
    s_lines = search.split("\n")
    if s_lines and s_lines[-1] == "":
        s_lines = s_lines[:-1]
    if not s_lines:
        return None
    s_stripped = [ln.strip() for ln in s_lines]
    n = len(s_lines)

    for i in range(len(cur) - n + 1):
        if [ln.strip() for ln in cur[i:i + n]] != s_stripped:
            continue
        matched_indent = cur[i][:len(cur[i]) - len(cur[i].lstrip())]
        search_indent = s_lines[0][:len(s_lines[0]) - len(s_lines[0].lstrip())]
        r_lines = replace.split("\n")
        if r_lines and r_lines[-1] == "":
            r_lines = r_lines[:-1]
        reindented = []
        for ln in r_lines:
            if ln.strip() == "":
                reindented.append("")
            else:
                body = ln[len(search_indent):] if search_indent and ln.startswith(search_indent) else ln
                reindented.append(matched_indent + body)
        return "\n".join(cur[:i] + reindented + cur[i + n:])
    return None


def apply_edits(model_output: str) -> tuple[int, list[dict]]:
    """Apply every SEARCH/REPLACE block. Returns (applied_count, failed_blocks); each failed
    block is {path, search, replace, reason}. Retryable ones feed a single second pass."""
    applied = 0
    failed: list[dict] = []
    for m in BLOCK_RE.finditer(model_output):
        rel = normalize_rel(m.group("path"))
        search = m.group("search").replace("\r\n", "\n").replace("\r", "\n")
        replace = m.group("replace").replace("\r\n", "\n").replace("\r", "\n")
        target = (WORK / rel).resolve()

        # Containment guard: never let an edit escape the work tree.
        # is_relative_to (not str.startswith, which would accept a /tmp/qavren-work-evil sibling).
        if not target.is_relative_to(WORK.resolve()):
            log(f"REJECT out-of-tree path: {rel}")
            failed.append({"path": rel, "search": search, "replace": replace, "reason": "out-of-tree"})
            continue

        if search.strip() == "":
            # New file: default to LF (no original ending to preserve).
            body = replace if (replace == "" or replace.endswith("\n")) else replace + "\n"
            write_preserving(target, body, "\n")
            applied += 1
            log(f"create {rel}")
            continue

        if not target.exists():
            log(f"FAIL {rel}: SEARCH given but file missing")
            failed.append({"path": rel, "search": search, "replace": replace, "reason": "file missing"})
            continue

        newline = detect_newline(target)          # preserve CRLF/LF of the existing file
        current = read_text_lf(target)

        if search in current:
            updated = current.replace(search, replace, 1)
            mode = "exact"
        else:
            updated = fuzzy_replace(current, search, replace)   # whitespace-tolerant fallback
            if updated is None:
                log(f"FAIL {rel}: SEARCH text not found")
                failed.append({"path": rel, "search": search, "replace": replace, "reason": "search not found"})
                continue
            mode = "fuzzy"

        write_preserving(target, updated, newline)
        applied += 1
        log(f"edit {rel} ({mode}, {'CRLF' if newline == chr(13) + chr(10) else 'LF'})")

    log(f"edits applied={applied} failed={len(failed)}")
    return applied, failed


def build_retry_prompt(task: str, failed: list[dict]) -> str:
    """Hand the model its failed blocks plus the CURRENT file content so it can fix SEARCH
    text that didn't match verbatim — the dominant failure mode for weaker local models."""
    sections = []
    for fb in failed:
        rel: Path = fb["path"]
        target = WORK / rel
        current = read_text_lf(target) if target.exists() else "(file does not exist)"
        sections.append(
            f"## {rel.as_posix()} (reason: {fb['reason']})\n"
            f"--- your previous SEARCH ---\n{fb['search']}\n"
            f"--- intended replacement ---\n{fb['replace']}\n"
            f"--- CURRENT content of {rel.as_posix()} ---\n{current}\n")
    return (
        f"# Task\n{task}\n\n"
        "# Your previous edit blocks did NOT apply: the SEARCH text was not found verbatim.\n"
        "For each file below, re-emit a corrected SEARCH/REPLACE block whose SEARCH matches the\n"
        "CURRENT content EXACTLY. Output ONLY the corrected edit blocks.\n\n"
        + "\n".join(sections))


# --------------------------------------------------------------------------- #
# 6. Validation
# --------------------------------------------------------------------------- #

def run_tests() -> bool | None:
    """Run the project's test suite if one is detectable. None = no suite / inconclusive."""
    env = {**os.environ}
    try:
        if (WORK / "package.json").exists():
            try:
                pkg = json.loads((WORK / "package.json").read_text(encoding="utf-8", errors="replace"))
            except ValueError as exc:
                log(f"package.json parse failed: {exc}")
                pkg = {}
            if "test" in pkg.get("scripts", {}):
                log("running: npm test")
                # --ignore-scripts: do NOT execute the untrusted workspace's pre/post-install
                # lifecycle hooks (arbitrary code) just to produce a diff.
                run(["npm", "install", "--no-audit", "--no-fund", "--ignore-scripts"],
                    cwd=WORK, env=env, timeout=TEST_TIMEOUT)
                res = run(["npm", "test", "--", "--watch=false"], cwd=WORK, env=env, timeout=TEST_TIMEOUT)
                log(res.stdout[-2000:], res.stderr[-2000:])
                return res.returncode == 0

        if (WORK / "pytest.ini").exists() or (WORK / "pyproject.toml").exists() \
                or list(WORK.glob("test_*.py")) or list(WORK.glob("**/test_*.py")):
            log("running: pytest")
            res = run(["python3", "-m", "pytest", "-q"], cwd=WORK, env=env, timeout=TEST_TIMEOUT)
            log(res.stdout[-2000:], res.stderr[-2000:])
            if res.returncode == 5:  # no tests collected
                return None
            return res.returncode == 0
    except subprocess.TimeoutExpired:
        log(f"tests timed out after {TEST_TIMEOUT}s")
        return None
    except OSError as exc:
        log(f"test run failed: {exc}")
        return None

    return None


# --------------------------------------------------------------------------- #
# 7. Emit diff + result
# --------------------------------------------------------------------------- #

def emit_diff(tests_passed: bool | None, failed_hunks: int) -> None:
    diff = ""
    if (WORK / ".git").exists():
        env = {**os.environ}
        run(["git", "add", "-A"], cwd=WORK, env=env)
        # Capture in BINARY mode: text=True would apply Python universal newlines and
        # silently strip the CR bytes a CRLF file's diff must carry for `git apply`
        # to match the (still-CRLF) host file.
        raw = subprocess.run(["git", "diff", "--cached"], cwd=WORK, env=env,
                             capture_output=True).stdout
        diff = raw.decode("utf-8", errors="replace")

    print(DIFF_START)
    print(diff, end="" if diff.endswith("\n") else "\n")
    print(DIFF_END)
    result = {
        "testsPassed": tests_passed,
        "changed": bool(diff.strip()),
        "failedHunks": failed_hunks,
    }
    print(RESULT_PREFIX + json.dumps(result), flush=True)


def main() -> int:
    task = os.environ.get("QAVREN_TASK", "").strip()
    if not task:
        log("no QAVREN_TASK provided")
        emit_diff(None, 0)
        return 2
    try:
        isolate_workspace()
        context = gather_context(task)
        log(f"context bytes={len(context)}")
        output = call_model(build_prompt(task, context))
        log(f"model output bytes={len(output)}")
        applied, failed = apply_edits(output)

        # One retry pass for hunks whose SEARCH didn't match — the dominant failure mode for
        # weaker local models. Bounded to a single retry to cap cost/latency.
        retryable = [f for f in failed if f["reason"] in ("search not found", "file missing")]
        if retryable:
            log(f"retrying {len(retryable)} failed hunks")
            retry_out = call_model(build_retry_prompt(task, retryable))
            applied2, failed = apply_edits(retry_out)
            log(f"after retry: applied+={applied2} stillFailed={len(failed)}")

        tests_passed = run_tests()
        emit_diff(tests_passed, len(failed))
        return 0
    except Exception as exc:  # last-resort: still emit a (possibly empty) diff envelope
        log(f"FATAL {type(exc).__name__}: {exc}")
        try:
            emit_diff(None, -1)
        except Exception:  # noqa: BLE001
            pass
        return 1


if __name__ == "__main__":
    sys.exit(main())
