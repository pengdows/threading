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

        var task = Task.Run(async () =>
        {
            await _semaphore.WaitAsync(_cancellationToken).ConfigureAwait(false);
            try
            {
                var attempts = 0;
                while (true)
                {
                    try
                    {
                        await workItem().ConfigureAwait(false);
                        var completed = Interlocked.Increment(ref _totalCompleted);
                        OnCompleted?.Invoke(completed);
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempts++;
                        _logger?.LogWarning(ex, "Task attempt {Attempt} failed", attempts);

                        if (_retryPolicy.Mode == RetryMode.None ||
                            (_retryPolicy.Mode == RetryMode.RetryXTimes && attempts > _retryPolicy.MaxRetries))
                        {
                            lock (_lock) _exceptions.Add(ex);
                            OnError?.Invoke(ex);
                            _logger?.LogError(ex, "Task failed permanently after {Attempts} attempts", attempts);
                            break;
                        }

                        await Task.Delay(_retryPolicy.RetryDelay, _cancellationToken).ConfigureAwait(false);
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
            OnQueued?.Invoke(queued);
        }
    }

    public async Task WaitForAllAsync()
    {
        Task[] snapshot;
        lock (_lock) snapshot = _tasks.ToArray();
        await Task.WhenAll(snapshot).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _internalCts.Cancel();
        if (_monitorTask != null) await _monitorTask.ConfigureAwait(false);

        await _semaphore.WaitAsync().ConfigureAwait(false);
        _semaphore.Release();
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
                _currentConcurrency = newConcurrency;
                OnConcurrencyChanged?.Invoke(_currentConcurrency);
                _logger?.LogInformation("Concurrency adjusted to {Concurrency} due to CPU usage: {CpuUsage:F2}%",
                    _currentConcurrency, cpuUsage);
            }
        }
    }
}