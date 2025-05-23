using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace pengdows.threading.Tests;

public class AdaptiveConcurrencyTests
{
    //[Fact(Skip = "Long-running test, enable manually if needed")]
    [Fact]
    public async Task AdaptiveConcurrency_AdjustsConcurrencyUnderLoad()
    {
        // Arrange
        var observedConcurrency = new List<int>();
        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 25f, // set low to force scaling down
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromSeconds(1)
        );

        var converge = new ConvergeWait(
            maxConcurrency: 4,
            adaptiveConfig: adaptive,
            logger: NullLogger<ConvergeWait>.Instance
        );

        converge.OnConcurrencyChanged += concurrency =>
        {
            lock (observedConcurrency)
            {
                observedConcurrency.Add(concurrency);
            }
        };

        for (var i = 0; i < 32; i++)
        {
            converge.Queue(() => CpuTestHelper.BurnCpu(TimeSpan.FromSeconds(2)));
        }

        // Act
        await converge.WaitForAllAsync();

        // Assert
        Assert.NotEmpty(observedConcurrency);
        Assert.Contains(observedConcurrency, c => c < 4); // should scale down at least once
    }


    [Fact(Skip = "Unreliable in CI environments due to CPU variability")]
    public async Task AdaptiveConcurrency_ScalesUpAsCpuUsageDrops()
    {
        var observedConcurrency = new List<int>();
        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 90f,
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromSeconds(1)
        );

        var converge = new ConvergeWait(1, adaptiveConfig: adaptive, logger: NullLogger<ConvergeWait>.Instance);
        converge.OnConcurrencyChanged += c =>
        {
            lock (observedConcurrency)
            {
                observedConcurrency.Add(c);
            }
        };

        for (var i = 0; i < 16; i++)
        {
            converge.Queue(async () => await Task.Delay(500));
        }

        await converge.WaitForAllAsync();
        Assert.NotEmpty(observedConcurrency);
        Assert.Contains(observedConcurrency, c => c > 1);
    }
}