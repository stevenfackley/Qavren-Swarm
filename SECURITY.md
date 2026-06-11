# Security Policy

## Context & trust model

Qavren Swarm is a **single‑user, local developer tool**. It runs on your workstation, talks to
your IDE harness over stdio, and drives your local Docker daemon. The MCP client (the IDE harness)
is **trusted** — it is you. The components treated as **untrusted** are:

1. **Model output.** The LLM (local or remote) is prompted with your task + repository and may be
   prompt‑injected by hostile content in the workspace. Its edits are never trusted blindly.
2. **Workspace content.** The repository you point a job at may contain hostile files (malicious
   `package.json` scripts, odd paths, symlinks).
3. **The LAN**, to the extent the broker listens on a routable interface.

## Controls

| Threat | Control |
|--------|---------|
| LLM/workspace code escaping the sandbox | Untrusted code runs only inside an **ephemeral, non‑root** container with `--cap-drop ALL`, `no-new-privileges`, and PID/memory/CPU caps. |
| Exfiltration of host files / credentials | Workspace mounted **read‑only**; `ANTHROPIC_API_KEY` enters the container only for the `anthropic` provider; set `QAVREN_NETWORK_MODE=none` to cut egress for local runs. |
| Malicious diff overwriting host files | `apply_diff` is the **only** host‑mutating op and is explicit. `git apply` rejects `.git/`‑ and `..`‑targeting patches (verified against git 2.53). |
| Untrusted npm lifecycle scripts | `npm install --ignore-scripts`; every test subprocess has a timeout. |
| Forged result envelope via prompt injection | The stdout diff/result markers are stamped with a **per‑job nonce** the model never sees. |
| Unauthorized broker access | Bound `0.0.0.0:<port>` (required for `host.docker.internal`) and gated by a **random per‑session bearer token**, constant‑time compared. The host `claude` runs with `--disallowedTools` in an empty scratch dir. |
| Runaway / hung jobs | Per‑job wall‑clock timeout + `cancel_job`; the broker `claude -p` and host `git` calls have their own timeouts with process‑tree kill. |

## Residual risks (by design / accepted)

- The broker binds `0.0.0.0` so containers can reach it; on an untrusted LAN, harden the firewall
  to the Docker subnet or set `QAVREN_BROKER_ENABLED=false`. The bearer token is the mitigation.
- The `anthropic` provider forwards a long‑lived API key into the container env; prefer
  `QAVREN_NETWORK_MODE=none` is **not** compatible with remote providers, so scope/rotate that key
  or use `claude-code` / `openai` instead.
- Subscription usage (`claude-code`) is subject to provider rate limits; a wide swarm can exhaust
  the window. Fall back to `openai`/local per‑spawn.

## Reporting a vulnerability

This is a personal project with no support guarantees. To report an issue, open a GitHub issue
with a clear reproduction (or, for sensitive reports, a private security advisory on the repo).
There is no SLA; fixes are best‑effort.
