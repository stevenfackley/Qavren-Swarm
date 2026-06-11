# Qavren Swarm

A local **MCP server** that orchestrates ephemeral, Docker-isolated coding agents on a Windows
workstation. An IDE harness (Claude Code, OpenCode, Cline, …) talks JSON-RPC over **stdio**;
each task spawns a disposable container that edits a **read-only** copy of your workspace and
returns a `git diff`. Nothing touches your real files until you explicitly call `apply_diff`.

The model backend is pluggable per task:

| Provider      | Backend                                            | Cost            |
|---------------|----------------------------------------------------|-----------------|
| `claude-code` | Your logged-in Claude Code CLI via a host broker   | flat-rate (sub) |
| `openai`      | Any OpenAI-compatible endpoint (Ollama/LM Studio/vLLM) | local/free   |
| `anthropic`   | Metered Anthropic API                              | per-token       |

Default is `claude-code`.

## How it works

```
IDE harness ──stdio JSON-RPC──▶ QavrenSwarm (this server)
                                   │
                   ┌───────────────┼────────────────────────────┐
                   ▼               ▼                            ▼
            Docker.DotNet     Kestrel broker             JobStateStore
            (npipe)           /v1/chat/completions
                   │               │ (claude-code only)
                   ▼               ▼
        ephemeral container   host `claude -p`  (subscription, file tools disabled)
          /workspace (ro) ──▶ /work (copy) ──▶ model ──▶ SEARCH/REPLACE ──▶ git diff
```

For `claude-code`, the container only ever speaks **OpenAI**: its `OPENAI_BASE_URL` points at
the in-process broker, which bridges to `claude -p` on the host. Subscription credentials never
enter a container.

## Prerequisites

- **Docker Desktop** (Linux engine) — reachable at `npipe://./pipe/docker_engine`.
- **.NET 10 SDK**.
- For the `claude-code` provider only: the **`claude` CLI**, installed and logged in on the host.
- For the `anthropic` provider only: `ANTHROPIC_API_KEY` in the server's environment.

## Build

```powershell
dotnet publish -c Release
# → bin\Release\net10.0\QavrenSwarm.dll
```

## Register the server (same binary, three hosts)

> Only the `anthropic` provider needs `ANTHROPIC_API_KEY`. For `claude-code`/`openai`, omit it.

**Claude Code** — `.mcp.json` (or `claude mcp add qavren-swarm -- dotnet <path>\QavrenSwarm.dll`):

```json
{
  "mcpServers": {
    "qavren-swarm": {
      "command": "dotnet",
      "args": ["C:\\Users\\steve\\projects\\Qavren-Swarm\\bin\\Release\\net10.0\\QavrenSwarm.dll"]
    }
  }
}
```

**OpenCode** — `opencode.json` (note: key is `environment`, not `env`):

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "qavren-swarm": {
      "type": "local",
      "command": ["dotnet", "C:\\Users\\steve\\projects\\Qavren-Swarm\\bin\\Release\\net10.0\\QavrenSwarm.dll"],
      "enabled": true
    }
  }
}
```

**Cline** — `cline_mcp_settings.json`: identical shape to the Claude Code `mcpServers` block.

## Tools

- **`spawn_sandbox(runtime, workspacePath, task, provider?, model?, thinkingBudget?)`** → `{ jobId }`.
  `runtime` = `node` | `python`. Returns immediately; the container runs in the background under a
  wall-clock timeout.
- **`check_sandbox_status(jobId)`** → `Pending | Running | Completed | Failed` (+ exit code, tests).
- **`list_jobs()`** → every job this session (newest first), to recover a dropped `jobId`.
- **`cancel_job(jobId)`** → stop a Pending/Running job: kills the container, marks it Failed.
- **`retrieve_diff(jobId)`** → the unified diff (advisory; host not modified).
- **`apply_diff(jobId)`** → `git apply` the diff to the real workspace. The only host-mutating op.

Invalid input (bad runtime/provider/path, unknown jobId, un-appliable patch) is returned as an
MCP tool error (`isError`), not a success result.

## Configuration (environment variables)

| Var | Default | Purpose |
|-----|---------|---------|
| `QAVREN_DEFAULT_PROVIDER` | `claude-code` | provider when `spawn_sandbox` omits one |
| `QAVREN_BROKER_PORT` | `8787` | Kestrel port the containers reach via `host.docker.internal` |
| `QAVREN_BROKER_ENABLED` | `true` | disable the broker (work box / no Claude Code) |
| `QAVREN_CLAUDE_BIN` | `claude` | Claude Code CLI binary name/path |
| `QAVREN_CLAUDE_CODE_MODEL` | `sonnet` | model alias passed to `claude -p --model` |
| `QAVREN_ANTHROPIC_MODEL` | `claude-sonnet-4-6` | model for the `anthropic` provider |
| `QAVREN_THINKING_BUDGET` | `8000` | extended-thinking budget (anthropic) |
| `QAVREN_OPENAI_BASE_URL` | `http://host.docker.internal:11434/v1` | local OpenAI-compatible endpoint |
| `QAVREN_OPENAI_MODEL` | `qwen2.5-coder` | model for the `openai` provider |
| `QAVREN_JOB_TIMEOUT_SECONDS` | `900` | per-job wall-clock cap (then container is killed) |
| `QAVREN_BROKER_TIMEOUT_SECONDS` | `300` | per-`claude -p` cap |
| `QAVREN_CONTEXT_BUDGET` | `200000` | max chars of repo inlined into the prompt (lower for small local models) |
| `QAVREN_PIDS_LIMIT` / `QAVREN_MEMORY_MB` / `QAVREN_CPUS` | `512` / `2048` / `2` | container resource caps |
| `QAVREN_NETWORK_MODE` | _(bridge)_ | set `none` for offline/local-only runs |
| `QAVREN_LOG_FILE` | `bin/.../logs/qavren.log` | log file (stdout is reserved for JSON-RPC) |

## Security notes

- The workspace is mounted **read-only**; edits happen on an in-container copy. `apply_diff` is
  the only operation that writes to your files.
- Containers are **hardened**: non-root user, `--cap-drop ALL`, `no-new-privileges`, and
  pids/memory/CPU caps. Test execution uses `npm install --ignore-scripts` and per-step timeouts.
  Set `QAVREN_NETWORK_MODE=none` to also cut network egress for offline/local runs.
- The broker binds `0.0.0.0:<port>` so containers can reach it, gated by a **random per-session
  bearer token** (constant-time compared). The host `claude` runs with `--disallowedTools` in an
  empty scratch directory — a pure text engine that cannot touch the host filesystem.
- The agent's stdout envelope (diff markers + result line) is stamped with a **per-job nonce** the
  model never sees, so injected task text cannot forge it.
- Each edited file's **line endings are preserved** (CRLF stays CRLF) so diffs are minimal and
  `git apply` matches cleanly on Windows hosts.
