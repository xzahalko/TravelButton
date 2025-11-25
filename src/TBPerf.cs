using System.Diagnostics;
using UnityEngine;

static class TBPerf
{
    public static Stopwatch StartTimer() { var s = new Stopwatch(); s.Start(); return s; }
    public static void Log(string tag, Stopwatch sw, string extra = "")
    {
        sw.Stop();
        TBLog.Info($"[PERF] {tag} took {sw.Elapsed.TotalMilliseconds:F1} ms {extra}");
    }
}