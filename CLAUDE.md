# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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
2. When CPU exceeds target + 5%, concurrency scales DOWN by acquiring semaphore slots and holding them (`_reservedSlots`)
3. When CPU drops below target - 5%, concurrency scales UP by releasing reserved slots
4. The semaphore is initialized with `AdaptiveConfig.MaxConcurrency` to allow scaling up beyond initial value

### Key Design Patterns

- Tasks are queued via `Queue(Func<Task>)` and execute when a semaphore slot is available
- Events (`OnQueued`, `OnCompleted`, `OnError`, `OnConcurrencyChanged`) provide observability
- Exceptions are captured in a thread-safe list and accessible via `Exceptions` property
- Cancellation token is passed to `SemaphoreSlim.WaitAsync` so pending tasks can be cancelled

### Why ConvergeWait vs Task.WhenAll

`Task.WhenAll` starts all tasks immediately ("fire everything, then wait"), which can exhaust CPU/memory with many heavy work items. `ConvergeWait` limits concurrent execution, queues the rest, and optionally adapts that limit based on CPU saturation.

## Project Structure

```
threading/           # Main library (pengdows.threading NuGet package)
threading.Tests/     # xUnit tests
```

## Testing

- All 21 tests run (no skipped tests)
- `CpuTestHelper.BurnCpu()` creates synthetic CPU load for testing adaptive concurrency
- Tests use `NullLogger<ConvergeWait>.Instance` for logging in test scenarios
- Adaptive tests use aggressive thresholds (low target CPU for scale-down, high for scale-up) to ensure reliable triggering
