// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Export;

public enum ExportItemState { Waiting, Running, Done, Failed, Cancelled }

/// <summary>A queued export. Mutated only by <see cref="ExportQueue"/>; its events carry the item after each change.</summary>
public sealed class ExportItem(ExportJob job, string title)
{
    internal readonly CancellationTokenSource Cts = new();

    public ExportJob Job { get; } = job;
    public string Title { get; } = title;
    public ExportItemState State { get; internal set; }
    public double Fraction { get; internal set; }
    public string Stage { get; internal set; } = "Waiting";
    public ExportResult? Result { get; internal set; }
    public Exception? Error { get; internal set; }
}

/// <summary>
/// Runs exports one at a time, in order, independent of any page. <see cref="Changed"/> fires on a worker thread: listeners marshal to
/// their own. One export at a time because each is already parallel inside ffmpeg, and two would fight over the disk and the GPU encoder.
/// </summary>
public sealed class ExportQueue(Func<ExportJob, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>>? runner = null)
{
    private readonly Func<ExportJob, IProgress<ExportProgress>, CancellationToken, Task<ExportResult>> _runner = runner ?? ExportService.RunAsync;
    private readonly List<ExportItem> _items = [];
    private readonly object _gate = new();
    private Task _pump = Task.CompletedTask;
    private bool _pumping;

    public event Action<ExportItem>? Changed;

    public IReadOnlyList<ExportItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public int Pending
    {
        get { lock (_gate) return _items.Count(i => i.State is ExportItemState.Waiting or ExportItemState.Running); }
    }

    /// <summary>The whole queue's progress 0..1: finished items count fully. Null when nothing is pending.</summary>
    public double? OverallFraction
    {
        get
        {
            lock (_gate)
            {
                var active = _items.Where(i => i.State is ExportItemState.Waiting or ExportItemState.Running).ToList();
                return active.Count == 0 ? null : active.Sum(i => i.Fraction) / active.Count;
            }
        }
    }

    public ExportItem Enqueue(ExportJob job, string title)
    {
        var item = new ExportItem(job, title);
        lock (_gate)
        {
            _items.Add(item);
            if (!_pumping)
            {
                _pumping = true;
                _pump = Task.Run(PumpAsync);
            }
        }
        Changed?.Invoke(item);
        return item;
    }

    /// <summary>Cancels a waiting or running item; finished items are left as they are.</summary>
    public void Cancel(ExportItem item)
    {
        bool waiting;
        lock (_gate)
        {
            if (item.State is not (ExportItemState.Waiting or ExportItemState.Running)) return;
            waiting = item.State == ExportItemState.Waiting;
            if (waiting) { item.State = ExportItemState.Cancelled; item.Stage = "Cancelled"; }
        }
        item.Cts.Cancel();
        if (waiting) Changed?.Invoke(item);
    }

    public void CancelAll()
    {
        foreach (var item in Items) Cancel(item);
    }

    /// <summary>Completes when the queue has run dry (tests).</summary>
    public Task IdleAsync()
    {
        lock (_gate) return _pump;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            ExportItem? item;
            lock (_gate)
            {
                item = _items.FirstOrDefault(i => i.State == ExportItemState.Waiting);
                if (item is null) { _pumping = false; return; }
                item.State = ExportItemState.Running;
                item.Stage = "Starting";
            }
            Changed?.Invoke(item);
            await RunOneAsync(item).ConfigureAwait(false);
            Changed?.Invoke(item);
        }
    }

    private async Task RunOneAsync(ExportItem item)
    {
        ExportService.RemoveStaleWork(item.Job.OutputFolder);       // nothing else runs now: anything left there is from a killed app
        try
        {
            var progress = new InlineProgress(p =>
            {
                item.Fraction = p.Fraction;
                item.Stage = p.Stage;
                Changed?.Invoke(item);
            });
            var result = await _runner(item.Job, progress, item.Cts.Token).ConfigureAwait(false);
            item.Result = result;
            item.Fraction = 1;
            item.Stage = "Done";
            item.State = ExportItemState.Done;
        }
        catch (OperationCanceledException)
        {
            item.Stage = "Cancelled";
            item.State = ExportItemState.Cancelled;
        }
        catch (Exception e)                                         // an export's failure is that item's result, never the queue's end
        {
            item.Error = e;
            item.Stage = "Failed";
            item.State = ExportItemState.Failed;
        }
    }

    private sealed class InlineProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
