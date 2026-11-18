using System.Diagnostics;

namespace WoWAddonLab.Diagnostics;

// Set WOWADDONLAB_FRAME_PROFILE=<file> to record per-frame phase timings; written on exit.
internal static class FrameProfiler
{
    private static readonly string? OutputPath = Environment.GetEnvironmentVariable("WOWADDONLAB_FRAME_PROFILE");
    private static readonly Dictionary<string, double> Current = [];
    private static readonly List<string> Lines = [];
    private static long _frameStart = Stopwatch.GetTimestamp();
    private static TimeSpan _gcPause;
    private static int _gen2;

    public static bool Enabled => OutputPath is not null;

    public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void Stop(string phase, long start)
    {
        if (!Enabled)
            return;
        Current[phase] = Current.GetValueOrDefault(phase) + Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public static void EndFrame()
    {
        if (!Enabled)
            return;
        var now = Stopwatch.GetTimestamp();
        var total = Stopwatch.GetElapsedTime(_frameStart, now).TotalMilliseconds;
        _frameStart = now;
        var pause = GC.GetTotalPauseDuration();
        Current["gc"] = (pause - _gcPause).TotalMilliseconds;
        Current["gen2"] = GC.CollectionCount(2) - _gen2;
        _gcPause = pause;
        _gen2 = GC.CollectionCount(2);
        Lines.Add($"{total:0.0}\t" +string.Join('\t', Current.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value:0.0}")));
        Current.Clear();
    }

    public static void Write()
    {
        if (OutputPath is not null)
            File.WriteAllLines(OutputPath, Lines);
    }
}
