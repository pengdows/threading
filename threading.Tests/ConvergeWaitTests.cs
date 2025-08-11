using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        {
            converge.Queue(async () =>
            {
                await Task.Delay(10);
                Interlocked.Increment(ref count);
            });
        }
        await converge.WaitForAllAsync();
        Assert.Equal(20, converge.TotalCompleted);
        Assert.Equal(20, count);
    }

    [Fact]
    public async Task MaxInFlight_NeverExceedsLimit()
    {
        const int limit = 4;
        var converge = new ConvergeWait(limit);
        var inFlight = 0;
        var maxInFlight = 0;

        for (var i = 0; i < 100; i++)
        {
            converge.Queue(async () =>
            {
                var cur = Interlocked.Increment(ref inFlight);
                UpdateMax(ref maxInFlight, cur);
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
            });
        }

        await converge.WaitForAllAsync();
        Assert.True(maxInFlight <= limit, $"Observed {maxInFlight} > {limit}");
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
        {
            converge.Queue(() => Task.CompletedTask);
        }

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
    public async Task Queue_AfterDispose_Throws()
    {
        var converge = new ConvergeWait(1);
        await converge.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => converge.Queue(() => Task.CompletedTask));
    }

    [Fact(Skip = "Unreliable in CI environments due to CPU variability")]
    public async Task Tasks_StopOnCancellation()
    {
        using var cts = new CancellationTokenSource();
        var converge = new ConvergeWait(1, cancellationToken: cts.Token);
        var started = 0;

        for (var i = 0; i < 10; i++)
        {
            converge.Queue(async () =>
            {
                Interlocked.Increment(ref started);
                await Task.Delay(5000, cts.Token);
            });
        }

        cts.CancelAfter(100);

        try
        {
            await converge.WaitForAllAsync();
        }
        catch (OperationCanceledException)
        {
            // expected: due to cancellation token triggering inside SemaphoreSlim
        }

        Assert.True(started < 10, "Not all tasks should have started due to cancellation.");
        Assert.True(converge.HasFailures || converge.TotalCompleted < 10);
    }


    [Fact ]
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
        {
            converge.Queue(() => CpuTestHelper.BurnCpu(TimeSpan.FromSeconds(2)));
        }

        await converge.WaitForAllAsync();
        Assert.NotEmpty(observedConcurrency);
        Assert.Contains(observedConcurrency, c => c < 4);
    }

    [Fact]
    public async Task RetryPolicy_DoesNotRetryCanceledException()
    {
        var attempts = 0;
        var converge = new ConvergeWait(1);
        converge.SetRetryPolicy(RetryPolicy.Retry(3));
        converge.Queue(() =>
        {
            attempts++;
            throw new OperationCanceledException();
        });

        await converge.WaitForAllAsync();
        Assert.Equal(1, attempts);
        Assert.Equal(1, converge.TotalFailed);
    }

    private static void UpdateMax(ref int target, int value)
    {
        int initial, computed;
        do
        {
            initial = target;
            computed = Math.Max(initial, value);
        }
        while (initial != Interlocked.CompareExchange(ref target, computed, initial));
    }
}