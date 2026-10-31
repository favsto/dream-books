using System.Windows.Threading;

namespace DreamBooks.App.Services;

/// <summary>The app's <see cref="IDelayScheduler"/>: a one-shot <see cref="DispatcherTimer"/> on the calling thread's dispatcher.</summary>
public sealed class DispatcherDelayScheduler : IDelayScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new Scheduled(timer);
    }

    private sealed class Scheduled(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
