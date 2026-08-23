namespace pengdows.threading;

/// <summary>
/// Configures retry behavior for failed tasks.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>
    /// Default retry delay used when not specified.
    /// </summary>
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);

    public RetryPolicy(
        RetryMode Mode,
        int MaxRetries = 3,
        TimeSpan RetryDelay = default,
        TimeSpan? MaxDelay = null,
        double BackoffMultiplier = 2.0,
        bool UseJitter = false)
    {
        if (MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetries));
        }

        if (RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        }

        if (MaxDelay.HasValue && MaxDelay.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay));
        }

        if (BackoffMultiplier <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier));
        }

        this.Mode = Mode;
        this.MaxRetries = MaxRetries;
        this.RetryDelay = RetryDelay;
        this.MaxDelay = MaxDelay;
        this.BackoffMultiplier = BackoffMultiplier;
        this.UseJitter = UseJitter;
    }

    /// <summary>
    /// The retry mode (None, RetryXTimes, or ExponentialBackoff).
    /// </summary>
    public RetryMode Mode { get; }

    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    public int MaxRetries { get; }

    /// <summary>
    /// Delay between retry attempts (TimeSpan.Zero means "use the 100ms default").
    /// </summary>
    public TimeSpan RetryDelay { get; }

    /// <summary>
    /// Maximum delay for exponential backoff (optional).
    /// </summary>
    public TimeSpan? MaxDelay { get; }

    /// <summary>
    /// Multiplier for exponential backoff.
    /// </summary>
    public double BackoffMultiplier { get; }

    /// <summary>
    /// When true, exponential backoff adds a random amount (0 to one base delay) on top of
    /// the computed delay, to avoid many callers retrying in lockstep against the same
    /// downstream resource at once ("thundering herd").
    /// </summary>
    public bool UseJitter { get; }

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
    /// <param name="useJitter">Add random jitter on top of the computed delay (default: false).</param>
    public static RetryPolicy Exponential(
        int maxRetries = 5,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2.0,
        bool useJitter = false) =>
        new(RetryMode.ExponentialBackoff, maxRetries, initialDelay ?? DefaultRetryDelay, maxDelay, multiplier, useJitter);

    private TimeSpan GetExponentialDelay(int attempt)
    {
        var baseDelay = EffectiveRetryDelay;
        var factor = Math.Pow(BackoffMultiplier, attempt - 1);
        var delayMs = baseDelay.TotalMilliseconds * factor;

        if (double.IsInfinity(delayMs) || double.IsNaN(delayMs) || delayMs > TimeSpan.MaxValue.TotalMilliseconds)
        {
            return MaxDelay ?? TimeSpan.MaxValue;
        }

        var delay = TimeSpan.FromMilliseconds(delayMs);

        if (UseJitter)
        {
            delay += TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * baseDelay.TotalMilliseconds);
        }

        if (MaxDelay.HasValue && delay > MaxDelay.Value)
        {
            return MaxDelay.Value;
        }

        return delay;
    }
}
