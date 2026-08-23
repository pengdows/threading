namespace pengdows.threading;

/// <summary>
/// Interface for a task coordinator that limits concurrent execution
/// and provides progress tracking with optional adaptive throttling.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WaitForAllAsync"/> and <see cref="IAsyncDisposable.DisposeAsync"/> give two
/// different, deliberately different guarantees:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Quiescence</b> (<see cref="WaitForAllAsync"/>): returns once the tracked work list is
///   observed empty. It never closes admission, so it is safe to call repeatedly on a still-live
///   instance - but a <see cref="Queue"/>/<see cref="QueueAsync"/> call that lands concurrently
///   with the empty-check is not guaranteed to be observed by that particular call. Reliable use
///   requires the caller to stop producing before awaiting it (every test in this library's own
///   suite follows that pattern).
///   </description></item>
///   <item><description>
///   <b>Completion</b> (<see cref="IAsyncDisposable.DisposeAsync"/>): permanently closes
///   admission first (further <see cref="Queue"/>/<see cref="QueueAsync"/> calls throw
///   <see cref="ObjectDisposedException"/>), then drains every already-accepted task before
///   returning. This is the only way to get a guarantee that no more work can ever be admitted
///   as of the moment you call it - at the cost of the instance being unusable afterward.
///   </description></item>
/// </list>
/// <para>
/// There is currently no primitive that closes admission, drains to true completion, and then
/// reopens for a new batch on the same instance - only the reusable-but-quiescence-only
/// <see cref="WaitForAllAsync"/> and the completion-but-terminal <see cref="IAsyncDisposable.DisposeAsync"/>.
/// </para>
/// </remarks>
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
    /// Waits until the tracked work list is observed empty - quiescence, not closure-backed
    /// completion. See the type-level remarks for the distinction from
    /// <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task WaitForAllAsync();
}
