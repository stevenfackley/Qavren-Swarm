# Software Test Document — Qavren Swarm

| | |
|---|---|
| **System** | Qavren Swarm |
| **Status** | v1 verified |
| **Last updated** | 2026‑11‑21 |
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
| Python | 3.15 (container) / host Python for pytest |

## 4. Automated tests

### 4.1 xUnit contract tests — `tests/*.cs` (72 tests, 86 cases, all passing)

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

`JobStateStore` (`tests/JobPersistenceTests.cs`) — disk persistence:

| # | Test | Verifies |
|---|------|----------|
| 11 | `Jobs_survive_a_restart_and_inflight_jobs_are_marked_interrupted` | a Completed job + diff reload after restart; an in‑flight job becomes `Failed`/interrupted |
| 12 | `Persistence_disabled_writes_nothing` | `QAVREN_PERSIST_JOBS=false` writes no files |
| 12a | `A_write_that_fails_halfway_leaves_the_previous_job_file_whole` | a job‑file write that fails part‑way (and a temporary left by a killed process) leaves the previous file whole: the job reloads with its last good state and no temporary file remains |

`UnifiedDiffSplitter` (`tests/UnifiedDiffSplitterTests.cs`) — pure hunk splitter/recomposer:

| # | Test | Verifies |
|---|------|----------|
| 13 | `Parses_files_paths_and_hunk_counts` | a 2‑file diff splits into the right files, paths, and hunk counts |
| 14 | `Header_excludes_hunks_and_single_hunk_patch_is_header_plus_one_hunk` | file header stops before `@@`; a single‑hunk patch = header + just that hunk |
| 15 | `Recompose_of_all_hunks_round_trips_the_original_bytes` | selecting every hunk reproduces the input byte‑for‑byte |
| 16 | `Recompose_of_a_subset_keeps_only_selected_hunks_and_drops_empty_files` | a subset keeps only chosen hunks; files with none are omitted |
| 17 | `Preserves_CR_bytes_in_hunk_bodies` | CRLF survives split + recompose (sub‑patch still applies to a CRLF file) |
| 18 | `New_file_path_uses_b_side_deletion_uses_a_side` | path resolution handles `/dev/null` create/delete sides |
| 19 | `Empty_or_garbage_input_yields_no_files` | non‑diff input → empty list (caller falls back to whole‑patch) |

`apply_diff` recovery (`tests/ApplyDiffTests.cs`) — real‑`git` hunk‑by‑hunk apply:

| # | Test | Verifies |
|---|------|----------|
| 20 | `Whole_patch_applies_cleanly_when_workspace_matches` | unchanged fast path: whole patch applies, `partial:false` |
| 21 | `Rejected_multi_hunk_diff_applies_good_hunks_and_exports_the_rest` | drifted diff → good hunk applied, bad hunk untouched + exported in `rejectedDiff` |
| 22 | `A_malicious_git_targeting_hunk_is_rejected_not_written_even_in_partial_mode` | **SECURITY:** a `.git/hooks/` hunk is refused by `git apply`, never written, exported instead; legit hunk still lands |
| 23 | `Strict_mode_applies_nothing_when_the_whole_patch_is_rejected` | `allowPartial:false` keeps the all‑or‑nothing contract (no file touched) |

`JobRecovery` / pause recovery (`tests/PauseRecoveryTests.cs`):

| # | Test | Verifies |
|---|------|----------|
| 24 | `User_cancel_classifies_as_failed` | `cancel_job` → terminal `Failed` |
| 25 | `Bare_timeout_classifies_as_paused` | wall‑clock timeout (hang) → recoverable `Paused` |
| 26 | `Pause_expiry_respects_the_grace_window_and_only_applies_to_paused_jobs` | `IsPauseExpired` honours the grace window and ignores non‑Paused jobs |
| 27 | `Resume_creates_a_fresh_job_with_the_same_spawn_params_and_supersedes_the_old_one` | `resume_job` re‑spawns with identical params; old job → `Failed` (superseded) |
| 28 | `Resume_rejects_a_non_paused_job` | resuming a non‑Paused job is an `McpException` |
| 29 | `Expired_paused_job_is_reaped_to_failed_when_its_status_is_read` | a stale Paused job is lazily reaped to `Failed` on `check_sandbox_status` |

Job lifecycle transitions (`tests/JobLifecycleTests.cs`); rows marked *theory* run once per status:

| # | Test | Verifies |
|---|------|----------|
| 30 | `Cancel_of_a_finished_job_reports_already_finished_and_changes_nothing` | *theory* (Completed, Failed): `cancel_job` on a finished job returns "job already finished" and writes nothing |
| 31 | `Cancel_of_a_paused_job_discards_it_as_failed_with_a_finish_time` | discarding a Paused job → `Failed`, "paused job discarded", `FinishedUtc` set |
| 32 | `Cancel_of_a_pending_job_flags_a_user_cancel_and_leaves_the_status_to_the_run` | cancel of a live job only sets `UserCancelled`; the run records the outcome |
| 33 | `Cancel_while_the_replacement_has_not_started_is_refused_and_keeps_the_superseded_record` | a cancel between the `resume_job` claim and the replacement's start is an `McpException` naming the replacement, and keeps "resumed as <id>" |
| 34 | `Resume_finishes_the_old_job_and_a_second_resume_of_it_is_refused` | the old job gets `FinishedUtc`; resuming it again is an `McpException` and creates nothing |
| 35 | `Unexpired_paused_job_stays_resumable_when_its_status_is_read` | a Paused job inside its grace window is not reaped |
| 36 | `Expired_paused_job_is_reaped_with_a_reason_and_finish_time_by_list_jobs` | `list_jobs` reaps a stale Paused job with the expiry reason and `FinishedUtc` |
| 37 | `Expired_paused_job_cannot_be_resumed` | `resume_job` on a stale Paused job reaps it and refuses |
| 38 | `Exit_code_is_reported_only_for_finished_jobs` | *theory* (all five statuses): `exitCode` only for Completed/Failed |
| 39 | `Eviction_removes_only_finished_jobs_oldest_first` | at the 200‑job cap only Completed/Failed are evicted, oldest first; Paused/Running stay |
| 40 | `Restart_stamps_a_finish_time_on_interrupted_jobs_and_keeps_paused_jobs_paused` | restart: in‑flight → `Failed` with `FinishedUtc`; Paused reloads Paused |
| 41 | `Run_that_throws_is_recorded_as_failed_with_the_exception_and_a_finish_time` | a run that throws → `Failed`, "<Type>: <message>", `FinishedUtc` |
| 42 | `IsTerminal_is_true_only_for_completed_and_failed` | *theory* (all five statuses) |
| 43 | `Fail_sets_status_reason_and_finish_time_together` | `JobState.Fail` writes all three fields |
| 44 | `TryTransition_mutates_and_persists_when_the_status_is_allowed` | allowed move mutates and re‑persists |
| 45 | `TryTransition_refuses_and_leaves_the_job_alone_when_the_status_has_moved` | disallowed move returns `false` and never runs the mutation |
| 46 | `TryTransition_on_an_unknown_job_returns_false_without_calling_the_mutation` | unknown id → `false` |
| 47 | `Two_resumes_that_both_passed_the_guard_create_only_one_replacement` | the second of two resumes that both read Paused is refused; one replacement job |
| 48 | `Concurrent_resumes_of_one_paused_job_create_exactly_one_replacement` | eight threads resume one Paused job; exactly one wins |

`resume_job` launch outcomes (`tests/ResumeLaunchTests.cs`), against a stand‑in Docker engine whose
steps a test holds open or fails; rows marked *theory* run once per case:

| # | Test | Verifies |
|---|------|----------|
| 49 | `Replacement_that_fails_before_its_container_starts_hands_the_old_job_back_as_paused` | *theory* (image lookup, create, start throws): the old job is `Paused` again with the failed relaunch as its reason and `PausedUtc` at the hand‑back, no `FinishedUtc`, and reads `resumable` |
| 50 | `Old_job_that_was_handed_back_resumes_once_docker_is_back` | a handed‑back job resumes normally; a further resume of it is refused |
| 51 | `Concurrent_resumes_of_a_handed_back_job_create_exactly_one_replacement` | eight threads resume a handed‑back job; exactly one wins |
| 52 | `Hand_back_is_written_to_disk` | the handed‑back job reloads as `Paused` after a restart, with its reason, `PausedUtc` and `HandBacks` |
| 53 | `Cancel_after_the_hand_back_discards_the_old_job_for_good` | a handed‑back job is discarded like any Paused job and cannot be resumed |
| 54 | `Replacement_that_starts_keeps_the_old_job_superseded` | the old job stays `Failed`, "resumed as <id>", while the replacement runs and after it completes |
| 55 | `Replacement_that_fails_after_its_container_started_keeps_the_old_job_superseded` | *theory* (non‑zero exit, wait throws): no hand‑back once the container has started |
| 56 | `Second_resume_while_the_replacement_is_starting_is_refused_and_creates_nothing` | a second `resume_job` during the start‑up window is an `McpException` and creates nothing |
| 57 | `Cancel_of_the_old_job_while_the_replacement_is_starting_is_told_it_is_being_resumed` | *theory* (launch then fails, launch then succeeds): the cancel is refused and changes nothing; trying again afterwards discards the handed‑back job, or reads "job already finished" once the replacement has run |
| 58 | `Cancel_of_the_replacement_while_it_is_starting_keeps_the_old_job_superseded` | *theory* (Docker honours the cancel, Docker throws something else): a cancelled replacement is not a failed launch; no hand‑back, and a cancel of the old job then reads "job already finished" |
| 59 | `Timeout_while_the_replacement_is_starting_pauses_the_replacement_and_keeps_the_old_job_superseded` | a start‑up timeout pauses the replacement only, so there is one resumable job, not two; a cancel of the old job then reads "job already finished" |
| 60 | `Hand_back_just_before_the_deadline_restarts_the_grace_window` | a launch that fails past the old deadline hands the job back unreaped; it is reaped one full window after the hand‑back, with the failed relaunch in its reason |
| 61 | `Each_failed_resume_restarts_the_window_and_only_another_resume_extends_it` | five failed resumes each just before the deadline restart the window every time; without another resume the job is reaped one window after the last hand‑back |
| 62 | `Paused_job_that_was_never_resumed_is_reaped_one_window_after_it_paused_as_before` | a job never handed back is reaped exactly one window after it paused, with the old reason |
| 63 | `Cancel_of_the_old_job_once_the_replacement_has_started_is_already_finished_for_good` | once the replacement's container has started (and while it still runs) a cancel of the old job reads "job already finished", and it stays finished |
| 64 | `Cancel_racing_a_resume_is_never_told_finished_and_then_undone` | 300 rounds of `cancel_job` and `resume_job` released together on one Paused job: an acknowledged cancel always ends `Failed` ("paused job discarded"); a refused one names the replacement, and the job comes back `Paused` |
| 65 | `Status_reads_during_a_hand_back_never_reap_the_job_whose_window_it_restarts` | 2000 rounds of a launch that fails after the old deadline while a thread polls `check_sandbox_status`: the job always ends `Paused` |
| 66 | `Cancel_of_the_replacement_before_its_launch_has_a_cancellation_handle_stops_it_before_it_starts` | a cancel between `PrepareResume` and the launch makes no Docker call; the replacement ends "cancelled by user" and the old job is finished for good |
| 67 | `Run_that_throws_past_its_own_handling_ends_the_replacement_and_settles_the_old_job` | a launch that fails before start and whose failure log throws: the replacement ends `Failed` ("run ended unexpectedly"), the old job is finished for good, not "being resumed" |
| 68 | `After_a_restart_a_superseded_job_reads_finished_and_names_its_replacement` (in `JobLifecycleTests.cs`) | `ResumedAs` survives a restart: a cancel of the superseded job reads "job already finished; resumed as <id>" |
| 69 | `UpdateIf_on_an_unknown_job_returns_false_without_calling_the_decision` (in `JobLifecycleTests.cs`) | unknown id → `false`, decision never run |
| 70 | `UpdateIf_persists_only_when_the_decision_says_it_changed_the_job` (in `JobLifecycleTests.cs`) | a `false` decision leaves the job file as it was; a `true` one rewrites it |
| 71 | `Cancel_of_an_evicted_job_is_an_unknown_job` (in `JobLifecycleTests.cs`) | `cancel_job`'s only lookup is the locked one: an evicted id is "unknown jobId", never an answer from a stale read |

Where a row above says a cancel of the old job reads "job already finished", the note is
"job already finished; resumed as <id>".

### 4.2 pytest — `tests/test_agent.py` (6, all passing)

| # | Test | Verifies |
|---|------|----------|
| 1 | `test_block_re_parses_edit_and_new_file` | `BLOCK_RE` parses both an edit and an empty‑SEARCH (new file) block |
| 2 | `test_apply_edits_preserves_crlf` | editing a CRLF file keeps CRLF (no whole‑file LF flip) |
| 3 | `test_apply_edits_creates_new_file_lf` | empty SEARCH creates a new LF file |
| 4 | `test_apply_edits_fuzzy_matches_wrong_indentation` | de‑indented SEARCH (exact‑fail) is fuzzy‑matched and re‑indented to the file |
| 5 | `test_apply_edits_reports_nonmatching_hunk` | unmatched SEARCH recorded as `search not found`, nothing written |
| 6 | `test_containment_rejects_escape_and_writes_nothing` | `../evil.py` rejected as out‑of‑tree; no file created |

### 4.3 Running

```powershell
dotnet test tests/QavrenSwarm.Tests.csproj   # 86 passed
python -m pytest tests/test_agent.py          # 6 passed
```

## 5. Integration verifications (manual, against real Docker)

| ID | Scenario | Result |
|----|----------|--------|
| IV‑1 | MCP handshake over stdio; `tools/list` | 8 tools enumerate; **0 non‑JSON lines on stdout** (logs on stderr) |
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
| IV‑17 | Persistence across a real server restart | a Completed job + its diff reload (identical) in a fresh process on the same `QAVREN_JOBS_DIR`; in‑flight jobs reload as interrupted |

## 6. Negative / security tests

| ID | Attack | Result |
|----|--------|--------|
| SEC‑1 | Diff creating `.git/hooks/post-commit` | `git apply --check` **REJECTED** (`invalid path`) |
| SEC‑2 | Diff with `../escaped.txt` traversal | `git apply --check` **REJECTED** |
| SEC‑3 | Diff creating a symlink (mode 120000) | passes `--check`, but **unreachable** — the agent only writes regular files, so `git diff` cannot emit a symlink hunk; visible in `retrieve_diff` before apply |
| SEC‑4 | Forged result envelope (wrong nonce) | ignored by `ParseAgentOutput` (test #3) |
| SEC‑5 | Out‑of‑tree edit path in agent | rejected by `is_relative_to` (pytest #5) |
| SEC‑6 | `.git/`‑targeting hunk inside a **partial** `apply_diff` | refused by `git apply`, never written, exported in `rejectedDiff` (xUnit #22) — the hunk‑by‑hunk path keeps `git apply` as the sole writer |

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
| NFR‑7 (testability) | §4 (79 automated test cases) |

## 8. Results summary

- **Automated:** 86/86 xUnit + 6/6 pytest passing.
- **Integration:** IV‑1…IV‑17 all pass; no resource leaks.
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
