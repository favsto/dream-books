using DreamBooks.Core.Model;

namespace DreamBooks.Core.Tests.Model;

public class UnitsTests
{
    [Fact]
    public void MmToPt_converts()
    {
        Assert.Equal(72.0, Units.MmToPt(25.4), 9);
        Assert.Equal(1.417, Units.MmToPt(0.5), 3);
    }

    [Fact]
    public void PtToMm_is_inverse_of_MmToPt()
    {
        Assert.Equal(3.7, Units.PtToMm(Units.MmToPt(3.7)), 9);
    }
}
