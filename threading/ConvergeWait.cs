using Microsoft.Extensions.Logging;

namespace pengdows.threading;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private int _currentConcurrency;
    private int _reservedSlots; // Slots held to reduce effective concurrency
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
    public IReadOnlyList<Exception> Exceptions => _exceptions.AsReadOnly();
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
        _retryPolicy = policy;
    }


    public void Queue(Func<Task> workItem)
    {
        if (workItem == null)
        {
            throw new ArgumentNullException(nameof(workItem));
        }

        var task = Task.Run(async () =>
        {
            await _semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteWithRetryAsync(workItem, _cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _semaphore.Release();
            }
        }, _cancellationToken);

        lock (_lock)
        {
            _tasks.Add(task);
            var queued = Interlocked.Increment(ref _totalQueued);
            OnQueued?.Invoke(queued);
            RaiseProgress();
        }
    }

    public async Task QueueAsync(Func<Task> workItem, CancellationToken ct = default)
    {
        if (workItem == null)
        {
            throw new ArgumentNullException(nameof(workItem));
        }

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, ct);

        try
        {
            await _semaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch
        {
            linkedCts.Dispose();
            throw;
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
                _semaphore.Release();
            }
        }, CancellationToken.None);

        lock (_lock)
        {
            _tasks.Add(task);
            var queued = Interlocked.Increment(ref _totalQueued);
            OnQueued?.Invoke(queued);
            RaiseProgress();
        }
    }

    public async Task WaitForAllAsync()
    {
        Task[] snapshot;
        lock (_lock)
        {
            snapshot = _tasks.ToArray();
        }
        await Task.WhenAll(snapshot).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _internalCts.Cancel();
        if (_monitorTask != null)
        {
            await _monitorTask.ConfigureAwait(false);
        }

        // Release any reserved slots before disposing
        var reserved = Interlocked.Exchange(ref _reservedSlots, 0);
        if (reserved > 0)
        {
            _semaphore.Release(reserved);
        }

        _semaphore.Dispose();
        _internalCts.Dispose();
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
            // Scaling UP: release reserved slots or add new capacity
            var toRelease = Math.Min(delta, _reservedSlots);
            if (toRelease > 0)
            {
                _semaphore.Release(toRelease);
                Interlocked.Add(ref _reservedSlots, -toRelease);
            }

            // If we need more capacity than reserved slots, release additional
            var additional = delta - toRelease;
            if (additional > 0 && _semaphore.CurrentCount + additional <= _maxConcurrency)
            {
                _semaphore.Release(additional);
            }
        }
        else if (delta < 0)
        {
            // Scaling DOWN: acquire slots to hold them (non-blocking attempt)
            var toAcquire = -delta;
            for (var i = 0; i < toAcquire; i++)
            {
                if (_semaphore.Wait(0)) // Non-blocking acquire
                {
                    Interlocked.Increment(ref _reservedSlots);
                }
                else
                {
                    // Can't acquire immediately - will naturally throttle as tasks complete
                    break;
                }
            }
        }

        _currentConcurrency = newConcurrency;
        OnConcurrencyChanged?.Invoke(_currentConcurrency);
        _logger?.LogInformation("Concurrency adjusted to {Concurrency} due to CPU usage: {CpuUsage:F2}%",
            _currentConcurrency, cpuUsage);
    }

    private async Task ExecuteWithRetryAsync(Func<Task> workItem, CancellationToken ct)
    {
        for (var attempts = 0; ; )
        {
            try
            {
                await workItem().ConfigureAwait(false);
                var completed = Interlocked.Increment(ref _totalCompleted);
                OnCompleted?.Invoke(completed);
                RaiseProgress();
                return;
            }
            catch (Exception ex)
            {
                attempts++;
                _logger?.LogWarning(ex, "Task attempt {Attempt} failed", attempts);

                var shouldRetry = _retryPolicy.Mode switch
                {
                    RetryMode.None => false,
                    RetryMode.RetryXTimes => attempts <= _retryPolicy.MaxRetries,
                    RetryMode.ExponentialBackoff => attempts <= _retryPolicy.MaxRetries,
                    _ => false
                };

                if (!shouldRetry)
                {
                    lock (_lock)
                    {
                        _exceptions.Add(ex);
                    }

                    Interlocked.Increment(ref _totalFailed);
                    OnError?.Invoke(ex);
                    _logger?.LogError(ex, "Task failed permanently after {Attempts} attempts", attempts);
                    RaiseProgress();
                    return;
                }

                await Task.Delay(_retryPolicy.GetDelay(attempts), ct).ConfigureAwait(false);
            }
        }
    }

    private void RaiseProgress()
    {
        OnProgress?.Invoke(ProgressPercent);
    }
}
