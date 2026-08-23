using System;
using System.Linq;
using Xunit;

namespace pengdows.threading.Tests;

public class RetryPolicyValidationTests
{
    [Fact]
    public void RetryPolicy_Properties_HaveNoPubliclyAccessibleSetter()
    {
        foreach (var prop in typeof(RetryPolicy).GetProperties())
        {
            Assert.Null(prop.SetMethod);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void RetryPolicy_NegativeMaxRetries_Throws(int maxRetries)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(RetryMode.RetryXTimes, MaxRetries: maxRetries));

        Assert.Equal("MaxRetries", ex.ParamName);
    }

    [Fact]
    public void RetryPolicy_NegativeRetryDelay_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(RetryMode.RetryXTimes, RetryDelay: TimeSpan.FromMilliseconds(-1)));

        Assert.Equal("RetryDelay", ex.ParamName);
    }

    [Fact]
    public void RetryPolicy_NegativeMaxDelay_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(RetryMode.ExponentialBackoff, MaxDelay: TimeSpan.FromMilliseconds(-1)));

        Assert.Equal("MaxDelay", ex.ParamName);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public void RetryPolicy_NonPositiveBackoffMultiplier_Throws(double multiplier)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RetryPolicy(RetryMode.ExponentialBackoff, BackoffMultiplier: multiplier));

        Assert.Equal("BackoffMultiplier", ex.ParamName);
    }

    [Fact]
    public void RetryPolicy_Exponential_NegativeMultiplier_ThrowsAtConstruction()
    {
        // Previously this passed construction silently, then crashed Task.Delay with an
        // unrelated ArgumentOutOfRangeException the first time an odd-numbered retry attempt
        // computed a negative delay - escaping ConvergeWait's own error-isolation contract.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Exponential(multiplier: -2.0));
    }

    [Fact]
    public void RetryPolicy_ExponentialBackoff_WithoutJitter_IsDeterministic()
    {
        var policy = RetryPolicy.Exponential(initialDelay: TimeSpan.FromMilliseconds(100), multiplier: 2.0);

        var delays = Enumerable.Range(0, 10).Select(_ => policy.GetDelay(3)).ToArray();

        Assert.All(delays, d => Assert.Equal(delays[0], d));
    }

    [Fact]
    public void RetryPolicy_ExponentialBackoff_WithJitter_VariesAcrossCalls()
    {
        var policy = new RetryPolicy(
            RetryMode.ExponentialBackoff,
            RetryDelay: TimeSpan.FromMilliseconds(100),
            BackoffMultiplier: 2.0,
            UseJitter: true);

        var baseDelay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, 2)); // attempt 3 -> 400ms unjittered
        var delays = Enumerable.Range(0, 30).Select(_ => policy.GetDelay(3)).ToArray();

        Assert.Contains(delays, d => d != delays[0]);
        Assert.All(delays, d =>
        {
            Assert.True(d >= baseDelay, $"Jittered delay {d} should never be below the unjittered base {baseDelay}");
            Assert.True(d <= baseDelay + TimeSpan.FromMilliseconds(100), $"Jittered delay {d} should stay within one base-delay window above {baseDelay}");
        });
    }

    [Fact]
    public void RetryPolicy_ExponentialBackoff_WithJitter_StillClampsToMaxDelay()
    {
        var policy = new RetryPolicy(
            RetryMode.ExponentialBackoff,
            RetryDelay: TimeSpan.FromMilliseconds(100),
            MaxDelay: TimeSpan.FromMilliseconds(150),
            BackoffMultiplier: 2.0,
            UseJitter: true);

        var delays = Enumerable.Range(0, 30).Select(_ => policy.GetDelay(3));

        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromMilliseconds(150)));
    }
}
