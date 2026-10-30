namespace PrintAssistant.Printing;

public static class PaperMath
{
    public static int MmToHundredthsInch(double millimeters)
    {
        return (int)Math.Round(millimeters / 25.4 * 100, MidpointRounding.AwayFromZero);
    }

    public static float MmToInch(double millimeters)
    {
        return (float)(millimeters / 25.4);
    }

    public static double HundredthsInchToMm(int value)
    {
        return value * 25.4 / 100.0;
    }
}
