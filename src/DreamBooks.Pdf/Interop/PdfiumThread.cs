using System.Collections.Concurrent;

namespace DreamBooks.Pdf.Interop;

/// <summary>
/// The single background thread on which every PDFium call runs (PDFium is not
/// thread-safe; NFR-04 keeps it off the UI thread). Work items run in FIFO order.
/// </summary>
internal static class PdfiumThread
{
    private static readonly BlockingCollection<Action> _queue = [];
    private static readonly Lazy<Thread> _thread = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static Exception? _initError;

    /// <summary>Managed id of the PDFium thread, or 0 if it has not started yet.</summary>
    public static int ThreadId => _thread.IsValueCreated ? _thread.Value.ManagedThreadId : 0;

    /// <summary>True when the caller is running on the PDFium thread.</summary>
    public static bool IsPdfiumThread =>
        _thread.IsValueCreated && Environment.CurrentManagedThreadId == _thread.Value.ManagedThreadId;

    /// <summary>
    /// Queues <paramref name="work"/> on the PDFium thread. The token is checked just before
    /// the item runs; a cancelled item completes as canceled without running.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(ct);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _thread.Value;
        _queue.Add(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            if (_initError is not null)
            {
                tcs.TrySetException(_initError);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    /// <summary>Queues an action with no result on the PDFium thread.</summary>
    public static Task RunAsync(Action work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            () =>
            {
                work();
                return true;
            },
            ct);
    }

    private static Thread Start()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "PDFium",
        };
        thread.Start();
        return thread;
    }

    private static void Loop()
    {
        try
        {
            PdfiumNative.FPDF_InitLibrary();
        }
        catch (Exception ex)
        {
            // e.g. DllNotFoundException on an unsupported runtime: surface it on every call.
            _initError = ex;
        }

        foreach (Action item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }
}
