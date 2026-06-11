# Software Design Document — Qavren Swarm

| | |
|---|---|
| **System** | Qavren Swarm |
| **Stack** | .NET 10 (Web SDK), C# 14, Docker.DotNet, ModelContextProtocol 1.4.0, Python 3.12+ agent |
| **Status** | Implemented (v1) |
| **Last updated** | 2026‑06‑11 |
| **Related** | [PRD](PRD.md) · [STD](STD.md) · [PDD](PDD.md) · [SECURITY](../SECURITY.md) |

## 1. Introduction

This document describes the design of Qavren Swarm: a local MCP server that orchestrates ephemeral
Docker containers running an LLM coding agent. It covers architecture, components, data model,
interfaces/contracts, control flow, concurrency, error handling, security, configuration, build, and
design rationale. It is intended to be exact to the implementation.

## 2. Architecture overview

A **single .NET process** simultaneously hosts:

- An **MCP server over stdio** (JSON‑RPC), the interface to the IDE harness.
- A **Kestrel HTTP endpoint** ("the broker"), reachable from containers via `host.docker.internal`,
  used by the `claude-code` provider.

It orchestrates **ephemeral Docker Linux containers** through `Docker.DotNet` over the Windows named
pipe `npipe://./pipe/docker_engine`. Each container runs `agent.py` against a read‑only bind mount of
a host workspace and emits a `git diff` on stdout.

`WebApplication.CreateBuilder` is used because `WebApplicationBuilder` is an
`IHostApplicationBuilder`, so the MCP stdio server and the Kestrel endpoint coexist in one host. See
the README "Architecture" diagram for the runtime topology.

**Invariant:** `stdout` carries only JSON‑RPC. All logging is routed to `stderr` and a file; the
agent's diff envelope is sentinel‑framed and the container's stdout is captured out‑of‑band by the
host, never interleaved with the server's own stdout.

## 3. Process & deployment model

- One long‑lived process per IDE harness session, launched by the harness as `dotnet QavrenSwarm.dll`
  over stdio.
- Kestrel binds `http://0.0.0.0:<QAVREN_BROKER_PORT>` (default 8787).
- Agent images (`qavren-agent-node:latest`, `qavren-agent-python:latest`) are built lazily on first
  use from build context embedded in the assembly.
- Containers are created per job and force‑removed on completion.

## 4. Component design

### 4.1 `Program.cs` (composition root)

- Builds a `SwarmConfig` (reads environment once).
- **Logging:** clears default providers; adds a console logger with
  `LogToStandardErrorThreshold = Trace` (all console output → stderr) plus a `FileLoggerProvider`
  (`QAVREN_LOG_FILE`, default `<bin>/logs/qavren.log`); minimum level `Information`.
- **Kestrel:** `UseUrls($"http://0.0.0.0:{BrokerPort}")`.
- **DI singletons:** `SwarmConfig`, `IDockerClient`
  (`new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient()`),
  `JobStateStore`, `DockerLifecycleManager`, `ClaudeCodeBroker`.
- `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` registers the stdio server and
  discovers `[McpServerToolType]` tools.
- When `BrokerEnabled`, maps `POST /v1/chat/completions` (→ `ClaudeCodeBroker.CompleteAsync`) and
  `GET /healthz`. The endpoint returns 401 (bad/missing bearer), 400 (unparseable / no messages), or
  502 (broker error).

### 4.2 `Services/SwarmConfig.cs`

Process‑wide configuration read once at startup. Holds all `QAVREN_*` server‑side settings and a
**per‑session broker token** = `RandomNumberGenerator.GetBytes(24)` hex (regenerated each launch;
never an env var). See §11 for the full variable list.

### 4.3 `Services/JobState.cs` + `JobStateStore.cs`

`JobStatus` enum: `Pending | Running | Completed | Failed`. `JobState` is the mutable per‑job record
(see §5). `JobStateStore` wraps a `ConcurrentDictionary<string, JobState>`:

- `Create(...)` mints a GUID‑`N` id; calls `EvictFinishedIfFull()` first.
- `EvictFinishedIfFull()` caps the store at `MaxJobs = 200`, removing the oldest **finished**
  (`Completed`/`Failed`) jobs by `FinishedUtc ?? CreatedUtc`. Pending/Running are never evicted.
- `TryGet(id, out job)` for reads.
- `Update(id, Action<JobState>)` mutates under `lock (job)` so the background runner's multi‑field
  writes are atomic with respect to one another.
- `All()` returns a snapshot array for `list_jobs`.

### 4.4 `Services/DockerLifecycleManager.cs`

Owns the container lifecycle.

- **Image build (lazy, idempotent).** `EnsureImageAsync(runtime)` checks a
  `ConcurrentDictionary<string,byte>` cache and `ImageExistsAsync` (a `ListImagesAsync` `reference`
  filter); if absent, it builds under a `SemaphoreSlim(1,1)` gate (double‑checked). The build context
  is an **in‑memory PAX tar** (`BuildContextTar`) containing the chosen `Dockerfile` (renamed to
  `Dockerfile`), `agent.py`, and `requirements.txt`, all read from embedded resources;
  `Images.BuildImageFromDockerfileAsync` performs the build (legacy builder API).
- **Run.** `RunAgentAsync(store, job, model, thinkingBudget, ct)`:
  1. Mark `Running`; `EnsureImageAsync`.
  2. Generate a **nonce** = `RandomNumberGenerator.GetBytes(12)` hex; build env (`BuildEnv`).
  3. `CreateContainerAsync` with `Tty=false`, `AttachStdout/Stderr=true`, and a hardened `HostConfig`:
     `Mounts` (bind, `Source = job.WorkspacePath`, `Target = /workspace`, `ReadOnly = true`) — note
     **`Mounts`, not `Binds`**, because `Binds` split on `:` and mangle the Windows drive‑letter
     colon; `ExtraHosts = ["host.docker.internal:host-gateway"]`; `AutoRemove=false`;
     `CapDrop=["ALL"]`; `SecurityOpt=["no-new-privileges:true"]`; `PidsLimit`, `Memory`, `NanoCPUs`
     from config; optional `NetworkMode`.
  4. `StartContainerAsync`; `WaitContainerAsync` (honours `ct` → timeout/cancel).
  5. `GetContainerLogsAsync(tty:false)` → `MultiplexedStream`; `ReadOutputToEndAsync` demuxes to
     `(stdout, stderr)`.
  6. `ParseAgentOutput(stdout, nonce)` extracts the diff + result JSON. Status = `Completed` if exit
     code 0 else `Failed`.
  7. `finally`: `RemoveContainerAsync(force)`.
  - `OperationCanceledException` → mark `Failed` with "cancelled or timed out". Other exceptions →
    `Failed` with the exception type/message. The container is removed in either case.
- **`BuildEnv`.** Common: `QAVREN_TASK`, `QAVREN_RUNTIME`, `QAVREN_NONCE`. Then forwards
  `QAVREN_CONTEXT_BUDGET`/`QAVREN_TEST_TIMEOUT`/`QAVREN_MAX_TOKENS` from the host env if set. Then,
  by provider:
  - `anthropic`: `QAVREN_PROVIDER=anthropic`, `ANTHROPIC_API_KEY` (required; throws if absent),
    `ANTHROPIC_MODEL`, `THINKING_BUDGET`.
  - `claude-code`: `QAVREN_PROVIDER=openai`, `OPENAI_BASE_URL=http://host.docker.internal:<port>/v1`,
    `OPENAI_API_KEY=<broker token>`, `OPENAI_MODEL=<claude alias>`.
  - `openai`: `QAVREN_PROVIDER=openai`, `OPENAI_BASE_URL` (per‑call `baseUrl` override, else the
    `QAVREN_OPENAI_BASE_URL` default), `OPENAI_API_KEY` (host env or `local`), `OPENAI_MODEL`.
- **`ParseAgentOutput(stdout, nonce)`.** Computes `===QAVREN_DIFF_START:<nonce>===`,
  `…_END:<nonce>===`, `QAVREN_RESULT:<nonce>=`; slices the diff body between the markers stripping
  only the single framing newline (**CR bytes preserved**); parses the trailing result JSON for
  `testsPassed` and `failedHunks`. Missing/forged (wrong‑nonce) envelope → empty diff, null tests.

### 4.5 `Services/ClaudeCodeBroker.cs`

Host‑side OpenAI‑compatible shim bridging to `claude -p`.

- Bounded by `SemaphoreSlim(2,2)`.
- `IsAuthorized(header)`: strips `Bearer `, constant‑time compares (`CryptographicOperations.
  FixedTimeEquals`) against the session token.
- `CompleteAsync(req, ct)`: flattens messages to a single prompt, resolves the model, runs in a
  temp scratch directory, returns an OpenAI `chat.completion` shape.
- `InvokeClaudeAsync`: runs `claude -p --output-format json --model <m> --disallowedTools <Bash Edit
  Write Read Glob Grep WebFetch WebSearch NotebookEdit Task>`; if `ClaudeBin` ends with `.cmd`/`.bat`
  it routes via `cmd.exe`, else executes the binary directly. The **prompt is fed over stdin** (never
  argv). A linked `CancellationTokenSource` enforces `BrokerTimeoutSeconds`; `finally` kills the
  process tree if still running.
- `ExtractResultText`: parses Claude Code's `--output-format json` (a **JSON array** of events) and
  returns the `type=="result"` element's `result` string; falls back to concatenated assistant text;
  supports the legacy single‑object form; returns trimmed raw text if not JSON.
- DTOs: `ChatMessage`, `ChatCompletionRequest`, `ChatCompletionResponse` (with `FromText`).

### 4.6 `Tools/SwarmTools.cs` (`[McpServerToolType]`)

DI‑injected (`JobStateStore`, `DockerLifecycleManager`, `SwarmConfig`, `ILogger`). Six tools (§6.1).
Input‑validation failures `throw new McpException(...)` (surfaced as `isError`). `spawn_sandbox`
creates a job, attaches a `CancellationTokenSource(JobTimeoutSeconds)` to it, and launches
`RunAgentAsync` on a background `Task` (disposing the CTS in a `finally`). `apply_diff` writes the
diff verbatim to a temp `.patch` (CR bytes preserved), runs `git apply --check` then `git apply`
(via `RunGit`, which closes stdin, drains both streams, and enforces a 60s timeout with process‑tree
kill).

### 4.7 `Services/FileLogger.cs`

`ILoggerProvider` holding one append‑mode `StreamWriter` (`FileShare.ReadWrite`, `AutoFlush`),
written under a lock; disposed with the provider. Enabled at `Debug` and above.

### 4.8 `Agent/agent.py` (in‑container agent)

Stdlib‑only at import (SDKs imported lazily inside the provider functions). Pipeline:

1. **Isolate.** `shutil.copytree('/workspace' → '/tmp/qavren-work', ignore=SKIP_DIRS,
   symlinks=False)`; `git init`, `core.autocrlf=false`, baseline commit.
2. **Gather context.** `gather_context(task)`: enumerate non‑skipped files; sort by **relevance**
   (filename/stem appearing in the task), inline text until `QAVREN_CONTEXT_BUDGET` chars are used,
   list the remainder in an "OMITTED FILES" manifest; binary/oversized files are manifested only.
3. **Model call.** `call_model(prompt)` dispatches on `QAVREN_PROVIDER`: `anthropic` (native SDK,
   extended thinking with `max_tokens > budget`) or `openai` (base‑url SDK — also serves the
   `claude-code` broker and local models).
4. **Apply edits.** `apply_edits` parses SEARCH/REPLACE blocks (`BLOCK_RE`), enforces containment via
   `Path.is_relative_to(WORK)`, matches SEARCH text (LF‑normalized), and writes back preserving the
   file's original line ending (`detect_newline` + `write_preserving`). Returns
   `(applied, failed[])`.
5. **Retry (once).** For hunks failing with "search not found"/"file missing", re‑prompt with the
   current file content (`build_retry_prompt`) and re‑apply.
6. **Test.** `run_tests`: `npm install --ignore-scripts` + `npm test` (if `package.json` has a test
   script) or `pytest` (if test files/config present); per‑step `QAVREN_TEST_TIMEOUT`; pytest exit 5
   = no tests collected → inconclusive.
7. **Emit.** `git diff --cached` captured in **binary** (so CR bytes survive), framed by the
   nonce‑stamped sentinels, followed by `QAVREN_RESULT:<nonce>={testsPassed,changed,failedHunks}`.

## 5. Data model

`JobState`:

| Field | Type | Notes |
|-------|------|-------|
| `Id` | string | GUID `N` |
| `Runtime` | string | `node` \| `python` |
| `Provider` | string | `anthropic` \| `openai` \| `claude-code` |
| `WorkspacePath` | string | absolute host path |
| `Task` | string | natural‑language task |
| `Status` | `JobStatus` | `Pending`/`Running`/`Completed`/`Failed` |
| `ContainerId` | string? | set once created |
| `ExitCode` | long | container exit code |
| `Diff` | string | captured unified diff (CR‑preserving) |
| `TestsPassed` | bool? | null = no suite / inconclusive |
| `FailedHunks` | int | unmatched edits after retry |
| `StdErrTail` | string? | last 4 KB of container stderr |
| `Error` | string? | failure reason |
| `CreatedUtc`/`FinishedUtc` | DateTimeOffset(?) | timestamps |
| `Cts` | CancellationTokenSource? | not serialized; drives timeout/cancel |

## 6. Interfaces & contracts

### 6.1 MCP tools

| Tool | Params | Success result | Error (`isError`) |
|------|--------|----------------|-------------------|
| `spawn_sandbox` | `runtime, workspacePath, task, provider?, model?, thinkingBudget?, baseUrl?` | `{jobId,status,provider,runtime}` | invalid runtime/provider/path, empty task, non‑http `baseUrl` |
| `check_sandbox_status` | `jobId` | `{jobId,status,provider,runtime,exitCode?,testsPassed,failedHunks,hasChanges,error}` | unknown jobId |
| `list_jobs` | — | `{count,jobs[]}` (newest first; `task` truncated to 80) | — |
| `cancel_job` | `jobId` | `{jobId,cancelling}` or `{jobId,status,note}` | unknown jobId |
| `retrieve_diff` | `jobId` | `{jobId,status,testsPassed,failedHunks,diff}` or running note | unknown jobId |
| `apply_diff` | `jobId` | `{jobId,applied,workspacePath}` or `{applied:false,note}` | unknown/non‑Completed jobId, `git apply` failure |

### 6.2 Broker HTTP API (OpenAI‑compatible)

`POST /v1/chat/completions`, `Authorization: Bearer <session token>`, body
`{model, messages:[{role,content}], max_tokens?, temperature?}` → `chat.completion` response with
`choices[0].message.content`. `GET /healthz` → `{ok:true}`. Auth failure 401, parse failure 400,
broker failure 502.

### 6.3 Host → container env contract

Always: `QAVREN_TASK`, `QAVREN_RUNTIME`, `QAVREN_NONCE`, `QAVREN_PROVIDER`. Forwarded when set:
`QAVREN_CONTEXT_BUDGET`, `QAVREN_TEST_TIMEOUT`, `QAVREN_MAX_TOKENS`. Provider‑specific:
`ANTHROPIC_API_KEY`/`ANTHROPIC_MODEL`/`THINKING_BUDGET` or
`OPENAI_BASE_URL`/`OPENAI_API_KEY`/`OPENAI_MODEL`. (`QAVREN_WORK_DIR` is read by the agent; defaults
to `/tmp/qavren-work` and is not forwarded by default.)

### 6.4 Container → host output envelope

```
===QAVREN_DIFF_START:<nonce>===
<unified diff, CR bytes preserved>
===QAVREN_DIFF_END:<nonce>===
QAVREN_RESULT:<nonce>={"testsPassed":bool|null,"changed":bool,"failedHunks":int}
```

### 6.5 SEARCH/REPLACE edit protocol

```
FILE: relative/path.ext
<<<<<<< SEARCH
<exact current lines, or blank for a new file>
=======
<replacement lines>
>>>>>>> REPLACE
```

Chosen over function‑calling because it is portable across all three backends (local models and the
`claude -p` broker cannot be assumed to support tool‑use) and token‑cheaper than full‑file rewrites.

## 7. Key sequence flows

**Spawn → complete.** harness `spawn_sandbox` → validate, create job + CTS, return jobId → background
`RunAgentAsync` (ensure image → create hardened container → start → wait → demux logs → parse
nonce envelope → store) → harness polls `check_sandbox_status` → `retrieve_diff`.

**`claude-code` model call.** container `agent.py` (openai SDK) → `POST host.docker.internal/v1/chat/
completions` (bearer) → broker validates → `claude -p` (stdin prompt, tools disabled, scratch cwd) →
parse result event → OpenAI response → container applies edits.

**Apply.** harness `apply_diff` → write temp patch (verbatim) → `git apply --check` → `git apply` →
host workspace updated.

## 8. Concurrency model

- `spawn_sandbox` returns immediately; the run executes on a fire‑and‑forget `Task` that records all
  outcomes into the store (its own `try/catch`), so faults are observable as `Failed` jobs.
- Per‑job `CancellationTokenSource` (timeout + `cancel_job`) is threaded through every Docker await.
- Image builds are serialized by a `SemaphoreSlim(1,1)` with double‑checked existence; the build
  cache is a `ConcurrentDictionary`.
- Job mutations are serialized per‑job via `lock (job)` in `Update`.
- The broker bounds concurrent `claude -p` processes with `SemaphoreSlim(2,2)`.

## 9. Error handling & timeouts

| Path | Bound | On expiry |
|------|-------|-----------|
| Whole job | `QAVREN_JOB_TIMEOUT_SECONDS` (900) | cancel awaits → `Failed`, container force‑removed |
| `claude -p` | `QAVREN_BROKER_TIMEOUT_SECONDS` (300) | 502 + process‑tree kill |
| `git apply` | 60 s | tool error + process‑tree kill |
| tests | `QAVREN_TEST_TIMEOUT` (300) | inconclusive (`testsPassed=null`) |

The agent's `main` wraps the pipeline; a fatal exception still emits a (possibly empty) envelope so
the host always has something to parse.

## 10. Security design

See [SECURITY.md](../SECURITY.md) for the threat model. Code‑level controls: read‑only bind mount;
non‑root images; `CapDrop ALL` + `no-new-privileges` + PID/mem/CPU caps; optional `NetworkMode=none`;
`npm install --ignore-scripts`; subprocess timeouts; `is_relative_to` containment in the agent and
`git apply`'s own `.git/`/`..` rejection on the host; broker bearer token (constant‑time) +
`--disallowedTools` + scratch cwd; per‑job nonce on the output envelope; secrets confined to env and
never logged.

## 11. Configuration

See the README "Configuration" tables for the authoritative list of server‑side and agent‑forwarded
variables, their defaults, and purposes.

## 12. Build & packaging

- `Microsoft.NET.Sdk.Web`, `net10.0`, `OutputType=Exe`. The Web SDK supplies the ASP.NET framework
  reference used by Kestrel (chosen over `HttpListener` to avoid Windows http.sys URL‑ACL setup).
- `Agent/*` are `<EmbeddedResource>` with stable logical names; the image build tar is assembled from
  them at runtime — no external `docker build`.
- `InternalsVisibleTo("QavrenSwarm.Tests")` exposes the two pure parsers for unit testing; the
  `tests/` sources are excluded from the app's compile.

## 13. Design decisions & rationale

| Decision | Rationale |
|----------|-----------|
| `HostConfig.Mounts` not `Binds` | `Binds` split on `:`, mangling `C:\...` drive‑letter colons on Windows. |
| OpenAI‑compatible broker for `claude-code` | Collapses three backends into two wire protocols in the agent; subscription creds stay on the host. |
| SEARCH/REPLACE over function‑calling | Portable across local models and the broker; token‑cheaper. |
| Per‑file CRLF preservation | Minimal diffs; clean `git apply` on Windows. |
| Per‑job nonce envelope | Prevents prompt‑injected task text forging the result. |
| Lazy image build from embedded tar | Self‑contained binary; no install step; fast startup. |
| stdout reserved for JSON‑RPC | MCP stdio dies on any non‑protocol stdout; logs go to stderr/file. |
| Read‑only mount + explicit `apply_diff` | Turns "what did the agent do" into a review problem, not a trust problem. |

## 14. Dependencies

`ModelContextProtocol` 1.4.0, `Docker.DotNet` 3.125.15, ASP.NET Core (Web SDK), .NET 10 BCL
(`System.Formats.Tar`, `System.Text.Json`, `System.Security.Cryptography`). Agent: `anthropic`,
`openai` Python SDKs, `git`, Node/Python runtimes. Tests: xUnit, `Microsoft.NET.Test.Sdk`, pytest.

## 15. Known limitations

Jobs are in‑memory only (lost on restart); one retry pass only; relevance heuristic is filename‑based;
`anthropic` forwards a long‑lived key into the container; broker binds `0.0.0.0`. See the PRD §11 and
SECURITY.md residual risks.
