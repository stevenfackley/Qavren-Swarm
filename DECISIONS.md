# Decisions

## 2026-08-19 — Dependabot sweep: majors merged

**Status:** accepted (awareness-only stub per saved sweep policy)
**Decision:** merge on green CI; watch items below.
- **xunit.runner.visualstudio 3.1.5 → 4.0.0** (#49): runner major. Failure mode is silent zero-discovery, not a red build — confirm the CI test log still reports a non-zero "Passed:" count.
- **node 22-alpine → 26-alpine** (/Agent Dockerfile, #40): Node 26 LTS line; any native module rebuild issues surface in the image build.
- **python 3.12-slim → 3.14-slim** (/Agent Dockerfile, #38): niche deps may lag 3.14 wheels and fall back to compiling; image build is the check.
- **openai >=2.53 → >=3.1** (/Agent, #46, merged after rebase): client v3 reworks responses/streaming surfaces — verify Agent call sites on next touch.

**Why no review:** sweep policy — CI gates, deploy watch, revert cheap.

## 2026-11-02 — Dependabot sweep: majors merged

**Status:** accepted (awareness-only stub per saved sweep policy)
**Decision:** merge on green CI; watch items below.

### 2026-11-02 — anthropic >=1.15.0 -> >=2.3.0
- SDK 2.x sends API version `2026-10-21` by default. Breaking on that version: `temperature`/`top_p`/`top_k` return 400 on every model; a `document` block directly in an `assistant` turn returns 400; `citations` on a `user` text block returns 400. `Agent/agent.py::call_anthropic` sends none of these (model, max_tokens, system, messages, thinking only) — no call-site change.
- SDK-level: the API key is now sent as `Authorization: Bearer` (only matters behind a proxy that reads `X-Api-Key`; the agent calls api.anthropic.com directly, no `base_url`); `MessageParam` is a per-role union (plain dicts still accepted); license is Apache 2.0.
- Pre-existing, not introduced here: `thinking={"type": "enabled", "budget_tokens": N}` is deprecated on Sonnet/Opus 4.6 and rejected (400) on 4.7+ / 5.x. The default `ANTHROPIC_MODEL` is `claude-sonnet-4-6`, so it still works; moving the model forward means `{"type": "adaptive"}` + `output_config.effort`, and `THINKING_BUDGET` stops meaning anything.

- **python 3.14-slim → 3.15-slim** (/Agent Dockerfile.python, #75, squash-merged directly): the whole requirement tree resolves to cp315 manylinux_2_28 wheels (`uv pip compile --python-version 3.15 --only-binary :all:`; pydantic-core 2.50.2, jiter 0.18.0), so nothing compiles from source in the slim image. The image build at job time is still the live check; CI never builds it.

**Why no review:** sweep policy — CI gates, revert cheap.
