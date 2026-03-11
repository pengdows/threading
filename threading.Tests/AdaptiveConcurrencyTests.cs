using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace pengdows.threading.Tests;

public class AdaptiveConcurrencyTests
{
    [Fact]
    public void AdaptiveConfig_DefaultConstructor_UsesSafeDefaults()
    {
        var config = new AdaptiveConfig();

        Assert.Equal(70f, config.TargetCpuUsagePercent);
        Assert.Equal(1, config.MinConcurrency);
        Assert.Equal(64, config.MaxConcurrency);
        Assert.Equal(TimeSpan.FromSeconds(2), config.SamplingInterval);
    }

    [Theory]
    [InlineData(-1)]
    public void AdaptiveConfig_InvalidSamplingInterval_Throws(int milliseconds)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConfig(
            SamplingInterval: TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("SamplingInterval", ex.ParamName);
    }

    [Fact]
    public void AdaptiveConfig_MinConcurrencyGreaterThanMax_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConfig(
            MinConcurrency: 5,
            MaxConcurrency: 4));

        Assert.Equal("MinConcurrency", ex.ParamName);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(100f)]
    [InlineData(-10f)]
    [InlineData(150f)]
    public void AdaptiveConfig_InvalidCpuTarget_Throws(float targetCpuUsagePercent)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConfig(
            TargetCpuUsagePercent: targetCpuUsagePercent));

        Assert.Equal("TargetCpuUsagePercent", ex.ParamName);
    }

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


    [Fact]
    public async Task AdaptiveConcurrency_ScalesUpAsCpuUsageDrops()
    {
        // Arrange: Configure for aggressive scale-up when CPU is idle
        var observedConcurrency = new List<int>();
        var adaptive = new AdaptiveConfig(
            TargetCpuUsagePercent: 95f, // Very high target - any idle time should trigger scale up
            MinConcurrency: 1,
            MaxConcurrency: 8,
            SamplingInterval: TimeSpan.FromMilliseconds(500) // Fast sampling for quicker response
        );

        var converge = new ConvergeWait(1, adaptiveConfig: adaptive, logger: NullLogger<ConvergeWait>.Instance);
        converge.OnConcurrencyChanged += c =>
        {
            lock (observedConcurrency)
            {
                observedConcurrency.Add(c);
            }
        };

        // Queue many tasks with delays (low CPU) to give adaptive time to scale up
        for (var i = 0; i < 30; i++)
        {
            converge.Queue(async () => await Task.Delay(300));
        }

        await converge.WaitForAllAsync();

        // Assert: Should have scaled up at least once
        Assert.NotEmpty(observedConcurrency);
        Assert.Contains(observedConcurrency, c => c > 1);
    }
}
