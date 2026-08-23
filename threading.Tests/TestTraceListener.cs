using System.Collections.Concurrent;
using System.Diagnostics;

namespace pengdows.threading.Tests;

public sealed class TestTraceListener : TraceListener
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public override void Write(string message)
    {
        if (message != null)
        {
            Messages.Enqueue(message);
        }
    }

    public override void WriteLine(string message)
    {
        if (message != null)
        {
            Messages.Enqueue(message);
        }
    }
}
