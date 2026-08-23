# ConvergeWait (original CodeProject article, archived)

> **Preservation note:** This is a verbatim archive of the original article that introduced
> `ConvergeWait`, preserved here so the design intent behind this library's name and core
> promise isn't lost if the source ever goes offline. Retrieved from the Wayback Machine
> snapshot of the live article (CodeProject's long-term status has been uncertain).
>
> - **Original title:** ConvergeWait
> - **Author:** Alaric Dailey
> - **First posted:** 9 Jan 2010 (article page also shows 18 Jan 2010)
> - **License:** [The Code Project Open License (CPOL)](http://www.codeproject.com/info/cpol10.aspx)
> - **Original URL:** <https://www.codeproject.com/KB/threads/ConvergeWait.aspx>
> - **Archived snapshot used:** <https://web.archive.org/web/20110829091445/http://www.codeproject.com/KB/threads/ConvergeWait.aspx>
> - **Abstract (as published):** "Wait for multiple threads to complete."
>
> The code below is transcribed exactly as published, including one inconsistency in the
> original: the prose says the class inherits the base `System.Threading.WaitHandle`, but the
> code actually inherits `EventWaitHandle` (the non-sealed parent of the sealed
> `ManualResetEvent`). That's left as-is for fidelity to the source.

---

## Introduction

This is the second article in my series. This time, I want to demonstrate something that I have found very useful. A thread-synchronization object that is very different than anything .NET provides.

## Background

.NET offers all sorts of thread control objects. Semaphores, Mutexes, ReaderWriterLocks, CriticalSections, and other thread control objects have one thing in common: they limit the number of threads that can run at a single time. There is no object that allows you to wait for several threads to finish. About the closest you can get is to use `Thread.Join` like this:

```csharp
private void Form1_Load(object sender, EventArgs e)
{
    List listOfThreads = new List();
    for (int i = 0; i < 10; i++)
    {
        Thread t = new Thread(ThreadFunc);
        t.Start();
        listOfThreads.Add(t);
    }
    foreach(Thread t in listOfThreads)
    {
        t.Join();
    }
    //when we get here ALL threads have finished.
}
private void ThreadFunc()
{
    Thread.Sleep(5000);
}
```

Of course, there are a few things to notice here:

- This approach will simply not work when using `ThreadPool` threads since `Thread.Join` **never** returns from those threads.
- It will try to join, even if the thread has exited.
- It just looks messy.

Of course, you could use a `ManualResetEvent`, but you now have to keep track of how many threads are supposed to be running, decrement the value in a thread-safe way, and finally `Set` the event when the count reaches zero.

```csharp
private const Int32 MaxThreads = 100;
private Int64 _runningThreads = MaxThreads;
private readonly ManualResetEvent _event = new ManualResetEvent(false);
private void Form1_Load(object sender, EventArgs e)
{
    for (int i = 0; i < MaxThreads; i++)
    {
        ThreadPool.QueueUserWorkItem(ThreadFunc, null);
    }
    _event.WaitOne();

}
private void ThreadFunc(object state)
{
    try
    {
        Thread.Sleep(5000);
    }
    finally
    {
        Int64 x = Interlocked.Decrement(ref _runningThreads);
        Debug.WriteLine(x + " threads still running");
        if (x == 0)
        {
            _event.Set();
        }
    }
}
```

The above code will indeed satisfy the requirements, but I prefer to make things more object oriented. Specifically, I want to:

1. Encapsulate the values, to make sure the critical values can't be changed when it's not appropriate.
2. Simplify the code required in the `finally` block, which means overriding the `Set` and `Reset` functions.
3. Enforce everything I can.

To do this, my initial thought was to inherit the `ManualResetEvent`. However, it is a `sealed` class, so I decided to inherit the base class myself, which happens to be `System.Threading.WaitHandle`. This is the final code for what I call a `ConvergeWait`:

```csharp
/// <summary>
/// Class to wait syncronize a number of threads.
/// </summary>
public class ConvergeWait : EventWaitHandle
{
    private Int32 _currentWaitingCount = 0;
    private Int32 _totalThreadsToWaitOn = 1;
    private bool _isReset = false;

    public ConvergeWait(Int32 numberOfThreads)
        : base(false, EventResetMode.ManualReset)
    {
        if (numberOfThreads < 1)
        {
            throw new ArgumentOutOfRangeException("numberOfThreads",
                      "number of threads must be greater than 0");
        }
        _totalThreadsToWaitOn = numberOfThreads;
    }

    public bool IsReset
    {
        get { return _isReset; }
    }

    public bool Reset(Int32 numberOfThreads)
    {
        if (_isReset)
        {
            if (numberOfThreads != _totalThreadsToWaitOn)
            {
                throw new Exception("already in reset state, " +
                          "you may not call 'Reset' again.");
            }
        }
        _isReset = true;
        _totalThreadsToWaitOn = numberOfThreads;
        Interlocked.Exchange(ref _currentWaitingCount, 0);
        return base.Reset();
    }

    public new bool Reset()
    {
        return Reset(_totalThreadsToWaitOn);
    }

    public new void Set()
    {
        if (!_isReset)
        {
            base.Set();
            _isReset = false;
            Interlocked.Exchange(ref _currentWaitingCount, _totalThreadsToWaitOn);
            return;
        }
        int i = Interlocked.Increment(ref _currentWaitingCount);
        Debug.WriteLine(string.Format("released thread count = {0}", i));
        if (i == _totalThreadsToWaitOn)
        {
            base.Set();
            _isReset = false;
        }
    }
}
```

## Using the Code

This object is safer and cleaner to use. Since this example is so small, it doesn't really show it too well. Here is a working example.

```csharp
private const Int32 MaxThreads = 100;

private void Form1_Load(object sender, EventArgs e)
{
    ConvergeWait convergeWait = new ConvergeWait(MaxThreads);

    for (int i = 0; i < MaxThreads; i++)
    {
        ThreadPool.QueueUserWorkItem(ThreadFunc, convergeWait);
    }
    convergeWait.WaitOne();

}
private void ThreadFunc(object state)
{
    ConvergeWait convergeWait = (ConvergeWait) state;
    try
    {
        Thread.Sleep(5000);
    }
    finally
    {
        convergeWait.Set();
    }
}
```

## Points of Interest

One of the nice features about the `ConvergeWait` is that if the threaded function is somewhere other than the class where you are creating the threads, only the `ConvergeWait` has to be passed. If you tried to use the code from my first attempt, you would have to pass a lot more data, adding significant complexity.

## History

None yet.

---

## Editorial note: how this relates to the current `pengdows.threading` library

The class in this repository keeps the name and the central promise (wait for a set of
independently-running units of work to finish), but the implementation has moved on
completely: `Task`/`async`-`await` and `SemaphoreSlim` instead of raw `Thread` objects and a
`WaitHandle` subclass (TPL didn't exist yet when this article was written - it shipped in
.NET 4, a year later). It has also grown well beyond the original's single job: this version
additionally bounds concurrency, retries failed work, and adaptively throttles based on CPU -
concerns the original article explicitly said were *already* solved by existing .NET
primitives (Semaphore, Mutex, etc.) and out of scope for `ConvergeWait` itself. See the git
history and `threading/ConvergeWait.cs` for the current design.
