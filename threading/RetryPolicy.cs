namespace pengdows.threading;

/// <summary>
/// Configures retry behavior for failed tasks.
/// </summary>
/// <param name="Mode">The retry mode (None or RetryXTimes).</param>
/// <param name="MaxRetries">Maximum number of retry attempts (default: 3).</param>
/// <param name="RetryDelay">Delay between retry attempts (default: 100ms).</param>
public record RetryPolicy(RetryMode Mode, int MaxRetries = 3, TimeSpan RetryDelay = default)
{
    /// <summary>
    /// Default retry delay used when not specified.
    /// </summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the effective retry delay, using 100ms if not explicitly set.
    /// </summary>
    public TimeSpan EffectiveRetryDelay => RetryDelay == default ? DefaultRetryDelay : RetryDelay;

    /// <summary>
    /// No retry - task fails immediately on first error.
    /// </summary>
    public static readonly RetryPolicy None = new(RetryMode.None);

    /// <summary>
    /// Creates a retry policy that retries a specified number of times.
    /// </summary>
    /// <param name="times">Maximum number of retry attempts.</param>
    /// <param name="delay">Delay between retries (default: 100ms).</param>
    public static RetryPolicy Retry(int times, TimeSpan? delay = null) =>
        new(RetryMode.RetryXTimes, times, delay ?? DefaultRetryDelay);
}