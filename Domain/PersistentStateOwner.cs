using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace BDVM.Domain;

// Admission never waits on Unity. Once a transaction starts it completes with a
// receipt or an error; cancellation cannot hide a side effect already committed.
public sealed class PersistentStateOwner : IDisposable
{
    private abstract class Work
    {
        public int Bytes;
        public long AdmittedAt;
        public CancellationToken Cancellation;
        public abstract void Execute(PersistentJournal journal);
        public abstract void Fail(Exception exception);
    }
    private sealed class Work<T> : Work
    {
        public Func<PersistentJournal, T> Action = null!;
        public readonly TaskCompletionSource<T> Completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Execute(PersistentJournal journal)
        { try { Completion.TrySetResult(Action(journal)); } catch (Exception exception) { Fail(exception); } }
        public override void Fail(Exception exception) => Completion.TrySetException(exception);
    }
    private readonly object admission = new object();
    private readonly BlockingCollection<Work> queue;
    private readonly int maximumBytes;
    private readonly double maximumAgeSeconds;
    private readonly TaskCompletionSource<bool> stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int pendingBytes;
    private bool closing;
    private bool cancelPending;
    public Task Completion => stopped.Task;
    public int PendingBytes { get { lock (admission) return pendingBytes; } }
    public int PendingCount => queue.Count;

    public PersistentStateOwner(Func<PersistentJournal> create, int maximumQueued = 128, int maximumBytes = 16 * 1024 * 1024, double maximumAgeSeconds = 10)
    {
        if (create == null) throw new ArgumentNullException(nameof(create));
        if (maximumQueued <= 0 || maximumBytes <= 0 || maximumAgeSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(maximumQueued));
        queue = new BlockingCollection<Work>(maximumQueued);
        this.maximumBytes = maximumBytes; this.maximumAgeSeconds = maximumAgeSeconds;
        new Thread(() => Run(create)) { IsBackground = true, Name = "BDVM persistent state owner" }.Start();
    }
    public Task<T> Enqueue<T>(Func<PersistentJournal, T> action, int payloadBytes = 0, CancellationToken cancellationToken = default)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (payloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        var work = new Work<T> { Action = action, Bytes = payloadBytes, AdmittedAt = Stopwatch.GetTimestamp(), Cancellation = cancellationToken };
        lock (admission)
        {
            if (closing) work.Fail(new ObjectDisposedException(nameof(PersistentStateOwner)));
            else if (cancellationToken.IsCancellationRequested) work.Fail(new OperationCanceledException(cancellationToken));
            else if (payloadBytes > maximumBytes - pendingBytes || !queue.TryAdd(work)) work.Fail(new InvalidOperationException("Persistent state queue is full; retry with the same operation ID."));
            else pendingBytes += payloadBytes;
        }
        return work.Completion.Task;
    }
    public Task StopAsync(bool drain = true)
    {
        lock (admission)
        {
            if (!drain) cancelPending = true;
            if (!closing) { closing = true; queue.CompleteAdding(); }
        }
        return Completion;
    }
    private void Run(Func<PersistentJournal> create)
    {
        Exception? failure = null;
        try
        {
            using (var journal = create())
                foreach (var work in queue.GetConsumingEnumerable())
                {
                    bool cancelled;
                    lock (admission) { pendingBytes -= work.Bytes; cancelled = cancelPending; }
                    if (cancelled || work.Cancellation.IsCancellationRequested) work.Fail(new OperationCanceledException("Persistent state command cancelled before execution."));
                    else if ((Stopwatch.GetTimestamp() - work.AdmittedAt) / (double)Stopwatch.Frequency > maximumAgeSeconds)
                        work.Fail(new TimeoutException("Persistent state command expired before execution."));
                    else work.Execute(journal);
                }
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            lock (admission)
            {
                closing = true; queue.CompleteAdding();
                while (queue.TryTake(out var pending)) { pendingBytes -= pending.Bytes; pending.Fail(failure ?? new OperationCanceledException("Persistent state owner stopped.")); }
            }
            if (failure == null) stopped.TrySetResult(true); else stopped.TrySetException(failure);
        }
    }
    public void Dispose() { _ = StopAsync(false); }
}
