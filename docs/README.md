# pengdows.threading

[![NuGet](https://img.shields.io/nuget/v/pengdows.threading.svg)](https://www.nuget.org/packages/pengdows.threading)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../LICENSE)
[![Build](https://github.com/pengdows/threading/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/pengdows/threading/actions)

A high-performance .NET 8 task coordination utility that limits concurrency, supports adaptive CPU-based scaling, and includes built-in retry logic.

## Features

- **Controlled Concurrency**: Queue tasks with a concurrency cap.
- **Adaptive Throttling**: Automatically adjusts concurrency based on CPU usage.
- **Retry Support**: Configurable retry policies with delay and attempt limits.
- **Safe Shutdown**: Gracefully cancels and disposes resources.
- **Thread-Safe Events**: Track queue status, failures, and concurrency shifts.

## Installation

```bash
dotnet add package pengdows.threading
```

_Note: Requires .NET 8._

## Usage

```csharp
// Note: maxConcurrency must not exceed adaptiveConfig.MaxConcurrency (enforced by the
// constructor). AdaptiveConfig.Default.MaxConcurrency scales with Environment.ProcessorCount,
// so pick an explicit, machine-independent MaxConcurrency if maxConcurrency is fixed like this.
var converge = new ConvergeWait(
    maxConcurrency: 2,
    adaptiveConfig: new AdaptiveConfig(MinConcurrency: 2, MaxConcurrency: 8),
    cancellationToken: CancellationToken.None,
    logger: logger
);

converge.OnQueued += count => Console.WriteLine($"Queued: {count}");
converge.OnCompleted += count => Console.WriteLine($"Completed: {count}");
converge.OnError += ex => Console.WriteLine($"Error: {ex.Message}");
converge.OnProgress += percent => Console.WriteLine($"Progress: {percent:F1}%");

converge.SetRetryPolicy(RetryPolicy.Retry(3, TimeSpan.FromMilliseconds(250)));

for (int i = 0; i < 100; i++)
{
    converge.Queue(async () =>
    {
        // Your task logic here
        await Task.Delay(100);
    });
}

await converge.WaitForAllAsync();
await converge.DisposeAsync();
```

`QueueAsync` is the backpressure-aware alternative to `Queue`: the enqueue call itself awaits a
free concurrency slot, instead of returning immediately and throttling only when the work runs.
Use it when an unbounded producer could otherwise queue faster than the work can drain:

```csharp
await converge.QueueAsync(async () =>
{
    await Task.Delay(100);
}, cancellationToken);
```

## API Reference

### `ConvergeWait`

- `Queue(Func<Task>)`: Enqueues a task; returns immediately, throttled when the work runs.
- `QueueAsync(Func<Task>, CancellationToken)`: Enqueues a task; awaits a free concurrency slot before returning (real backpressure on the caller).
- `WaitForAllAsync()`: Waits for all queued tasks - including any queued concurrently while waiting - to converge.
- `SetRetryPolicy(RetryPolicy)`: Sets retry logic.
- `DisposeAsync()`: Drains all queued/in-flight work, then releases resources. Safe to call concurrently or more than once.
- `TotalQueued` / `TotalCompleted` / `TotalFailed`: Running counts.
- `HasFailures`: `true` if any task has permanently failed.
- `Exceptions`: A point-in-time snapshot of exceptions from permanently failed tasks.
- `ProgressPercent`: `(TotalCompleted + TotalFailed) / TotalQueued * 100`.

### `AdaptiveConfig`

Configure adaptive concurrency behavior:

```csharp
new AdaptiveConfig(
    TargetCpuUsagePercent: 70f,
    MinConcurrency: 2,
    MaxConcurrency: 32,
    SamplingInterval: TimeSpan.FromSeconds(2)
);
```

### `RetryPolicy`

Control retry behavior:

```csharp
RetryPolicy.Retry(5, TimeSpan.FromMilliseconds(200));
RetryPolicy.Exponential(maxRetries: 5, initialDelay: TimeSpan.FromMilliseconds(100),
    maxDelay: TimeSpan.FromSeconds(5), multiplier: 2.0, useJitter: true);
RetryPolicy.None;
```

`MaxRetries`, `RetryDelay`, `MaxDelay`, and `BackoffMultiplier` are all validated at
construction (e.g. `BackoffMultiplier` must be positive) - invalid values throw immediately
rather than failing later inside a retry loop.

`useJitter: true` adds a random amount (0 to one base delay) on top of each computed
exponential-backoff delay, so many callers retrying at once don't all retry in lockstep against
the same downstream resource. `OutOfMemoryException` and `StackOverflowException` are never
retried regardless of policy - they're not the kind of failure a retry can recover from.

## Events

- `OnQueued`: Triggered when a task is queued.
- `OnCompleted`: Triggered when a task completes successfully.
- `OnError`: Triggered when a task permanently fails.
- `OnConcurrencyChanged`: Triggered when adaptive concurrency adjusts.
- `OnProgress`: Triggered whenever `ProgressPercent` changes.

## History

`ConvergeWait` traces back to a 2010 CodeProject article of the same name by the same author,
which first identified the gap this class fills: .NET has plenty of ways to *limit* concurrent
threads (Semaphore, Mutex, etc.) but nothing to *wait* for a set of them to finish. The original
article is archived in full at
[`docs/ConvergeWait-Original-CodeProject-Article.md`](ConvergeWait-Original-CodeProject-Article.md).

## License

MIT License
