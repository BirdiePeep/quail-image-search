using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace QuailImageSearch.Services.Models;

public static class Prefetch
{
    /// <summary>
    /// Runs <paramref name="work"/> on the items in order, up to <paramref name="depth"/> items ahead
    /// of the consumer, and yields each item with its result and how long the consumer waited for it.
    /// Used to decode the next batches of images while the current one is embedded.
    /// </summary>
    public static IEnumerable<(TIn Item, TOut Result, TimeSpan Waited)> Run<TIn, TOut>(
        IReadOnlyList<TIn> items, Func<TIn, TOut> work, int depth, CancellationToken ct)
    {
        var pending = new Queue<Task<TOut>>();
        var next = 0;
        void Fill()
        {
            while (next < items.Count && pending.Count < depth)
            {
                var item = items[next++];
                pending.Enqueue(Task.Run(() => work(item), ct));
            }
        }
        try
        {
            Fill();
            for (var i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                var result = pending.Dequeue().GetAwaiter().GetResult();
                var waited = sw.Elapsed;
                Fill();
                yield return (items[i], result, waited);
            }
        }
        finally
        {
            // Let work that's still running finish before the caller releases what it uses (e.g. the model).
            foreach (var t in pending)
                try { t.Wait(); } catch (Exception) { /* cancelled or failed; nobody needs the result */ }
        }
    }
}
