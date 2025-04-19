namespace pengdows.threading;

public interface IConvergeWait : IAsyncDisposable
{
    event Action<int>? OnQueued;
    event Action<int>? OnCompleted;
    event Action<Exception>? OnError;
    event Action<int>? OnConcurrencyChanged;

    int TotalQueued { get; }
    int TotalCompleted { get; }
    int TotalFailed { get; }
    bool HasFailures { get; }
    IReadOnlyList<Exception> Exceptions { get; }

    void SetRetryPolicy(RetryPolicy policy);
    void Queue(Func<Task> workItem);
    Task WaitForAllAsync();
}