using System.Windows.Threading;

namespace DreamBooks.App.Services;

/// <summary>
/// The app's <see cref="IDelayScheduler"/>: one-shot <see cref="DispatcherTimer"/>s on the dispatcher of the thread
/// that created the scheduler (the UI thread). <see cref="Schedule"/> may be called from any thread: off that thread
/// the timer is started through the dispatcher, so the action still runs on the UI thread.
/// </summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var scheduled = new Scheduled(_dispatcher, delay, action);
        if (_dispatcher.CheckAccess())
        {
            scheduled.Start();
        }
        else
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, scheduled.Start);
        }

        return scheduled;
    }

    private sealed class Scheduled(Dispatcher dispatcher, TimeSpan delay, Action action) : IDisposable
    {
        private readonly object _gate = new();
        private DispatcherTimer? _timer;
        private bool _cancelled;

        // On the dispatcher's thread.
        public void Start()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                action();
            };
            lock (_gate)
            {
                if (_cancelled)
                {
                    return;
                }

                _timer = timer;
            }

            timer.Start();
        }

        public void Dispose()
        {
            DispatcherTimer? timer;
            lock (_gate)
            {
                _cancelled = true;
                timer = _timer;
            }

            if (timer is null)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                timer.Stop();
            }
            else
            {
                dispatcher.BeginInvoke(DispatcherPriority.Send, timer.Stop);
            }
        }
    }
}
