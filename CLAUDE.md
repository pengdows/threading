# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Mandatory Workflow And Reviews

- TDD is mandatory for every behavior change, bug fix, regression fix, and public-contract change.
- Start by writing or updating an automated test that fails for the intended reason before changing implementation.
- Do not start implementation until the test is red; after implementation, rerun the relevant automated tests and do not consider the work complete until they pass with no skipped tests introduced.
- If automated coverage is genuinely not possible, say so explicitly and document the verification gap.
- All reviews are done against [REVIEW_POLICY.md](./REVIEW_POLICY.md).
- Review output, merge guidance, blocker/major/minor classification, required evidence, and minimal patch guidance must follow [REVIEW_POLICY.md](./REVIEW_POLICY.md).
- If instructions overlap, follow the more stringent requirement. If this file conflicts with [REVIEW_POLICY.md](./REVIEW_POLICY.md) on review behavior, follow [REVIEW_POLICY.md](./REVIEW_POLICY.md).

## Build and Test Commands

```bash
# Build
dotnet build threading.sln

# Run all tests
dotnet test threading.Tests/threading.Tests.csproj

# Run a single test
dotnet test threading.Tests/threading.Tests.csproj --filter "FullyQualifiedName~Queue_Single_Task_Completes_Successfully"

# Build release
dotnet build --configuration Release
```

## Architecture

pengdows.threading is a .NET 8 task coordination library that provides controlled concurrency with optional adaptive CPU-based scaling and built-in retry logic.

### Core Components

- **`ConvergeWait`** (`threading/ConvergeWait.cs`): The main class that queues async tasks with a concurrency limit using `SemaphoreSlim`. Unlike raw `Task.WhenAll`, it doesn't start all tasks immediately—it limits in-flight work and queues the rest.

- **`IConvergeWait`** (`threading/IConvergeWait.cs`): Interface for `ConvergeWait`, enabling dependency injection and testing.

- **`AdaptiveConfig`** (`threading/AdaptiveConfig.cs`): Record that configures adaptive concurrency behavior—target CPU percentage, min/max concurrency bounds, and sampling interval.

- **`RetryPolicy`** / **`RetryMode`** (`threading/RetryPolicy.cs`, `threading/RetryMode.cs`): Configures retry behavior. Use `EffectiveRetryDelay` property to get the actual delay (defaults to 100ms when not explicitly set).

### How Adaptive Throttling Works

1. A background monitor task samples CPU usage via `Process.GetCurrentProcess().TotalProcessorTime`
2. When CPU exceeds target + 5%, concurrency scales DOWN by recording a *reservation debt*
   (`_pendingReservationDebt`) rather than synchronously grabbing a free semaphore permit.
   A synchronous `Wait(0)` against a semaphore with a backlog of already-registered async
   waiters almost always loses that race (`Release()` favors existing waiters), so scale-down
   instead lets the next `-delta` task completions each withhold their own permit
   (`ReleaseOrReserve()`) instead of releasing it back to the pool — deterministic, just not
   instantaneous.
3. When CPU drops below target - 5%, concurrency scales UP by first canceling any outstanding
   reservation debt, then releasing already-withheld slots (`_reservedSlots`), then injecting
   new capacity if still short.
4. The semaphore is initialized with `AdaptiveConfig.MaxConcurrency` to allow scaling up beyond initial value
5. `DisposeAsync` clears any outstanding reservation debt before draining queued work, so a
   task completing during shutdown can never stall waiting on a permit that's being withheld
   to honor a throttling target that no longer matters.

### Key Design Patterns

- Tasks are queued via `Queue(Func<Task>)` (fire-and-forget enqueue; throttling happens when
  the work actually executes) or `QueueAsync(Func<Task>, CancellationToken)` (the enqueue call
  itself awaits a free concurrency slot, i.e. real backpressure on the caller)
- Events (`OnQueued`, `OnCompleted`, `OnError`, `OnConcurrencyChanged`, `OnProgress`) provide observability
- Exceptions are captured in a thread-safe list and accessible via `Exceptions` property
- Cancellation token is passed to `SemaphoreSlim.WaitAsync` so pending tasks can be cancelled
- `WaitForAllAsync()` loops snapshot-and-await until the tracked task list is genuinely empty,
  so work queued by another thread mid-wait is still included before the caller resumes
- `DisposeAsync()` is safe to call concurrently from multiple callers - only the first caller
  observes the outcome (including any exception); all others wait for the same completion and
  return normally

### Why ConvergeWait vs Task.WhenAll

`Task.WhenAll` starts all tasks immediately ("fire everything, then wait"), which can exhaust CPU/memory with many heavy work items. `ConvergeWait` limits concurrent execution, queues the rest, and optionally adapts that limit based on CPU saturation.

## Project Structure

```
threading/           # Main library (pengdows.threading NuGet package)
threading.Tests/     # xUnit tests
```

## Testing

- All tests run (no skipped tests)
- `CpuTestHelper.BurnCpu()` creates synthetic CPU load for testing adaptive concurrency
- Tests use `NullLogger<ConvergeWait>.Instance` for logging in test scenarios
- Adaptive tests use aggressive thresholds (low target CPU for scale-down, high for scale-up) to ensure reliable triggering
