using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace pengdows.threading.Tests;

/// <summary>
/// End-to-end tests for the core promise the class is named for: once WaitForAllAsync (or
/// DisposeAsync) returns, every queued unit of work has actually finished - not merely started,
/// not merely observed once, but genuinely converged - even when work keeps arriving from
/// multiple real producer threads and runs on the real thread pool with real delays.
/// </summary>
public class ConvergenceIntegrationTests
{
    [Fact]
    public async Task WaitForAllAsync_ConvergesAllWorkFromMultipleConcurrentProducers()
    {
        const int producerCount = 5;
        const int itemsPerProducer = 40;
        const int totalItems = producerCount * itemsPerProducer;

        var converge = new ConvergeWait(8);
        var finished = new ConcurrentDictionary<int, bool>();
        var rng = new Random(12345);

        var producers = Enumerable.Range(0, producerCount).Select(producerIndex => Task.Run(async () =>
        {
            for (var i = 0; i < itemsPerProducer; i++)
            {
                var itemId = (producerIndex * itemsPerProducer) + i;
                var delayMs = rng.Next(1, 15);

                converge.Queue(async () =>
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                    finished[itemId] = true;
                });

                // Interleave producers with real work rather than queueing in a tight loop, so
                // WaitForAllAsync is genuinely racing against still-arriving work.
                await Task.Delay(1).ConfigureAwait(false);
            }
        })).ToArray();

        // Start waiting for convergence concurrently with producers still enqueueing work -
        // this is exactly the scenario a single snapshot-and-await would get wrong.
        var waitTask = Task.Run(async () =>
        {
            // Give producers a moment to get going so the wait genuinely overlaps production.
            await Task.Delay(10);
            await converge.WaitForAllAsync();
        });

        await Task.WhenAll(producers);
        await waitTask;

        Assert.Equal(totalItems, converge.TotalCompleted);
        Assert.False(converge.HasFailures);

        for (var itemId = 0; itemId < totalItems; itemId++)
        {
            Assert.True(finished.TryGetValue(itemId, out var done) && done,
                $"Item {itemId} had not finished by the time WaitForAllAsync returned.");
        }

        await converge.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ConvergesAllQueuedWorkWithoutAnExplicitWaitForAllFirst()
    {
        const int itemCount = 200;

        var converge = new ConvergeWait(6);
        var finished = new bool[itemCount];
        var rng = new Random(54321);

        for (var i = 0; i < itemCount; i++)
        {
            var itemId = i;
            var delayMs = rng.Next(1, 20);

            converge.Queue(async () =>
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                Volatile.Write(ref finished[itemId], true);
            });
        }

        // No WaitForAllAsync() call here on purpose: DisposeAsync alone must be sufficient to
        // guarantee every queued item has actually run to completion before it returns.
        await converge.DisposeAsync();

        Assert.Equal(itemCount, converge.TotalCompleted);
        Assert.False(converge.HasFailures);

        for (var i = 0; i < itemCount; i++)
        {
            Assert.True(Volatile.Read(ref finished[i]), $"Item {i} had not finished by the time DisposeAsync returned.");
        }
    }

    [Fact]
    public async Task WaitForAllAsync_ConvergesUnderAdaptiveThrottlingAndRetries()
    {
        const int itemCount = 60;

        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 50f,
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromMilliseconds(200));

        var converge = new ConvergeWait(4, adaptiveConfig: adaptive);
        converge.SetRetryPolicy(RetryPolicy.Retry(2, TimeSpan.FromMilliseconds(5)));

        var finished = new ConcurrentDictionary<int, bool>();
        var attempts = new ConcurrentDictionary<int, int>();

        for (var i = 0; i < itemCount; i++)
        {
            var itemId = i;
            converge.Queue(async () =>
            {
                var attempt = attempts.AddOrUpdate(itemId, 1, (_, count) => count + 1);

                // Every third item fails on its first attempt, exercising retry alongside
                // adaptive throttling, without ever failing permanently.
                if (itemId % 3 == 0 && attempt == 1)
                {
                    throw new InvalidOperationException($"transient failure for item {itemId}");
                }

                await Task.Delay(2).ConfigureAwait(false);
                finished[itemId] = true;
            });
        }

        await converge.WaitForAllAsync();

        Assert.Equal(itemCount, converge.TotalCompleted);
        Assert.False(converge.HasFailures);

        for (var itemId = 0; itemId < itemCount; itemId++)
        {
            Assert.True(finished.TryGetValue(itemId, out var done) && done,
                $"Item {itemId} had not finished by the time WaitForAllAsync returned.");
        }

        await converge.DisposeAsync();
    }
}
