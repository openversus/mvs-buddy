using System.Collections.Concurrent;
using RmlUi.Desktop;

namespace MvsBuddy.App;

/// <summary>
/// RmlUi's thread, and work kept off it. RmlUi may only be called from the thread that made the window, so work that
/// downloads or moves files runs on the thread pool and hands its result back here, where the loop runs it between
/// frames. While work runs the loop stays awake (no power save), because nothing can wake a window waiting for input
/// from another thread.
/// </summary>
internal sealed class UiThread(RmlWindow window)
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly CancellationTokenSource closing = new();
    private int working;

    /// <summary>Cancelled when the window closes: work that can stop (a search of the drives) watches it.</summary>
    public CancellationToken Closing => closing.Token;

    /// <summary>Where an exception from something run on this thread goes, instead of ending the program.</summary>
    public Action<Exception>? OnError { get; set; }

    /// <summary>Keeps the loop running frames without waiting for input (the autopilot and screenshots need frames).</summary>
    public bool KeepAwake { get; set; }

    /// <summary>Whether work is running off this thread.</summary>
    public bool Busy => Volatile.Read(ref working) > 0;

    /// <summary>Runs <paramref name="work"/> off this thread, then <paramref name="done"/> or <paramref name="failed"/> on it.</summary>
    public void Run<T>(Func<Task<T>> work, Action<T> done, Action<Exception> failed)
    {
        Interlocked.Increment(ref working);
        window.PowerSave = false;
        Task.Run(work).ContinueWith(task =>
        {
            // Queued before the count drops, so the loop never sees "not busy" with this still unseen.
            queue.Enqueue(() =>
            {
                if (task.IsFaulted)
                {
                    failed(task.Exception!.GetBaseException());
                }
                else
                {
                    done(task.Result);
                }
            });
            Interlocked.Decrement(ref working);
        }, TaskScheduler.Default);
    }

    /// <summary>Runs <paramref name="action"/> on this thread at the next frame (progress from running work).</summary>
    public void Post(Action action) => queue.Enqueue(action);

    /// <summary>
    /// Runs frames until the window closes, running what was handed back between them; <paramref name="afterFrame"/>
    /// runs after each. When the window closes, work that can stop is told to (<see cref="Closing"/>), and the rest (an
    /// install moving files) is waited for: a change is all or nothing only if the process lives to put it back.
    /// </summary>
    public void Loop(Action? afterFrame = null)
    {
        while (window.RunFrame())
        {
            while (queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e) when (OnError != null)
                {
                    OnError(e);
                }
            }
            afterFrame?.Invoke();
            bool idle = !Busy && queue.IsEmpty && !KeepAwake;
            window.PowerSave = idle;
            if (!idle)
            {
                // Awake only to notice work finishing; a frame every 15 ms is plenty for a progress line.
                Thread.Sleep(15);
            }
        }
        closing.Cancel();
        while (Busy)
        {
            Thread.Sleep(50);
        }
    }
}
