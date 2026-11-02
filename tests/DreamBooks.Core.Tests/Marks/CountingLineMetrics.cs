using DreamBooks.Core.Marks;

namespace DreamBooks.Core.Tests.Marks;

/// <summary>Fixed 36 pt line height that counts how often the recogniser asks for it (once per recognised page).</summary>
internal sealed class CountingLineMetrics : IPageLineMetrics
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public double LineHeightPt(int pageIndex)
    {
        Interlocked.Increment(ref _calls);
        return 36;
    }
}
