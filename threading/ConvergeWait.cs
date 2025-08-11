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

    private int _currentConcurrency;
    private int _desiredConcurrency;
    private readonly object _scaleLock = new();
    private volatile bool _disposed;
    private Task? _monitorTask;
    private RetryPolicy _retryPolicy = RetryPolicy.None;
    private int _totalCompleted = 0;
    private int _totalQueued;

    public event Action<int>? OnQueued;
    public event Action<int>? OnCompleted;
    public event Action<Exception>? OnError;
    public event Action<int>? OnConcurrencyChanged;

    public int TotalQueued => _totalQueued;

    public int TotalCompleted => _totalCompleted;

    public int TotalFailed => _exceptions.Count;
    public bool HasFailures => _exceptions.Count > 0;
    public IReadOnlyList<Exception> Exceptions => _exceptions.AsReadOnly();

    public ConvergeWait(int maxConcurrency,
        AdaptiveConfig? adaptiveConfig = null,
        CancellationToken cancellationToken = default,
        ILogger<ConvergeWait>? logger = null)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        _cancellationToken = cancellationToken;
        _adaptiveConfig = adaptiveConfig;
        _currentConcurrency = maxConcurrency;
        _desiredConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _logger = logger;

        if (_adaptiveConfig != null)
        {
            _monitorTask = Task.Run(() => MonitorCpuLoadAsync(), _internalCts.Token);
        }
    }

    public void SetRetryPolicy(RetryPolicy policy)
    {
        _retryPolicy = policy;
    }


    public void Queue(Func<Task> workItem)
    {
        if (workItem == null) throw new ArgumentNullException(nameof(workItem));
        if (_disposed) throw new ObjectDisposedException(nameof(ConvergeWait));

        var task = Task.Run(async () =>
        {
            await _semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                var attempts = 0;
                for (;;)
                {
                    try
                    {
                        await workItem().ConfigureAwait(false);
                        var completed = Interlocked.Increment(ref _totalCompleted);
                        InvokeSafely(OnCompleted, completed);
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempts++;
                        _logger?.LogWarning(ex, "Task attempt {Attempt} failed", attempts);

                        var shouldRetry = ShouldRetry(ex) &&
                                         (_retryPolicy.Mode != RetryMode.RetryXTimes ||
                                          attempts <= _retryPolicy.MaxRetries);
                        if (!shouldRetry || _retryPolicy.Mode == RetryMode.None)
                        {
                            lock (_lock)
                            {
                                _exceptions.Add(ex);
                            }
                            InvokeSafely(OnError, ex);
                            _logger?.LogError(ex, "Task failed permanently after {Attempts} attempts", attempts);
                            break;
                        }

                        var delay = ComputeDelay(_retryPolicy.RetryDelay, attempts);
                        await Task.Delay(delay, _cancellationToken).ConfigureAwait(false);
                    }
                }
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
            InvokeSafely(OnQueued, queued);
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

        lock (_lock)
        {
            _tasks.RemoveAll(static t => t.IsCompleted);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _internalCts.Cancel();
        if (_monitorTask != null)
        {
            await _monitorTask.ConfigureAwait(false);
        }

        await WaitForAllAsync().ConfigureAwait(false);

        _semaphore.Dispose();
        _internalCts.Dispose();
    }

    private async Task MonitorCpuLoadAsync()

    {
        var sw = Stopwatch.StartNew();
        var lastTotalCpu = Process.GetCurrentProcess().TotalProcessorTime;

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
                ApplyScale(newConcurrency);
                _logger?.LogInformation("Concurrency adjusted to {Concurrency} due to CPU usage: {CpuUsage:F2}%",
                    _currentConcurrency, cpuUsage);
            }
        }
    }

    private void ApplyScale(int newConcurrency)
    {
        lock (_scaleLock)
        {
            var delta = newConcurrency - _desiredConcurrency;
            _desiredConcurrency = newConcurrency;
            if (delta > 0)
            {
                _semaphore.Release(delta);
            }
            else if (delta < 0)
            {
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < -delta && !_internalCts.IsCancellationRequested; i++)
                    {
                        await _semaphore.WaitAsync(_internalCts.Token).ConfigureAwait(false);
                    }
                }, _internalCts.Token);
            }

            _currentConcurrency = newConcurrency;
            InvokeSafely(OnConcurrencyChanged, _currentConcurrency);
        }
    }

    private static bool ShouldRetry(Exception ex)
    {
        return ex is not (OperationCanceledException or TaskCanceledException or OutOfMemoryException or StackOverflowException);
    }

    private static TimeSpan ComputeDelay(TimeSpan baseDelay, int attempt)
    {
        var multiplier = Math.Pow(2, attempt - 1);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)baseDelay.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * multiplier) + jitter;
    }

    private void InvokeSafely(Action<int>? handler, int arg)
    {
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(arg);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Event handler threw");
        }
    }

    private void InvokeSafely(Action<Exception>? handler, Exception ex)
    {
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(ex);
        }
        catch (Exception log)
        {
            _logger?.LogError(log, "Event handler threw");
        }
    }
}