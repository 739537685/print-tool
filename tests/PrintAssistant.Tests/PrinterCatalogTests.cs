using PrintAssistant.Printing;
using PrintAssistant.Server;

namespace PrintAssistant.Tests;

public sealed class PrinterCatalogTests
{
    [Fact]
    public void ResolvePrinterName_IsCaseInsensitive()
    {
        var catalog = new PrinterCatalog(() => new[] { "Printer A", "Printer B" });

        Assert.Equal("Printer A", catalog.ResolvePrinterName("printer a"));
    }

    [Fact]
    public void ResolvePrinterName_UnknownPrinterThrows()
    {
        var catalog = new PrinterCatalog(() => new[] { "Printer A" });

        var exception = Assert.Throws<PrintException>(() => catalog.ResolvePrinterName("Printer Z"));

        Assert.Equal(ProtocolErrors.PrinterNotFound, exception.Code);
    }

    [Fact]
    public void GetPrinters_ReturnsInstalledNames()
    {
        var catalog = new PrinterCatalog(() => new[] { "Printer A", "Printer B" });

        var printers = catalog.GetPrinters();

        Assert.Equal(2, printers.Count);
        Assert.Contains(printers, p => p.Name == "Printer A");
    }

    [Fact]
    public void GetPrinters_CachesResultsUntilInvalidated()
    {
        var count = 0;
        var catalog = new PrinterCatalog(() =>
        {
            count++;
            return new[] { $"Printer {count}" };
        });

        var first = catalog.GetPrinters();
        var second = catalog.GetPrinters();
        Assert.Equal(1, count);
        Assert.Equal("Printer 1", first[0].Name);
        Assert.Equal("Printer 1", second[0].Name);

        catalog.InvalidateCache();
        var third = catalog.GetPrinters();
        Assert.Equal(2, count);
        Assert.Equal("Printer 2", third[0].Name);
    }
}
