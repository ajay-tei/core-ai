# Diva Agent API Load Tester

A dependency-free .NET console tool that load-tests the Diva agent execution API
(`POST /api/agents/{id}/invoke` and `POST /api/agents/{id}/invoke/stream`) to answer: **can the
backend handle N concurrent agentic requests, including tool-heavy MCP calls, without falling over?**

It talks to the API purely over HTTP/SSE — exactly like the admin portal or any external client — so
it can point at localhost, a docker-compose stack, or a real staging/production deployment.

See [`docs/scalability-load-review.md`](../../docs/scalability-load-review.md) for the architecture
review this tool was built to validate.

## Build & run

```powershell
dotnet run --project tools/LoadTest -- --help
```

No other project needs to build first — this tool has zero references to the rest of the solution.

## Convenience wrapper: `Run-LoadTest.ps1`

For repeated manual runs, `Run-LoadTest.ps1` wraps the `dotnet run` invocation with named parameters
and masks the API key in its echoed command line:

```powershell
$env:DIVA_LOAD_TEST_KEY = "tei_..."   # set once per shell session — never pass the raw key as a literal
./tools/LoadTest/Run-LoadTest.ps1 -AgentId <id> -Scenario burst -Users 5
./tools/LoadTest/Run-LoadTest.ps1 -AgentId <id> -Scenario ramp -RampMaxUsers 100 -Duration 300 -OutputCsv ramp.csv
```

Run `Get-Help ./tools/LoadTest/Run-LoadTest.ps1 -Full` for all parameters.

## In-progress status + end-of-run summary

Every scenario prints a status line every 5 seconds while it runs (`[t=25s] 1/2 done  ok=1  failed=0
in-flight=1  users=2`), since each request can take 20-70+ seconds against a real LLM — this
confirms the run is alive rather than hung. A "Test run complete — compiling summary..." line marks
the transition into the final report (latency percentiles, error breakdown, tool-call stats).


## Quick examples

```powershell
# 50 concurrent users, 3 requests each, against a locally auto-discovered agent (buffered /invoke)
dotnet run --project tools/LoadTest -- --scenario burst --users 50 --requests-per-user 3

# Streaming, tool-heavy query, 100 concurrent users for 2 minutes (the scenario most relevant to
# "multiple complex MCP calls" — reports time-to-first-tool-call and tool-call counts per request)
dotnet run --project tools/LoadTest -- --mode stream --scenario soak --users 100 --duration 120 `
    --agent-id <your-agent-id> --query "Check availability at all 5 courses and summarize which have openings this weekend."

# Find the breaking point: ramp from 0 to 300 users, +20 every 15s
dotnet run --project tools/LoadTest -- --scenario ramp --ramp-max-users 300 --ramp-step-users 20 --ramp-step-seconds 15 --duration 300 --output ramp-results.csv

# Against a deployed instance with auth enabled
dotnet run --project tools/LoadTest -- --base-url https://ai.example.com --api-key diva_xxx... --scenario burst --users 200 --requests-per-user 2
```

## Scenarios

| Scenario | Behavior | Use it to answer |
|---|---|---|
| `burst` (default) | `--users` virtual users all start at once, each fires `--requests-per-user` requests back-to-back, then the run ends. | "What happens if 200 users all hit the API in the same instant?" (e.g. everyone opening the app after a company-wide announcement) |
| `soak` | Fixed `--users` concurrency, looping continuously for `--duration` seconds. | "Does latency/error rate degrade over a sustained period?" (DB lock contention, cache TTL churn, memory growth, connection pool exhaustion) |
| `ramp` | Starts at 0 and adds `--ramp-step-users` every `--ramp-step-seconds`, up to `--ramp-max-users` or `--duration`. Prints a 10-second time-series table. | "Where exactly does it start falling over?" — read the time-series table for the concurrency level where p95/p99 latency or error rate spikes. |

## Modes

- `--mode invoke` (default) — buffered `POST /invoke`, measures total request latency only.
- `--mode stream` — `POST /invoke/stream` (SSE), parses the event timeline and additionally reports:
  - time to first byte / first token / first tool call
  - tool calls per request, ReAct iterations per request
  - verification-blocked count

Use `--mode stream` with a query that forces multi-step tool use (reference an agent with MCP
bindings) to specifically exercise "multiple complex MCP calls" concurrency — this is the scenario
that stresses `McpClientCache`, `MaxParallelToolCalls`, and the MCP servers themselves the hardest.

## Session behavior

By default every request uses a brand-new session (no `--shared-session` flag) — the worst case for
session-creation and MCP-cache load, and the most realistic model of "hundreds of distinct users."
Pass `--shared-session` to instead have each virtual user reuse one session across its requests
(simulates a smaller number of users each having a longer multi-turn conversation).

## Interpreting results

- **Throughput** (`successful req/s`) — compare against your target user count × expected request rate.
- **Latency percentiles** — p50 is the typical case; p95/p99 show what your slowest users experience.
  For SSE, `TimeToFirstTokenMs` matters most for perceived responsiveness, not total duration (agent
  runs with continuations can legitimately take minutes).
- **Error breakdown** — HTTP 429s indicate you've hit a rate limit (yours or the LLM provider's) at
  this concurrency; connection errors / timeouts indicate the server (or its DB) is saturated.
- **Ramp time-series** — the concurrency level (`Users` column) where `p95`/`p99` or `Errs` visibly
  jumps is your current capacity ceiling. Use it to size Kestrel limits / rate-limiter thresholds
  (see the P0 recommendations in `docs/scalability-load-review.md`) just above that number so the
  system rejects excess load with 429s instead of degrading for everyone.

## Options reference

Run `dotnet run --project tools/LoadTest -- --help` for the full, current list.
