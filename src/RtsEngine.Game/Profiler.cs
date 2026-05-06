using System.Diagnostics;
using System.Text;

namespace RtsEngine.Game;

/// <summary>
/// Lightweight per-frame profiler. Counts and sums elapsed time for named
/// scopes; once per frame, a snapshot is published so the host can render or
/// clipboard it. Disabled by default so it costs nothing in shipping builds —
/// every <see cref="Scope"/> call short-circuits to a no-op until
/// <see cref="Enabled"/> is set true (typically via the F3 toggle in
/// <see cref="GameEngine"/>).
///
/// Designed to be used as <c>using (Profiler.Scope("foo")) { ... }</c>. The
/// inner <see cref="ScopeHandle"/> is a struct so a no-op scope is a free
/// stack value with no heap alloc.
/// </summary>
public static class Profiler
{
    public static bool Enabled { get; set; }

    private sealed class Sample
    {
        public string Name = string.Empty;
        public long TicksThisFrame;
        public int CountThisFrame;
        // Snapshotted at EndFrame so readers see a consistent picture even
        // while the next frame is mid-flight.
        public long TicksLast;
        public int CountLast;
    }

    private static readonly Dictionary<string, Sample> _samples = new();
    private static long _frameStartTicks;
    private static long _frameTicksLast;
    private static int _frameCount;
    private static double _frameMsRolling; // EMA of frame ms, for display smoothing

    public static void BeginFrame()
    {
        if (!Enabled) return;
        foreach (var s in _samples.Values) { s.TicksThisFrame = 0; s.CountThisFrame = 0; }
        _frameStartTicks = Stopwatch.GetTimestamp();
    }

    public static void EndFrame()
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        _frameTicksLast = now - _frameStartTicks;
        _frameCount++;
        double ms = _frameTicksLast * 1000.0 / Stopwatch.Frequency;
        // EMA so the displayed total doesn't jitter wildly per frame.
        _frameMsRolling = _frameMsRolling == 0 ? ms : _frameMsRolling * 0.9 + ms * 0.1;
        foreach (var s in _samples.Values)
        {
            s.TicksLast = s.TicksThisFrame;
            s.CountLast = s.CountThisFrame;
        }
    }

    /// <summary>
    /// Open a scope named <paramref name="name"/>. The returned handle stops
    /// timing when disposed. Returns a default (no-op) handle when disabled,
    /// so the call site has zero hot-path overhead in release builds.
    /// </summary>
    public static ScopeHandle Scope(string name)
    {
        if (!Enabled) return default;
        return new ScopeHandle(name, Stopwatch.GetTimestamp());
    }

    private static void Record(string name, long elapsedTicks)
    {
        if (!_samples.TryGetValue(name, out var s))
        {
            s = new Sample { Name = name };
            _samples[name] = s;
        }
        s.TicksThisFrame += elapsedTicks;
        s.CountThisFrame++;
    }

    /// <summary>
    /// Render a multi-line text snapshot of the most-recent completed frame.
    /// Sorted by descending time so the worst offenders rise to the top.
    /// </summary>
    public static string Snapshot()
    {
        var sb = new StringBuilder(1024);
        double freq = Stopwatch.Frequency;
        double frameMs = _frameTicksLast * 1000.0 / freq;
        double fps = frameMs > 0 ? 1000.0 / frameMs : 0;
        sb.Append("Frame ").Append(_frameCount).Append(": ");
        sb.Append(frameMs.ToString("F2")).Append("ms (")
          .Append(fps.ToString("F0")).Append(" fps");
        if (_frameMsRolling > 0)
            sb.Append(", avg ").Append(_frameMsRolling.ToString("F2")).Append("ms");
        sb.AppendLine(")");
        sb.AppendLine("─────────────────────────────────");

        // Snapshot to a list so we can sort by time (descending).
        var rows = new List<(string name, long ticks, int count)>(_samples.Count);
        foreach (var s in _samples.Values)
            if (s.CountLast > 0) rows.Add((s.Name, s.TicksLast, s.CountLast));
        rows.Sort((a, b) => b.ticks.CompareTo(a.ticks));

        foreach (var r in rows)
        {
            double ms = r.ticks * 1000.0 / freq;
            double pct = frameMs > 0 ? ms / frameMs * 100.0 : 0;
            // Pad name to a fixed column so the numbers align.
            string name = r.name.Length >= 28 ? r.name[..28] : r.name.PadRight(28);
            sb.Append(name)
              .Append("  ").Append(r.count.ToString().PadLeft(4)).Append('×')
              .Append(' ').Append(ms.ToString("F2").PadLeft(7)).Append("ms")
              .Append(' ').Append('(').Append(pct.ToString("F1").PadLeft(4)).Append("%)")
              .AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Disposable returned from <see cref="Scope"/>. Struct + zero
    /// fields when disabled means the call is a no-op.</summary>
    public readonly struct ScopeHandle : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        internal ScopeHandle(string name, long start) { _name = name; _start = start; }
        public void Dispose()
        {
            if (_name == null) return;
            Record(_name, Stopwatch.GetTimestamp() - _start);
        }
    }
}
