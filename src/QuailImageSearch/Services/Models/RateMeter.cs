using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace QuailImageSearch.Services.Models;

/// <summary>Items per second over a sliding time window (for progress text), rather than since the start.</summary>
public sealed class RateMeter(TimeSpan window)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(TimeSpan Time, long Done)> _samples = [(TimeSpan.Zero, 0)];

    /// <summary>Records that <paramref name="done"/> items have been processed in total so far.</summary>
    public void Add(long done)
    {
        var now = _clock.Elapsed;
        _samples.Add((now, done));
        // Keep one sample at or before the window start, so the rate spans the whole window.
        var start = now - window;
        var drop = 0;
        while (drop + 1 < _samples.Count - 1 && _samples[drop + 1].Time <= start) drop++;
        _samples.RemoveRange(0, drop);
    }

    /// <summary>Items per second over the window (or since the start, early on).</summary>
    public double Rate
    {
        get
        {
            var (first, last) = (_samples[0], _samples[^1]);
            var seconds = (last.Time - first.Time).TotalSeconds;
            return seconds > 0 ? (last.Done - first.Done) / seconds : 0;
        }
    }
}
