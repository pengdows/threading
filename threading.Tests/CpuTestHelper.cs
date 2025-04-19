using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace pengdows.threading.Tests;

public class CpuTestHelper
{
    public static Task BurnCpu(TimeSpan duration)
    {
        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration)
            {
                // Perform useless work to keep the CPU busy
                double x = 0;
                for (int i = 0; i < 100_000; i++)
                {
                    x += Math.Sqrt(i) * Math.PI / Math.E;
                }
            }
        });
    }
}