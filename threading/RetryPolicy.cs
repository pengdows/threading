namespace pengdows.threading;

public record RetryPolicy(RetryMode Mode, int MaxRetries = 3, TimeSpan RetryDelay = default)
{
    public static readonly RetryPolicy None = new(RetryMode.None);

    public static RetryPolicy Retry(int times, TimeSpan? delay = null) =>
        new(RetryMode.RetryXTimes, times, delay ?? TimeSpan.FromMilliseconds(100));
}