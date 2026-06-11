# Project Design Document — Qavren Swarm

> "PDD" here means **Project Design Document**: how the project is structured, built, operated,
> and maintained — distinct from the [PRD](PRD.md) (what/why) and [SDD](SDD.md) (how it's built).

| | |
|---|---|
| **Project** | Qavren Swarm |
| **Repository** | `stevenfackley/Qavren-Swarm` (public) |
| **License** | MIT |
| **Status** | v1 shipped |
| **Last updated** | 2026‑06‑11 |

## 1. Project overview & objectives

Deliver a self‑contained, single‑binary MCP server that gives a solo developer a **safe, cheap,
portable** way to run agentic coding tasks in disposable Docker sandboxes. Objectives:

- Ship one binary that runs on two machines (home: Claude Code subscription; work: local models)
  and across three harnesses (Claude Code, OpenCode, Cline).
- Keep the host workspace safe by construction (read‑only mount, explicit apply, hardened
  containers).
- Be operable: observable, cancellable, time‑bounded jobs with durable logs.

## 2. Stakeholders

| Role | Who | Interest |
|------|-----|----------|
| Owner / sole developer / user | Steven Fackley | Builds, runs, and maintains the tool |
| IDE harnesses | Claude Code, OpenCode, Cline | MCP clients consuming the tools |
| Model providers | Claude Code subscription, local OpenAI‑compatible servers, Anthropic API | Backends |

## 3. Deliverables

- The `QavrenSwarm` .NET 10 application (source + buildable binary).
- Two embedded agent images (`qavren-agent-node`, `qavren-agent-python`) built on demand.
- Test suites: `tests/QavrenSwarm.Tests.csproj` (xUnit), `tests/test_agent.py` (pytest).
- Documentation: README, [PRD](PRD.md), [SDD](SDD.md), [STD](STD.md), this PDD, [SECURITY](../SECURITY.md), LICENSE.

## 4. Environments

| Environment | Provider default | Notes |
|-------------|------------------|-------|
| **Home box** | `claude-code` | `claude` CLI logged in; broker enabled; flat‑rate. |
| **Work box** | `openai` (local) | Point `QAVREN_OPENAI_BASE_URL` at Ollama/LM Studio/vLLM; set `QAVREN_DEFAULT_PROVIDER=openai`; optionally `QAVREN_BROKER_ENABLED=false` and `QAVREN_NETWORK_MODE=none`. |
| **Either** | `anthropic` | Requires `ANTHROPIC_API_KEY`; metered fallback. |

Shared prerequisites: Windows 11, Docker Desktop (Linux engine), .NET 10 SDK, Git for Windows.

## 5. Repository structure

See the README "Project layout". In short: app root (`Program.cs`, `Services/`, `Tools/`), embedded
agent build context (`Agent/`), tests (`tests/`), and docs (`docs/`). Build artifacts (`bin/`,
`obj/`, `logs/`, `__pycache__/`, `.pytest_cache/`) are git‑ignored.

## 6. Build, run & deploy

```powershell
# Build & test
dotnet build -c Release
dotnet test  tests/QavrenSwarm.Tests.csproj
python -m pytest tests/test_agent.py

# Publish (single artifact the harness launches)
dotnet publish -c Release        # -> bin/Release/net10.0/QavrenSwarm.dll
```

"Deployment" is registration with an MCP harness (see README "Register the server"). The agent
container images build themselves on the first `spawn_sandbox` of each runtime; subsequent runs reuse
them. There is no server to host and no external `docker build` step.

## 7. Configuration management

- All tunables are environment variables with safe defaults (README "Configuration"); nothing is
  hardcoded that a per‑machine setup would need to change.
- Secrets (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`) come from the environment and are never committed
  or logged. The broker token is generated fresh each process launch.
- Per‑task overrides (`provider`, `model`, `thinkingBudget`) are passed as `spawn_sandbox` arguments,
  so a single running server can mix backends without restart.

## 8. Operations / runbook

| Need | Action |
|------|--------|
| Where are logs? | `QAVREN_LOG_FILE` (default `<bin>/logs/qavren.log`); also stderr. stdout is JSON‑RPC only. |
| A job is hung | `cancel_job(jobId)`; or it self‑terminates at `QAVREN_JOB_TIMEOUT_SECONDS`. |
| Lost a `jobId` | `list_jobs()`. |
| Switch a task to a local model | pass `provider:"openai"` (and set `QAVREN_OPENAI_BASE_URL`). |
| Go fully offline | `QAVREN_NETWORK_MODE=none` + `openai`/local. |
| Disable the broker | `QAVREN_BROKER_ENABLED=false`. |
| Small local context window | lower `QAVREN_CONTEXT_BUDGET`. |
| Image seems stale after an agent change | `docker rmi qavren-agent-python:latest` (rebuilds on next spawn). |
| Port conflict | change `QAVREN_BROKER_PORT`. |

## 9. Development phases (as executed)

| Phase | Outcome |
|-------|---------|
| 0 — Design | Architecture + locked decisions (providers, OpenAI‑compatible broker, read‑only mount + explicit apply, `Mounts` not `Binds`, env‑configurable models). |
| 1 — Core build | csproj, `Program.cs`, services, tools, Dockerfiles, `agent.py`. |
| 2 — Verify core | MCP handshake (stdout clean), container e2e, CRLF correctness. |
| 3 — Multi‑provider | `openai` + `claude-code` broker bridging `claude -p`; verified live. |
| 4 — Review & fixes | Three‑reviewer council → per‑job timeout, `cancel_job`/`list_jobs`, broker timeout, `Dockerfile.node` pinning. |
| 5 — Hardening & quality | Non‑root + cap‑drop + limits + `--ignore-scripts`; failed‑hunk retry; context budget. |
| 6 — Remaining items | Per‑job nonce, MCP `isError`, job eviction, held‑writer logger, contract tests. |
| 7 — Docs & release | Full documentation suite; MIT license; public GitHub repo. |

## 10. Risk register

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| `claude-code` subscription rate limit exhausted by a swarm | Medium | Medium | Per‑spawn fallback to `openai`/local; usage is the user's own. |
| Broker reachable on an untrusted LAN | Low | High | Bearer token + `--disallowedTools`; firewall to Docker subnet or disable broker. |
| `anthropic` key forwarded into a container | Low | High | Prefer `claude-code`/`openai`; scope/rotate the key; the container is ephemeral and cap‑dropped. |
| Jobs lost on server restart | Low | Low | Resolved: jobs persist to `QAVREN_JOBS_DIR`; in‑flight jobs reload as "interrupted". |
| Poor edit quality from weak local models | Medium | Low | One retry pass; context budget; choose a stronger model/provider. |
| First‑run image build latency | High | Low | One‑time per runtime; cached thereafter. |
| Untrusted workspace runs hostile test scripts | Medium | Medium | `--ignore-scripts`, timeouts, hardened/non‑root container, optional no‑egress. |

## 11. Maintenance & roadmap

- **Maintenance:** keep `ModelContextProtocol`/`Docker.DotNet`/SDK pins current; re‑run both test
  suites after dependency or `agent.py` changes; rebuild agent images after `agent.py` changes.
- **Branching:** the genesis commit is on `main`; subsequent changes go through a feature branch and
  a squash‑merged PR (workspace convention).
- **Roadmap (candidate, see PRD §11):** additional providers via the existing seam; multi‑round
  agentic editing; CI for the integration/security verifications. (CI, CodeQL, Dependabot, fuzzy
  hunk matching, per‑call `baseUrl`, `retrieve_logs`, and job persistence have shipped.)
