using PrintAssistant.Printing;

namespace PrintAssistant.Tests;

public sealed class PaperMathTests
{
    [Theory]
    [InlineData(100, 394)]
    [InlineData(150, 591)]
    [InlineData(25.4, 100)]
    [InlineData(0, 0)]
    public void MillimeterToHundredthsInch(double millimeters, int expected)
    {
        Assert.Equal(expected, PaperMath.MmToHundredthsInch(millimeters));
    }

    [Fact]
    public void HundredthsInchBackToMillimeter()
    {
        Assert.InRange(PaperMath.HundredthsInchToMm(394), 99.9, 100.2);
        Assert.InRange(PaperMath.HundredthsInchToMm(591), 149.9, 150.3);
    }
}
