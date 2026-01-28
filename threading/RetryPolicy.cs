namespace pengdows.threading;

/// <summary>
/// Configures retry behavior for failed tasks.
/// </summary>
/// <param name="Mode">The retry mode (None or RetryXTimes).</param>
/// <param name="MaxRetries">Maximum number of retry attempts (default: 3).</param>
/// <param name="RetryDelay">Delay between retry attempts (default: 100ms).</param>
/// <param name="MaxDelay">Maximum delay for exponential backoff (optional).</param>
/// <param name="BackoffMultiplier">Multiplier for exponential backoff (default: 2.0).</param>
public record RetryPolicy(
    RetryMode Mode,
    int MaxRetries = 3,
    TimeSpan RetryDelay = default,
    TimeSpan? MaxDelay = null,
    double BackoffMultiplier = 2.0)
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
    /// Gets the delay for the specified retry attempt.
    /// </summary>
    /// <param name="attempt">The 1-based attempt number.</param>
    public TimeSpan GetDelay(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        return Mode switch
        {
            RetryMode.ExponentialBackoff => GetExponentialDelay(attempt),
            _ => EffectiveRetryDelay
        };
    }

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

    /// <summary>
    /// Creates a retry policy with exponential backoff.
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="initialDelay">Initial delay before retrying (default: 100ms).</param>
    /// <param name="maxDelay">Maximum delay between retries (optional).</param>
    /// <param name="multiplier">Exponent multiplier (default: 2.0).</param>
    public static RetryPolicy Exponential(
        int maxRetries = 5,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2.0) =>
        new(RetryMode.ExponentialBackoff, maxRetries, initialDelay ?? DefaultRetryDelay, maxDelay, multiplier);

    private TimeSpan GetExponentialDelay(int attempt)
    {
        var baseDelay = EffectiveRetryDelay;
        var factor = Math.Pow(BackoffMultiplier, attempt - 1);
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);

        if (MaxDelay.HasValue && delay > MaxDelay.Value)
        {
            return MaxDelay.Value;
        }

        return delay;
    }
}
