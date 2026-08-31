# C# Visualizer

Write C#, press Run, and step through it: the stack, the heap, pointer arrows, the control-flow
graph with an execution heat map, where every value came from, and the whole call tree.

Optionally, plain-English narration of any step and a post-mortem when a program crashes.

## What makes it work

**The interpreter never executes your code.** It walks Roslyn's `IOperation` tree — the bound,
fully-resolved semantic tree, after overload resolution, generic substitution and
struct-vs-class classification have already happened — and evaluates it with its own value,
heap and frame model.

That one decision settles four problems at once:

| Problem | Why it goes away |
| --- | --- |
| Reflection cannot read local variables | Locals live in our own frames, so they are simply readable |
| Sandbox escape | No submitted code ever runs. `File.Delete` is an unimplemented method, not a threat |
| Free hosting forbids Docker socket access | The API is an ordinary stateless container needing no privileges |
| Statement-level granularity only | We own the evaluation loop, so finer stepping is available |
| Runaway loops and memory bombs | A step budget and a heap cap, enforced in-process and deterministically |

The interpreter drives an **explicit continuation stack** rather than host recursion. Deep user
recursion therefore reports a clean `stackDepth` limit instead of crashing the server with a
stack overflow.

## Correctness

Two assets carry the quality of this project, and both run in CI:

- **`CsViz.Differential`** compiles every corpus program with Roslyn, runs it on the *real*
  .NET runtime, and diffs its stdout against the interpreter's. Real C# is the expectation, so
  it can never go stale, and it catches the dangerous class of bug — the interpreter running
  happily and producing the wrong number. It found thirteen of those.
- **`CsViz.Golden`** pins the trace JSON for a curated set of programs, protecting the wire
  format the frontend replays. A change that keeps the arithmetic right but stops emitting
  `setField` deltas passes every differential test and breaks the memory view; this catches it.

Plus determinism tests (the same program must produce byte-identical JSON twice) and limit
tests (every resource ceiling must actually fire).

```bash
dotnet test cs-visualizer/backend/CsViz.slnx
```

## Running it

Backend:

```bash
dotnet run --project cs-visualizer/backend/src/CsViz.Api
```

Frontend:

```bash
npm install --prefix cs-visualizer/frontend && npm run dev --prefix cs-visualizer/frontend
```

The dev server proxies `/api` to `http://127.0.0.1:5069`; set `CSVIZ_API` to point elsewhere.

## Configuration

Everything is optional. With none of it set, the visualizer works fully — only the AI panel
reports itself unavailable.

| Variable | Default | Purpose |
| --- | --- | --- |
| `MISTRAL_API_KEY` | *(unset)* | Enables narration and crash explanation. Server-side only; never reaches the browser |
| `CSVIZ_AI_ENABLED` | `true` | Set `false` to disable AI without removing the key |
| `CSVIZ_AI_NARRATION_MODEL` | `mistral-small-latest` | |
| `CSVIZ_AI_EXPLAIN_MODEL` | `mistral-medium-latest` | `mistral-large-latest` is not available on the free tier |
| `CSVIZ_AI_DAILY_BUDGET` | `1000` | Upstream calls per day, counted durably so a restart cannot reset it |
| `CSVIZ_AI_CACHE` | `csviz-ai-cache.db` | SQLite narration cache |
| `CSVIZ_DEBUG` | *(unset)* | `1` includes interpreter stack traces in `CSVIZ0001` diagnostics. Never enable in production |

## Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /healthz` | Liveness |
| `POST /api/trace` | `{"source": "...", "stdin": "..."}` or a raw C# body → a trace |
| `GET /api/trace/{hash}` | A previously traced program, which is what makes permalinks shareable |
| `GET /api/support` | The supported-language manifest the UI renders |
| `GET /api/ai/status` | Whether AI is available, and why not if it is not |
| `POST /api/ai/narrate` | `{"sourceHash", "stepIndex"}` → narration for one step |
| `POST /api/ai/explain` | `{"sourceHash"}` → a crash post-mortem with validated step citations |

The AI endpoints take a **source hash, not a trace**. The server re-derives the trace from its
own cache, so a caller cannot put text of its own choosing in front of the model while claiming
the interpreter produced it.

## Limits

Every ceiling returns a clean `limit_exceeded` trace showing everything up to the cutoff — a
student's infinite loop should teach them something, not return a 500.

| Limit | Value |
| --- | --- |
| Recorded steps | 15,000 |
| Interpreter operations | 500,000 |
| Heap objects | 5,000 |
| Frame depth | 2,000 |
| Output | 64 KB |
| Source | 100 KB |
| Wall clock | 3 s |

## Deploying

```bash
docker build -f cs-visualizer/docker/Dockerfile -t csviz-api cs-visualizer
docker run -p 8080:8080 -e MISTRAL_API_KEY=... -v csviz-data:/app/data csviz-api
```

The image runs unprivileged and needs no Docker socket, no `--privileged`, and no custom
seccomp profile — which is exactly what makes a free host viable. The frontend is a static
bundle (`npm run build`) deployable to any static host.

## Scope

The interpreter covers a real but bounded subset of C#. `GET /api/support` returns the current
manifest, and the UI shows it under **Diagnostics → What C# is supported?** — the boundary is
published rather than discovered by hitting it.

Notably absent: `async`/`await`, iterators, lambdas and LINQ, user-declared generic types,
multi-dimensional arrays, and pattern matching beyond constant cases. Anything unsupported
produces a diagnostic pointing at the exact source span. **A visualizer that quietly renders a
wrong memory diagram is worse than one that declines to run**, and that rule decided a lot of
the design.
