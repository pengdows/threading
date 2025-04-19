namespace pengdows.threading;

public record AdaptiveConfig(
    float TargetCpuUsagePercent = 70f,
    int MinConcurrency = 1,
    int MaxConcurrency = 64,
    TimeSpan SamplingInterval = default
)
{
    public static readonly AdaptiveConfig Default = new(
        TargetCpuUsagePercent: 70f,
        MinConcurrency: 1,
        MaxConcurrency: Environment.ProcessorCount * 2,
        SamplingInterval: TimeSpan.FromSeconds(2)
    );
}