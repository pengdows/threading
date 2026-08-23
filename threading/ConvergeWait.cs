using Microsoft.Extensions.Logging;

namespace pengdows.threading;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// ConvergeWait limits the number of concurrently executing tasks and waits for all to complete.
/// Optionally adjusts concurrency dynamically based on CPU saturation.
/// </summary>
public sealed class ConvergeWait : IConvergeWait
{
    private readonly SemaphoreSlim _semaphore;
    private readonly List<Task> _tasks = new();
    private readonly List<Exception> _exceptions = new();
    private readonly object _lock = new();
    private readonly CancellationToken _cancellationToken;
    private readonly AdaptiveConfig? _adaptiveConfig;
    private readonly CancellationTokenSource _internalCts = new();
    private readonly ILogger<ConvergeWait>? _logger;
    private readonly int _maxConcurrency;

    private const int DisposeStateActive = 0;
    private const int DisposeStateDisposing = 1;
    private const int DisposeStateDisposed = 2;

    private int _currentConcurrency;
    private int _disposeState;
    private int _reservedSlots; // Slots actually withheld to reduce effective concurrency
    private int _pendingReservationDebt; // Slots the next completions still owe as withheld
    private Task? _disposeTask;
    private Task? _monitorTask;
    private RetryPolicy _retryPolicy = RetryPolicy.None;
    private int _totalCompleted;
    private int _totalFailed;
    private int _totalQueued;

    public event Action<int>? OnQueued;
    public event Action<int>? OnCompleted;
    public event Action<Exception>? OnError;
    public event Action<int>? OnConcurrencyChanged;
    public event Action<double>? OnProgress;

    public int TotalQueued => _totalQueued;

    public int TotalCompleted => _totalCompleted;

    public int TotalFailed => _totalFailed;
    public bool HasFailures => _totalFailed > 0;
    public IReadOnlyList<Exception> Exceptions
    {
        get
        {
            lock (_lock)
            {
                return _exceptions.ToArray();
            }
        }
    }
    public double ProgressPercent
    {
        get
        {
            var queued = Volatile.Read(ref _totalQueued);
            if (queued <= 0)
            {
                return 0;
            }

            var completed = Volatile.Read(ref _totalCompleted);
            var failed = Volatile.Read(ref _totalFailed);
            var percent = (double)(completed + failed) / queued * 100.0;
            return percent > 100.0 ? 100.0 : percent;
        }
    }

    public ConvergeWait(int maxConcurrency,
        AdaptiveConfig? adaptiveConfig = null,
        CancellationToken cancellationToken = default,
        ILogger<ConvergeWait>? logger = null)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        if (adaptiveConfig != null && maxConcurrency > adaptiveConfig.MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency),
                "maxConcurrency cannot exceed adaptiveConfig.MaxConcurrency.");
        }

        _cancellationToken = cancellationToken;
        _adaptiveConfig = adaptiveConfig;
        _currentConcurrency = maxConcurrency;
        _logger = logger;

        // When adaptive config is provided, use its max as the semaphore limit
        // so we can scale up beyond initial maxConcurrency if needed
        _maxConcurrency = _adaptiveConfig?.MaxConcurrency ?? maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, _maxConcurrency);

        if (_adaptiveConfig != null)
        {
            _monitorTask = Task.Run(MonitorCpuLoadAsync, _internalCts.Token);
        }
    }

    public void SetRetryPolicy(RetryPolicy policy)
    {
        Volatile.Write(ref _retryPolicy, policy);
    }


    public void Queue(Func<Task> workItem)
    {
        if (workItem == null)
        {
            throw new ArgumentNullException(nameof(workItem));
        }

        ThrowIfNotActive();

        Task task;
        int queued;
        lock (_lock)
        {
            ThrowIfNotActive();

            task = Task.Run(async () =>
            {
                await _semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
                try
                {
                    await ExecuteWithRetryAsync(workItem, _cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseOrReserve();
                }
            }, _cancellationToken);

            _tasks.Add(task);
            queued = Interlocked.Increment(ref _totalQueued);
        }

        InvokeObserver(OnQueued, queued, nameof(OnQueued));
        RaiseProgress();
    }

    public async Task QueueAsync(Func<Task> workItem, CancellationToken ct = default)
    {
        if (workItem == null)
        {
            throw new ArgumentNullException(nameof(workItem));
        }

        ThrowIfNotActive();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, ct);

        // The admission wait (for a free concurrency slot) additionally observes _internalCts,
        // which DisposeAsync cancels immediately. That way a call still waiting for a slot is
        // canceled promptly instead of risking a hang against a semaphore DisposeAsync may go on
        // to dispose while this call is still blocked. Once admitted, execution/retry below uses
        // linkedCts alone, so an already-running item's retry backoff is unaffected by disposal.
        using (var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, _internalCts.Token))
        {
            try
            {
                await _semaphore.WaitAsync(admissionCts.Token).ConfigureAwait(false);
            }
            catch
            {
                linkedCts.Dispose();
                throw;
            }
        }

        int queued;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposeState) != DisposeStateActive)
            {
                linkedCts.Dispose();
                _semaphore.Release();
                throw new ObjectDisposedException(GetType().FullName);
            }

            var task = Task.Run(async () =>
            {
                try
                {
                    await ExecuteWithRetryAsync(workItem, linkedCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    linkedCts.Dispose();
                    ReleaseOrReserve();
                }
            }, CancellationToken.None);

            _tasks.Add(task);
            queued = Interlocked.Increment(ref _totalQueued);
        }

        InvokeObserver(OnQueued, queued, nameof(OnQueued));
        RaiseProgress();
    }

    public async Task WaitForAllAsync()
    {
        // Loop until the task list is genuinely empty, not just until one snapshot drains.
        // A single snapshot-and-await would let work queued by another thread mid-wait slip
        // past unobserved, letting the caller resume before everything has converged.
        while (true)
        {
            Task[] snapshot;
            lock (_lock)
            {
                snapshot = _tasks.ToArray();
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            finally
            {
                PruneCompletedTasks();
            }

            lock (_lock)
            {
                if (_tasks.Count == 0)
                {
                    return;
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        bool isOwner;
        lock (_lock)
        {
            isOwner = _disposeTask == null;
            if (isOwner)
            {
                _disposeState = DisposeStateDisposing;
                _disposeTask = DisposeCoreAsync();
            }

            disposeTask = _disposeTask!;
        }

        // Only the caller that actually triggered disposal observes its outcome (including any
        // exception). Every other caller - whether joining concurrently or arriving after the
        // fact - waits for that same completion but returns normally, matching the standard
        // IAsyncDisposable expectation that calling DisposeAsync more than once is safe.
        return isOwner ? new ValueTask(disposeTask) : new ValueTask(WaitWithoutObservingFaultAsync(disposeTask));
    }

    private static async Task WaitWithoutObservingFaultAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed - the owning caller already observed/threw this.
        }
    }

    private async Task DisposeCoreAsync()
    {
        _internalCts.Cancel();
        if (_monitorTask != null)
        {
            await _monitorTask.ConfigureAwait(false);
        }

        // Cancel any outstanding reservation debt so the drain below can't stall: without this,
        // a task completing during the drain could keep withholding its permit (honoring a
        // stale throttling target) instead of releasing it to another task still waiting on
        // admission, which could stop that task's Task.WhenAll from ever converging.
        Interlocked.Exchange(ref _pendingReservationDebt, 0);

        // Release any already-reserved slots before disposing
        var reserved = Interlocked.Exchange(ref _reservedSlots, 0);
        if (reserved > 0)
        {
            _semaphore.Release(reserved);
        }

        ExceptionDispatchInfo? capturedException = null;

        try
        {
            Task[] snapshot;
            lock (_lock)
            {
                snapshot = _tasks.ToArray();
            }

            await Task.WhenAll(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            lock (_lock)
            {
                _tasks.Clear();
            }

            _semaphore.Dispose();
            _internalCts.Dispose();
            Volatile.Write(ref _disposeState, DisposeStateDisposed);
        }

        capturedException?.Throw();
    }

    private async Task MonitorCpuLoadAsync()
    {
        var sw = Stopwatch.StartNew();
        var lastTotalCpu = Process.GetCurrentProcess().TotalProcessorTime;

        try
        {
            while (!_internalCts.IsCancellationRequested)
            {
                await Task.Delay(_adaptiveConfig!.SamplingInterval, _internalCts.Token).ConfigureAwait(false);

                var nowTotalCpu = Process.GetCurrentProcess().TotalProcessorTime;
                var deltaCpu = nowTotalCpu - lastTotalCpu;
                lastTotalCpu = nowTotalCpu;

                var elapsed = sw.Elapsed;
                sw.Restart();

                var cpuUsage = 100f *
                               (float)(deltaCpu.TotalMilliseconds /
                                       (Environment.ProcessorCount * elapsed.TotalMilliseconds));

                var newConcurrency = _currentConcurrency;

                if (cpuUsage < _adaptiveConfig.TargetCpuUsagePercent - 5 &&
                    _currentConcurrency < _adaptiveConfig.MaxConcurrency)
                {
                    newConcurrency++;
                }
                else if (cpuUsage > _adaptiveConfig.TargetCpuUsagePercent + 5 &&
                         _currentConcurrency > _adaptiveConfig.MinConcurrency)
                {
                    newConcurrency--;
                }

                if (newConcurrency != _currentConcurrency)
                {
                    AdjustConcurrency(newConcurrency, cpuUsage);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disposing - exit gracefully
        }
    }

    private void AdjustConcurrency(int newConcurrency, float cpuUsage)
    {
        var delta = newConcurrency - _currentConcurrency;

        if (delta > 0)
        {
            // Scaling UP: first cancel any outstanding reservation debt (slots promised to a
            // prior scale-down that haven't been withheld yet), then release already-reserved
            // slots, then inject new capacity if still short.
            var remaining = delta;

            var debt = Volatile.Read(ref _pendingReservationDebt);
            if (debt > 0)
            {
                var toCancel = Math.Min(remaining, debt);
                Interlocked.Add(ref _pendingReservationDebt, -toCancel);
                remaining -= toCancel;
            }

            var toRelease = Math.Min(remaining, _reservedSlots);
            if (toRelease > 0)
            {
                _semaphore.Release(toRelease);
                Interlocked.Add(ref _reservedSlots, -toRelease);
                remaining -= toRelease;
            }

            if (remaining > 0 && _semaphore.CurrentCount + remaining <= _maxConcurrency)
            {
                _semaphore.Release(remaining);
            }
        }
        else if (delta < 0)
        {
            // Scaling DOWN: record a reservation debt rather than racing a synchronous Wait(0)
            // against the semaphore's own backlog of already-registered async waiters - a race
            // SemaphoreSlim always resolves in the backlog's favor under real contention, which
            // is exactly when scale-down matters most. The next `-delta` task completions each
            // withhold their permit (see ReleaseOrReserve) instead of releasing it back to the
            // pool, so the target is always deterministically reached, just not instantaneously.
            Interlocked.Add(ref _pendingReservationDebt, -delta);
        }

        _currentConcurrency = newConcurrency;
        InvokeObserver(OnConcurrencyChanged, _currentConcurrency, nameof(OnConcurrencyChanged));
        _logger?.LogInformation("Concurrency adjusted to {Concurrency} due to CPU usage: {CpuUsage:F2}%",
            _currentConcurrency, cpuUsage);
    }

    private void ReleaseOrReserve()
    {
        while (true)
        {
            var debt = Volatile.Read(ref _pendingReservationDebt);
            if (debt <= 0)
            {
                _semaphore.Release();
                return;
            }

            if (Interlocked.CompareExchange(ref _pendingReservationDebt, debt - 1, debt) == debt)
            {
                Interlocked.Increment(ref _reservedSlots);
                return;
            }
        }
    }

    private async Task ExecuteWithRetryAsync(Func<Task> workItem, CancellationToken ct)
    {
        for (var attempts = 0; ; )
        {
            try
            {
                await workItem().ConfigureAwait(false);
                var completed = Interlocked.Increment(ref _totalCompleted);
                InvokeObserver(OnCompleted, completed, nameof(OnCompleted));
                RaiseProgress();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempts++;
                _logger?.LogWarning(ex, "Task attempt {Attempt} failed", attempts);

                var retryPolicy = Volatile.Read(ref _retryPolicy);
                var isFatal = ex is OutOfMemoryException or StackOverflowException;
                var shouldRetry = !isFatal && retryPolicy.Mode switch
                {
                    RetryMode.None => false,
                    RetryMode.RetryXTimes => attempts <= retryPolicy.MaxRetries,
                    RetryMode.ExponentialBackoff => attempts <= retryPolicy.MaxRetries,
                    _ => false
                };

                if (!shouldRetry)
                {
                    lock (_lock)
                    {
                        _exceptions.Add(ex);
                    }

                    Interlocked.Increment(ref _totalFailed);
                    InvokeObserver(OnError, ex, nameof(OnError));
                    _logger?.LogError(ex, "Task failed permanently after {Attempts} attempts", attempts);
                    RaiseProgress();
                    return;
                }

                await Task.Delay(retryPolicy.GetDelay(attempts), ct).ConfigureAwait(false);
            }
        }
    }

    internal int ReservedSlotsForTesting => _reservedSlots;

    internal int PendingReservationDebtForTesting => _pendingReservationDebt;

    internal int CurrentConcurrencyForTesting => _currentConcurrency;

    internal int SemaphoreCurrentCountForTesting => _semaphore.CurrentCount;

    internal void AdjustConcurrencyForTesting(int newConcurrency, float cpuUsage)
    {
        AdjustConcurrency(newConcurrency, cpuUsage);
    }

    private void RaiseProgress()
    {
        InvokeObserver(OnProgress, ProgressPercent, nameof(OnProgress));
    }

    private void ThrowIfNotActive()
    {
        if (Volatile.Read(ref _disposeState) != DisposeStateActive)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void PruneCompletedTasks()
    {
        lock (_lock)
        {
            _tasks.RemoveAll(static task => task.IsCompleted);
        }
    }

    private void InvokeObserver<T>(Action<T>? observer, T value, string eventName)
    {
        if (observer == null)
        {
            return;
        }

        foreach (Action<T> handler in observer.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                // Fail loudly even when no logger was configured (the common case) - a
                // subscriber's exception must never be discarded with zero observability.
                if (_logger != null)
                {
                    _logger.LogWarning(ex, "Observer {EventName} failed", eventName);
                }
                else
                {
                    Trace.TraceWarning($"Observer {eventName} failed: {ex}");
                }
            }
        }
    }
}
