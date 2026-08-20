# Decisions

## 2026-08-19 — Dependabot sweep: majors merged

**Status:** accepted (awareness-only stub per saved sweep policy)
**Decision:** merge on green CI; watch items below.
- **xunit.runner.visualstudio 3.1.5 → 4.0.0** (#49): runner major. Failure mode is silent zero-discovery, not a red build — confirm the CI test log still reports a non-zero "Passed:" count.
- **node 22-alpine → 26-alpine** (/Agent Dockerfile, #40): Node 26 LTS line; any native module rebuild issues surface in the image build.
- **python 3.12-slim → 3.14-slim** (/Agent Dockerfile, #38): niche deps may lag 3.14 wheels and fall back to compiling; image build is the check.
- **openai >=2.53 → >=3.1** (/Agent, #46, merged after rebase): client v3 reworks responses/streaming surfaces — verify Agent call sites on next touch.

**Why no review:** sweep policy — CI gates, deploy watch, revert cheap.
