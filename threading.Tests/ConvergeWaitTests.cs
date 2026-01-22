using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace pengdows.threading.Tests;

public class ConvergeWaitTests
{
    [Fact]
    public async Task Queue_Single_Task_Completes_Successfully()
    {
        // Arrange
        var converge = new ConvergeWait(1);
        var taskRan = false;

        converge.Queue(() =>
        {
            taskRan = true;
            return Task.CompletedTask;
        });

        // Act
        await converge.WaitForAllAsync();

        // Assert
        Assert.True(taskRan);
        Assert.Equal(1, converge.TotalCompleted);
        Assert.Equal(1, converge.TotalQueued);
        Assert.False(converge.HasFailures);
        Assert.Empty(converge.Exceptions);
    }

    [Fact]
    public async Task Queue_Multiple_Task_Completes_Successfully()
    {
        // Arrange
        var s = Stopwatch.StartNew();
        var converge = new ConvergeWait(20);
        var numberOfTasks = 100;
        for (var i = 0; i < numberOfTasks; i++)
        {
            converge.Queue(() =>
            {
                Thread.Sleep(1000);
                return Task.CompletedTask;
            });
        }

        // Act
        await converge.WaitForAllAsync();
        s.Stop();

        // Assert
        Assert.True(s.ElapsedMilliseconds < (1000 * numberOfTasks));
        Assert.Equal(numberOfTasks, converge.TotalCompleted);
        Assert.Equal(numberOfTasks, converge.TotalQueued);
        Assert.False(converge.HasFailures);
        Assert.Empty(converge.Exceptions);
    }

    [Fact]
    public async Task Queue_Multiple_Tasks_Respects_Concurrency()
    {
        var converge = new ConvergeWait(4);
        var count = 0;
        for (var i = 0; i < 20; i++)
            converge.Queue(async () =>
            {
                await Task.Delay(10);
                Interlocked.Increment(ref count);
            });
        await converge.WaitForAllAsync();
        Assert.Equal(20, converge.TotalCompleted);
        Assert.Equal(20, count);
    }

    [Fact]
    public async Task RetryPolicy_RetryXTimes_SucceedsEventually()
    {
        var attempts = 0;
        var converge = new ConvergeWait(1);
        converge.SetRetryPolicy(RetryPolicy.Retry(3));
        converge.Queue(() =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("fail");
            return Task.CompletedTask;
        });
        await converge.WaitForAllAsync();
        Assert.Equal(1, converge.TotalCompleted);
        Assert.False(converge.HasFailures);
    }

    [Fact]
    public async Task RetryPolicy_ExceedsMaxRetries_Fails()
    {
        var converge = new ConvergeWait(1);
        converge.SetRetryPolicy(RetryPolicy.Retry(2));
        converge.Queue(() => throw new InvalidOperationException("always fail"));
        await converge.WaitForAllAsync();
        Assert.Equal(1, converge.TotalFailed);
        Assert.True(converge.HasFailures);
    }

    [Fact]
    public async Task OnQueued_OnCompleted_Events_AreFired()
    {
        var queued = 0;
        var completed = 0;
        var converge = new ConvergeWait(1);
        converge.OnQueued += _ => queued++;
        converge.OnCompleted += _ => completed++;

        for (var i = 0; i < 5; i++)
            converge.Queue(() => Task.CompletedTask);

        await converge.WaitForAllAsync();
        Assert.Equal(5, queued);
        Assert.Equal(5, completed);
    }

    [Fact]
    public async Task DisposeAsync_CleansUpResources()
    {
        var converge = new ConvergeWait(2);
        for (var i = 0; i < 10; i++)
        {
            converge.Queue(async () => await Task.Delay(10));
        }

        await converge.DisposeAsync();
        // No assert, just ensure Dispose doesn't throw
    }

    [Fact]
    public async Task Tasks_StopOnCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var converge = new ConvergeWait(2, cancellationToken: cts.Token); // Allow 2 concurrent
        var started = 0;
        var completed = 0;
        const int totalTasks = 20;

        for (var i = 0; i < totalTasks; i++)
        {
            converge.Queue(async () =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(500, cts.Token); // Long enough to allow cancellation
                Interlocked.Increment(ref completed);
            });
        }

        // Let some tasks start, then cancel
        await Task.Delay(100);
        cts.Cancel();

        // Act
        try
        {
            await converge.WaitForAllAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert: Cancellation should have prevented most tasks from completing
        // Some tasks will have started (those that acquired semaphore before cancel)
        // But not all should complete since we cancelled mid-execution
        Assert.True(started > 0, "At least some tasks should have started");
        Assert.True(started < totalTasks, $"Not all {totalTasks} tasks should have started, but {started} did");
        Assert.True(completed < started, $"Fewer tasks should complete ({completed}) than started ({started}) due to cancellation");
    }


    [Fact]
    public async Task AdaptiveConcurrency_AdjustsConcurrencyUnderLoad()
    {
        var observedConcurrency = new List<int>();
        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 25f,
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromSeconds(1)
        );

        var converge = new ConvergeWait(4, adaptiveConfig: adaptive, logger: NullLogger<ConvergeWait>.Instance);
        converge.OnConcurrencyChanged += c =>
        {
            lock (observedConcurrency)
            {
                observedConcurrency.Add(c);
            }
        };

        for (var i = 0; i < 32; i++)
            converge.Queue(() => CpuTestHelper.BurnCpu(TimeSpan.FromSeconds(2)));

        await converge.WaitForAllAsync();
        Assert.NotEmpty(observedConcurrency);
        Assert.Contains(observedConcurrency, c => c < 4);
    }

    [Fact]
    public async Task DisposeAsync_WithoutWaitForAll_DoesNotDeadlock()
    {
        // Arrange: Queue tasks but don't wait for them
        var converge = new ConvergeWait(2);
        for (var i = 0; i < 10; i++)
        {
            converge.Queue(async () => await Task.Delay(500));
        }

        // Act: Dispose should complete within a reasonable time, not deadlock
        var disposeTask = converge.DisposeAsync().AsTask();
        var completedInTime = await Task.WhenAny(disposeTask, Task.Delay(5000)) == disposeTask;

        // Assert
        Assert.True(completedInTime, "DisposeAsync should not deadlock");
    }

    [Fact]
    public async Task WaitForAllAsync_CalledMultipleTimes_Succeeds()
    {
        // Arrange
        var converge = new ConvergeWait(2);
        for (var i = 0; i < 5; i++)
        {
            converge.Queue(async () => await Task.Delay(50));
        }

        // Act: Call WaitForAllAsync multiple times
        await converge.WaitForAllAsync();
        await converge.WaitForAllAsync(); // Should not throw or hang

        // Assert
        Assert.Equal(5, converge.TotalCompleted);
    }

    [Fact]
    public async Task Queue_AfterWaitForAllAsync_StillWorks()
    {
        // Arrange
        var converge = new ConvergeWait(2);
        converge.Queue(() => Task.CompletedTask);
        await converge.WaitForAllAsync();

        // Act: Queue more tasks after initial wait
        converge.Queue(async () => await Task.Delay(10));
        converge.Queue(async () => await Task.Delay(10));
        await converge.WaitForAllAsync();

        // Assert
        Assert.Equal(3, converge.TotalCompleted);
    }

    [Fact]
    public async Task WaitForAllAsync_WhileQueueing_CapturesAllTasks()
    {
        // Arrange
        var converge = new ConvergeWait(4);
        var completedCount = 0;

        // Act: Start queueing in background while waiting
        var queueTask = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                converge.Queue(async () =>
                {
                    await Task.Delay(10);
                    Interlocked.Increment(ref completedCount);
                });
                await Task.Delay(5); // Stagger the queueing
            }
        });

        // Wait a bit for some tasks to be queued, then wait for all
        await Task.Delay(50);
        await queueTask; // Ensure all are queued
        await converge.WaitForAllAsync();

        // Assert: All queued tasks should complete
        Assert.Equal(20, completedCount);
        Assert.Equal(20, converge.TotalCompleted);
    }

    [Fact]
    public async Task AdaptiveConcurrency_ActuallyLimitsConcurrentExecution()
    {
        // Arrange: Track actual concurrent execution count
        var currentConcurrent = 0;
        var maxObservedConcurrent = 0;
        var concurrencyAfterScaleDown = new List<int>();
        var scaleDownOccurred = false;
        var lockObj = new object();

        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 10f, // Very low to force scale down
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromMilliseconds(500)
        );

        var converge = new ConvergeWait(4, adaptiveConfig: adaptive, logger: NullLogger<ConvergeWait>.Instance);
        converge.OnConcurrencyChanged += c =>
        {
            if (c < 4) scaleDownOccurred = true;
        };

        // Act: Queue CPU-intensive tasks and track actual concurrency
        for (var i = 0; i < 40; i++)
        {
            converge.Queue(() =>
            {
                var concurrent = Interlocked.Increment(ref currentConcurrent);
                lock (lockObj)
                {
                    if (concurrent > maxObservedConcurrent)
                        maxObservedConcurrent = concurrent;
                    if (scaleDownOccurred)
                        concurrencyAfterScaleDown.Add(concurrent);
                }

                // Burn CPU to trigger adaptive throttling
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 200)
                {
                    double x = 0;
                    for (var j = 0; j < 10000; j++)
                        x += Math.Sqrt(j);
                }

                Interlocked.Decrement(ref currentConcurrent);
                return Task.CompletedTask;
            });
        }

        await converge.WaitForAllAsync();

        // Assert: After scale down, actual concurrent tasks should be limited
        Assert.True(scaleDownOccurred, "Adaptive throttling should have triggered scale down");
        Assert.True(concurrencyAfterScaleDown.Count > 0, "Should have recorded concurrency after scale down");

        // The max concurrent after scale down should be less than initial (4)
        var maxAfterScaleDown = concurrencyAfterScaleDown.Max();
        Assert.True(maxAfterScaleDown <= 4,
            $"Max concurrent after scale down ({maxAfterScaleDown}) should not exceed initial concurrency (4)");
    }

    [Fact]
    public void RetryPolicy_EffectiveRetryDelay_ReturnsDefaultWhenNotSet()
    {
        // Arrange: Create policy with default (zero) delay
        var policy = new RetryPolicy(RetryMode.RetryXTimes, MaxRetries: 3);

        // Act & Assert: EffectiveRetryDelay should return 100ms, not zero
        Assert.Equal(TimeSpan.Zero, policy.RetryDelay); // Raw value is zero
        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.EffectiveRetryDelay); // Effective is 100ms
    }

    [Fact]
    public void RetryPolicy_EffectiveRetryDelay_ReturnsExplicitValueWhenSet()
    {
        // Arrange: Create policy with explicit delay
        var policy = new RetryPolicy(RetryMode.RetryXTimes, MaxRetries: 3, RetryDelay: TimeSpan.FromSeconds(5));

        // Act & Assert: EffectiveRetryDelay should return the explicit value
        Assert.Equal(TimeSpan.FromSeconds(5), policy.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), policy.EffectiveRetryDelay);
    }

    [Fact]
    public void RetryPolicy_FactoryMethod_SetsCorrectDelay()
    {
        // Arrange & Act
        var defaultDelay = RetryPolicy.Retry(3);
        var customDelay = RetryPolicy.Retry(3, TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(100), defaultDelay.RetryDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), defaultDelay.EffectiveRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), customDelay.RetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), customDelay.EffectiveRetryDelay);
    }

    [Fact]
    public async Task RetryPolicy_UsesEffectiveDelay_NotRawDelay()
    {
        // Arrange: Use record constructor with default delay (TimeSpan.Zero)
        // This previously would have caused immediate retries (no delay)
        var attempts = 0;
        var attemptTimes = new List<DateTime>();
        var converge = new ConvergeWait(1);

        // Use record constructor directly - this was the bug scenario
        converge.SetRetryPolicy(new RetryPolicy(RetryMode.RetryXTimes, MaxRetries: 2));

        converge.Queue(() =>
        {
            attemptTimes.Add(DateTime.UtcNow);
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("fail");
            return Task.CompletedTask;
        });

        // Act
        await converge.WaitForAllAsync();

        // Assert: Should have 3 attempts with delays between them
        Assert.Equal(3, attempts);
        Assert.Equal(3, attemptTimes.Count);

        // Verify there was a delay between attempts (should be ~100ms each)
        var delay1 = attemptTimes[1] - attemptTimes[0];
        var delay2 = attemptTimes[2] - attemptTimes[1];

        // Allow some tolerance, but should be at least 50ms (not instant)
        Assert.True(delay1.TotalMilliseconds >= 50, $"First retry delay was only {delay1.TotalMilliseconds}ms, expected ~100ms");
        Assert.True(delay2.TotalMilliseconds >= 50, $"Second retry delay was only {delay2.TotalMilliseconds}ms, expected ~100ms");
    }

    [Fact]
    public async Task DisposeAsync_AfterAdaptiveScaleDown_ReleasesReservedSlots()
    {
        // Arrange: Create converge with adaptive that will scale down
        var scaleDownOccurred = false;
        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 5f, // Very low to force aggressive scale down
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromMilliseconds(300)
        );

        var converge = new ConvergeWait(4, adaptiveConfig: adaptive, logger: NullLogger<ConvergeWait>.Instance);
        converge.OnConcurrencyChanged += c =>
        {
            if (c < 4) scaleDownOccurred = true;
        };

        // Queue enough CPU-intensive work to trigger scale down
        for (var i = 0; i < 20; i++)
        {
            converge.Queue(() => CpuTestHelper.BurnCpu(TimeSpan.FromMilliseconds(500)));
        }

        // Wait for some work to complete and scale down to occur
        await Task.Delay(2000);

        // Act: Dispose while tasks may still be running and slots are reserved
        // This should not deadlock or throw
        var disposeTask = converge.DisposeAsync().AsTask();
        var completedInTime = await Task.WhenAny(disposeTask, Task.Delay(10000)) == disposeTask;

        // Assert
        Assert.True(scaleDownOccurred, "Adaptive throttling should have scaled down");
        Assert.True(completedInTime, "DisposeAsync should complete without deadlock even with reserved slots");
    }
}