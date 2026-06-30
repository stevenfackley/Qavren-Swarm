# Product Requirements Document — Qavren Swarm

| | |
|---|---|
| **Product** | Qavren Swarm |
| **Type** | Local MCP server / developer tool |
| **Status** | Implemented (v1) |
| **Owner** | Steven Fackley |
| **Last updated** | 2026‑06‑11 |

## 1. Overview

Qavren Swarm is a local Model Context Protocol (MCP) server that lets an IDE agent harness delegate
coding tasks to **ephemeral, sandboxed Docker containers**. Each container runs an LLM‑driven coding
agent against a read‑only copy of a host workspace and returns a reviewable `git diff`. The human
applies the diff explicitly; the agent never writes to the real workspace directly.

## 2. Problem statement

A developer running an agentic IDE harness wants to:

- Run potentially **untrusted** AI‑generated edits and test execution **without risking the host
  workspace or machine**.
- Avoid **per‑token API cost** by using a flat‑rate Claude Code subscription or free local models,
  while keeping the option of the metered API.
- Use the **same tool across multiple harnesses** (Claude Code at home, OpenCode at work, Cline) and
  machines, without rewrites.

Existing in‑harness "edit the file directly" flows fail the first need (no isolation, no review gate)
and lock the user into one model/provider and one harness.

## 3. Goals

- **G1 — Isolation.** All AI‑generated edits and test execution occur in a disposable container; the
  host workspace is read‑only until the user explicitly applies a reviewed diff.
- **G2 — Provider flexibility.** Model backend is selectable per task: subscription (`claude-code`),
  local OpenAI‑compatible (`openai`), or metered API (`anthropic`). No hardcoded model IDs.
- **G3 — Harness portability.** Standard stdio MCP; works unmodified with Claude Code, OpenCode, and
  Cline.
- **G4 — Correctness on Windows.** Diffs preserve each file's line endings so `git apply` is clean on
  CRLF hosts.
- **G5 — Operability.** Jobs are observable, cancellable, and time‑bounded; the server never corrupts
  its JSON‑RPC channel.

## 4. Non‑goals

- Not a hosted/multi‑tenant service. Single user, single workstation.
- Not a general CI system; test execution is advisory signal, not a gate.
- Not a Git host or PR tool; it produces a diff and (optionally) applies it locally.
- No automatic commit/push of agent output.
- Not responsible for the model's reasoning quality beyond prompt construction and one retry.

## 5. Users & personas

- **Primary — the solo developer (you).** Runs an IDE harness, wants safe, cheap, portable agentic
  edits across a home box (Claude Code subscription) and a work box (local models).

## 6. User stories

- As a developer, I can **spawn** a coding task against a folder and get a `jobId` back immediately,
  so my harness isn't blocked.
- I can **poll status** and **list jobs** so a dropped `jobId` is recoverable.
- I can **review the diff** before anything touches my files, and **apply** it only when satisfied.
- I can **cancel** a runaway job without restarting the server.
- When a diff only **partly** applies (my workspace moved on), I still get the hunks that fit applied
  and the rejected ones handed back as a patch I can apply by hand — not an all‑or‑nothing failure.
- When a container **hangs**, the job is **paused and recoverable** (I can `resume_job` it), not lost.
- I can **choose the backend** per task to control cost.
- I can point a job at an **untrusted repo** and trust that build/test scripts run only in a locked‑
  down container.

## 7. Functional requirements

| ID | Requirement |
|----|-------------|
| FR‑1 | Expose MCP tools over stdio: `spawn_sandbox`, `check_sandbox_status`, `list_jobs`, `cancel_job`, `resume_job`, `retrieve_diff`, `retrieve_logs`, `apply_diff`. |
| FR‑2 | `spawn_sandbox` accepts `runtime` (`node`\|`python`), absolute `workspacePath`, `task`, optional `provider`, `model`, `thinkingBudget`; validates input; returns a `jobId` without blocking. |
| FR‑3 | Mount the host workspace **read‑only**; perform edits on an in‑container copy; never mutate the host except via `apply_diff`. |
| FR‑4 | Support three providers selectable per spawn: `claude-code` (default), `openai`, `anthropic`. |
| FR‑5 | For `claude-code`, bridge container model calls to the host `claude -p` CLI via an OpenAI‑compatible broker, without subscription credentials entering the container. |
| FR‑6 | The agent produces edits via a SEARCH/REPLACE protocol, applies them preserving per‑file line endings, and retries once for unmatched hunks. |
| FR‑7 | Detect and run a project test suite (`npm test` / `pytest`) when present; report pass/fail without blocking the diff. |
| FR‑8 | Return the unified `git diff`; `apply_diff` applies it to the host via `git apply` with a pre‑check. When the whole patch is rejected, retry **hunk‑by‑hunk** (default): apply each hunk that still fits and return the rejected hunks as an exportable `rejectedDiff`, without adding any host‑file write path beyond `git apply`. `allowPartial=false` restores the strict all‑or‑nothing apply. |
| FR‑9 | Track job state (`Pending`/`Running`/`Completed`/`Failed`/`Paused`) and expose it via status/list tools. |
| FR‑10 | Allow cancellation of a `Pending`/`Running` job, tearing down its container. |
| FR‑11 | Build the agent container images on demand from build context embedded in the assembly. |
| FR‑12 | When a container hangs past its wall‑clock cap, transition the job to a recoverable `Paused` state (distinct from a user cancel, which stays `Failed`); `resume_job` re‑spawns it with the original spawn params, and a Paused job is reaped to `Failed` after `QAVREN_PAUSE_GRACE_SECONDS`. |

## 8. Non‑functional requirements

| ID | Requirement |
|----|-------------|
| NFR‑1 (Safety) | Untrusted code executes only in a non‑root container with dropped capabilities, `no-new-privileges`, and PID/memory/CPU caps. Network egress can be disabled. |
| NFR‑2 (Integrity) | The MCP stdout channel carries only JSON‑RPC; all logs go to stderr + a file. The diff envelope is unforgeable (per‑job nonce). CRLF is preserved. |
| NFR‑3 (Robustness) | Every long‑running external call (container, `claude -p`, `git`) is time‑bounded with cleanup; a hung job cannot wedge the server or leak containers. |
| NFR‑4 (Portability) | Standard stdio MCP; runs against Claude Code, OpenCode, Cline. Windows host + Docker Desktop (WSL2). |
| NFR‑5 (Cost) | Default to flat‑rate/local backends; metered API is opt‑in per spawn. |
| NFR‑6 (Resource bounds) | Job registry is capped to bound memory over long sessions. |
| NFR‑7 (Testability) | Cross‑process contracts (output parsing, broker parsing, edit application) are covered by automated tests. |

## 9. Constraints & assumptions

- Windows 11 host; Docker Desktop with the Linux engine; daemon at `npipe://./pipe/docker_engine`.
- .NET 10 SDK; Git for Windows on `PATH`.
- `claude-code` requires the `claude` CLI installed and logged in on the host.
- The MCP caller (IDE harness) is trusted; the workspace and model output are not.

## 10. Success metrics

- A reviewed diff applies cleanly to a CRLF workspace with zero line‑ending churn.
- Spawning against a hostile workspace produces no host mutation prior to `apply_diff`.
- A job that hangs (dead model endpoint, stalled install) self‑terminates within the configured
  timeout and is cancellable; no orphaned containers remain.
- The same binary registers and serves all 6 tools under Claude Code and OpenCode.

## 11. Out of scope / future

- Multi‑round agentic loops beyond one retry.
- A retention policy beyond the current count cap (oldest‑finished eviction at 200 jobs).
- Additional providers (e.g. Gemini) — additive via the existing provider seam.

See the [SDD](SDD.md) for how these requirements are realized and the [STD](STD.md) for verification.
