# pengdows.threading

[![NuGet](https://img.shields.io/nuget/v/pengdows.threading.svg)](https://www.nuget.org/packages/pengdows.threading)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
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
var converge = new ConvergeWait(
    maxConcurrency: 8,
    adaptiveConfig: AdaptiveConfig.Default,
    cancellationToken: CancellationToken.None,
    logger: logger
);

converge.OnQueued += count => Console.WriteLine($"Queued: {count}");
converge.OnCompleted += count => Console.WriteLine($"Completed: {count}");
converge.OnError += ex => Console.WriteLine($"Error: {ex.Message}");

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

## API Reference

### `ConvergeWait`

- `Queue(Func<Task>)`: Adds a task for execution.
- `WaitForAllAsync()`: Waits for all queued tasks to complete.
- `SetRetryPolicy(RetryPolicy)`: Sets retry logic.
- `DisposeAsync()`: Cancels monitoring and releases resources.

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
RetryPolicy.None;
```

## Events

- `OnQueued`: Triggered when a task is queued.
- `OnCompleted`: Triggered when a task completes successfully.
- `OnError`: Triggered when a task permanently fails.
- `OnConcurrencyChanged`: Triggered when adaptive concurrency adjusts.

## License

MIT License
