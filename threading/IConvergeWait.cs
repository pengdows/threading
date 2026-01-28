namespace pengdows.threading;

/// <summary>
/// Interface for a task coordinator that limits concurrent execution
/// and provides progress tracking with optional adaptive throttling.
/// </summary>
public interface IConvergeWait : IAsyncDisposable
{
    /// <summary>
    /// Raised when a task is queued. Parameter is the total number of queued tasks.
    /// </summary>
    event Action<int>? OnQueued;

    /// <summary>
    /// Raised when a task completes successfully. Parameter is the total number of completed tasks.
    /// </summary>
    event Action<int>? OnCompleted;

    /// <summary>
    /// Raised when a task fails permanently (after exhausting retries). Parameter is the exception.
    /// </summary>
    event Action<Exception>? OnError;

    /// <summary>
    /// Raised when adaptive concurrency adjusts the concurrency limit. Parameter is the new limit.
    /// </summary>
    event Action<int>? OnConcurrencyChanged;

    /// <summary>
    /// Raised when progress percentage changes. Parameter is the percent value (0-100).
    /// </summary>
    event Action<double>? OnProgress;

    /// <summary>
    /// Total number of tasks that have been queued.
    /// </summary>
    int TotalQueued { get; }

    /// <summary>
    /// Total number of tasks that have completed successfully.
    /// </summary>
    int TotalCompleted { get; }

    /// <summary>
    /// Total number of tasks that have failed permanently.
    /// </summary>
    int TotalFailed { get; }

    /// <summary>
    /// True if any tasks have failed permanently.
    /// </summary>
    bool HasFailures { get; }

    /// <summary>
    /// Collection of exceptions from permanently failed tasks.
    /// </summary>
    IReadOnlyList<Exception> Exceptions { get; }

    /// <summary>
    /// Percentage of completed tasks out of total queued (0-100).
    /// </summary>
    double ProgressPercent { get; }

    /// <summary>
    /// Sets the retry policy for failed tasks.
    /// </summary>
    /// <param name="policy">The retry policy to apply to subsequent task failures.</param>
    void SetRetryPolicy(RetryPolicy policy);

    /// <summary>
    /// Queues a task for execution. The task will start when a concurrency slot is available.
    /// </summary>
    /// <param name="workItem">The async work to execute.</param>
    void Queue(Func<Task> workItem);

    /// <summary>
    /// Queues a task for execution asynchronously, waiting for a concurrency slot before scheduling.
    /// </summary>
    /// <param name="workItem">The async work to execute.</param>
    /// <param name="ct">Cancellation token for the enqueue operation.</param>
    Task QueueAsync(Func<Task> workItem, CancellationToken ct = default);

    /// <summary>
    /// Waits for all currently queued tasks to complete.
    /// </summary>
    Task WaitForAllAsync();
}
