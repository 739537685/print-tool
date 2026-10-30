using System.Drawing;
using PrintAssistant.Printing;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

internal sealed class FakePdfRenderer : IPdfRenderer
{
    private readonly HashSet<string> _failures;

    public FakePdfRenderer(params string[] failingPaths)
    {
        _failures = new HashSet<string>(failingPaths, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Bitmap> Render(string pdfPath, int dpi, int maxPages)
    {
        if (_failures.Contains(pdfPath) || pdfPath.Contains("bad", StringComparison.OrdinalIgnoreCase))
        {
            throw new PrintException(ProtocolErrors.InvalidPdf, "PDF 解析失败");
        }

        return new List<Bitmap> { new(10, 10) };
    }
}

internal sealed class FakeLabelPrinter : ILabelPrinter
{
    public List<string> PrintedPrinters { get; } = new();
    public double? LastOffsetXMm { get; private set; }
    public double? LastOffsetYMm { get; private set; }

    public PrintOutcome Print(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        int copies,
        PaperSpec paper,
        double? offsetXMm,
        double? offsetYMm,
        AppSettings settings)
    {
        PrintedPrinters.Add(printerName);
        LastOffsetXMm = offsetXMm;
        LastOffsetYMm = offsetYMm;
        return new PrintOutcome(pages.Count, Array.Empty<string>());
    }
}
