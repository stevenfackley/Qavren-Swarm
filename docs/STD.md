# Software Test Document — Qavren Swarm

| | |
|---|---|
| **System** | Qavren Swarm |
| **Status** | v1 verified |
| **Last updated** | 2026‑06‑11 |
| **Related** | [PRD](PRD.md) · [SDD](SDD.md) |

## 1. Scope

This document records the test strategy, automated test cases, integration verifications, and
results for Qavren Swarm v1. It covers the load‑bearing cross‑process/cross‑language contracts
(output parsing, broker parsing, edit application) and end‑to‑end behavior across the three
providers and the security/robustness controls.

## 2. Test approach

- **Unit / contract tests** for pure functions on both sides of a process boundary (C# parsers and
  the Python edit applier), run in CI‑style via `dotnet test` and `pytest`.
- **Integration verifications** performed manually against a real Docker daemon and (for one case) a
  live Claude Code subscription, using a mock OpenAI server to drive deterministic edits without
  model cost where possible.
- **Negative/abuse tests** for path escape, forged envelopes, timeouts, and bad input.

## 3. Test environment

| Component | Version |
|-----------|---------|
| OS | Windows 11 Pro 26200 |
| .NET SDK | 10.0.301 |
| Docker | 29.4.1 (Linux engine, Docker Desktop / WSL2) |
| Git | 2.53.0.windows.3 |
| Claude CLI | 2.1.153 |
| Python | 3.12 (container) / host Python for pytest |

## 4. Automated tests

### 4.1 xUnit contract tests — `tests/*.cs` (10, all passing)

`ParseAgentOutput` (host‑side stdout envelope parser):

| # | Test | Verifies |
|---|------|----------|
| 1 | `Extracts_diff_and_result_with_matching_nonce` | diff body + `testsPassed`/`failedHunks` parsed with the correct nonce; surrounding noise ignored |
| 2 | `Preserves_CR_bytes_in_diff_body` | CRLF bytes survive parsing (required for `git apply` on Windows) |
| 3 | `Ignores_envelope_stamped_with_a_different_nonce` | a forged/wrong‑nonce envelope yields no diff (anti‑injection) |
| 4 | `Missing_envelope_yields_empty_not_crash` | absent markers → empty result, no exception |

`ExtractResultText` (broker‑side `claude --output-format json` parser):

| # | Test | Verifies |
|---|------|----------|
| 5 | `Reads_result_from_claude_json_array` | extracts the `type=="result"` element from the event array |
| 6 | `Reads_result_from_single_object_form` | legacy single‑object form supported |
| 7 | `Falls_back_to_assistant_text_when_no_result_field` | concatenates assistant text blocks as fallback |
| 8 | `Non_json_returns_trimmed_raw` | non‑JSON stdout returned as trimmed text |

`BuildEnv` (`tests/BuildEnvTests.cs`) — container environment assembly:

| # | Test | Verifies |
|---|------|----------|
| 9 | `Openai_uses_per_call_baseurl_override` | a per‑call `baseUrl` overrides `OPENAI_BASE_URL` |
| 10 | `Openai_without_override_uses_a_default_base_url` | falls back to the configured default |

### 4.2 pytest — `tests/test_agent.py` (5, all passing)

| # | Test | Verifies |
|---|------|----------|
| 1 | `test_block_re_parses_edit_and_new_file` | `BLOCK_RE` parses both an edit and an empty‑SEARCH (new file) block |
| 2 | `test_apply_edits_preserves_crlf` | editing a CRLF file keeps CRLF (no whole‑file LF flip) |
| 3 | `test_apply_edits_creates_new_file_lf` | empty SEARCH creates a new LF file |
| 4 | `test_apply_edits_reports_nonmatching_hunk` | unmatched SEARCH recorded as `search not found`, nothing written |
| 5 | `test_containment_rejects_escape_and_writes_nothing` | `../evil.py` rejected as out‑of‑tree; no file created |

### 4.3 Running

```powershell
dotnet test tests/QavrenSwarm.Tests.csproj   # 10 passed
python -m pytest tests/test_agent.py          # 5 passed
```

## 5. Integration verifications (manual, against real Docker)

| ID | Scenario | Result |
|----|----------|--------|
| IV‑1 | MCP handshake over stdio; `tools/list` | 6 tools enumerate; **0 non‑JSON lines on stdout** (logs on stderr) |
| IV‑2 | Broker `GET /healthz`; unauthenticated `POST /v1/chat/completions` | 200; **401** without bearer |
| IV‑3 | Server builds `qavren-agent-python:latest` from the embedded tar | image built; no external `docker build` |
| IV‑4 | Agent run with no task | emits valid nonce envelope + `QAVREN_RESULT`, exit 2 |
| IV‑5 | Full mock e2e (single‑file, CRLF) | RO mount → isolated copy → SEARCH/REPLACE applied → **LF‑correct diff**, exit 0 |
| IV‑6 | Multi‑line CRLF edit (one line of six) | **minimal 2‑line diff with CR bytes preserved** (regression guard for the whole‑file‑rewrite bug) |
| IV‑7 | Host workspace after spawn+retrieve | **byte‑identical** (read‑only mount held); changes only after `apply_diff` |
| IV‑8 | `apply_diff` round‑trip on CRLF host | patch applies cleanly; file stays CRLF, only intended line changed |
| IV‑9 | Live `claude-code` provider (real subscription) | container → broker → `claude -p` → SEARCH/REPLACE → applied; **0 API tokens billed** |
| IV‑10 | Hardened non‑root image under `--cap-drop ALL --security-opt no-new-privileges --pids-limit 512 --memory 2g --cpus 2` | `uid=1000`, SDKs import, `/tmp` writable; full e2e still passes |
| IV‑11 | Job wedged on a dead model endpoint, then `cancel_job` | status → `Failed: cancelled or timed out`; container removed; no leak |
| IV‑12 | `list_jobs` empty and populated | `count:0`; then the running job with truncated task |
| IV‑13 | Failed‑hunk retry (mock returns non‑matching SEARCH, then correct) | **exactly one retry**, recovers to `failedHunks:0`, edit applied |
| IV‑14 | Context budget forwarding (`QAVREN_CONTEXT_BUDGET=40`) | agent inlines 3 files, **omits 2**, logs the budget decision |
| IV‑15 | Bad input (`runtime:"ruby"`) | tool response carries **`isError: true`** |
| IV‑16 | Post‑run resource check | no orphaned containers or `dotnet` server processes |

## 6. Negative / security tests

| ID | Attack | Result |
|----|--------|--------|
| SEC‑1 | Diff creating `.git/hooks/post-commit` | `git apply --check` **REJECTED** (`invalid path`) |
| SEC‑2 | Diff with `../escaped.txt` traversal | `git apply --check` **REJECTED** |
| SEC‑3 | Diff creating a symlink (mode 120000) | passes `--check`, but **unreachable** — the agent only writes regular files, so `git diff` cannot emit a symlink hunk; visible in `retrieve_diff` before apply |
| SEC‑4 | Forged result envelope (wrong nonce) | ignored by `ParseAgentOutput` (test #3) |
| SEC‑5 | Out‑of‑tree edit path in agent | rejected by `is_relative_to` (pytest #5) |

## 7. Traceability (requirement → evidence)

| Requirement | Evidence |
|-------------|----------|
| FR‑1 (tools) | IV‑1, IV‑12, IV‑15 |
| FR‑3 (read‑only + explicit apply) | IV‑7, IV‑8 |
| FR‑4/5 (providers, broker) | IV‑9, unit #5–8 |
| FR‑6 (SEARCH/REPLACE + retry) | pytest #1–4, IV‑13 |
| FR‑8 (diff + apply) | IV‑5, IV‑6, IV‑8, SEC‑1/2 |
| FR‑10 (cancel) | IV‑11 |
| FR‑11 (image build) | IV‑3 |
| NFR‑1 (hardening) | IV‑10 |
| NFR‑2 (stdout purity, nonce, CRLF) | IV‑1, IV‑6, unit #2/#3 |
| NFR‑3 (timeouts/robustness) | IV‑11 |
| NFR‑7 (testability) | §4 (15 automated tests) |

## 8. Results summary

- **Automated:** 10/10 xUnit + 5/5 pytest passing.
- **Integration:** IV‑1…IV‑16 all pass; no resource leaks.
- **Security:** SEC‑1…SEC‑5 behave as designed; the one "critical" finding (`git apply` RCE) was
  empirically refuted on git 2.53.

## 9. Known gaps / not automated

- Integration verifications (IV/SEC) are run manually, not yet in CI; they require a live Docker
  daemon (and IV‑9 a live subscription).
- The container build and demux path is exercised by integration tests, not unit tests.
- The broker `claude -p` **timeout** is build‑verified but not runtime‑exercised (reproducing a
  5‑minute CLI hang is impractical).
- Real‑model **edit quality** is not asserted (mock‑driven where deterministic output is needed).
- No load/concurrency test of many simultaneous spawns beyond design review.
