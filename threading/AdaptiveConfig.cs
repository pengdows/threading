namespace pengdows.threading;

public record AdaptiveConfig
{
    public AdaptiveConfig(
        float TargetCpuUsagePercent = 70f,
        int MinConcurrency = 1,
        int MaxConcurrency = 64,
        TimeSpan SamplingInterval = default)
    {
        if ((TargetCpuUsagePercent <= 0f) || (TargetCpuUsagePercent >= 100f))
        {
            throw new ArgumentOutOfRangeException(nameof(TargetCpuUsagePercent));
        }

        if (MinConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinConcurrency));
        }

        if (MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        }

        if (MinConcurrency > MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(MinConcurrency));
        }

        if (SamplingInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SamplingInterval));
        }

        this.TargetCpuUsagePercent = TargetCpuUsagePercent;
        this.MinConcurrency = MinConcurrency;
        this.MaxConcurrency = MaxConcurrency;
        this.SamplingInterval = SamplingInterval == default
            ? TimeSpan.FromSeconds(2)
            : SamplingInterval;
    }

    public float TargetCpuUsagePercent { get; init; }

    public int MinConcurrency { get; init; }

    public int MaxConcurrency { get; init; }

    public TimeSpan SamplingInterval { get; init; }

    public static readonly AdaptiveConfig Default = new(
        TargetCpuUsagePercent: 70f,
        MinConcurrency: 1,
        MaxConcurrency: Environment.ProcessorCount * 2,
        SamplingInterval: TimeSpan.FromSeconds(2)
    );
}
