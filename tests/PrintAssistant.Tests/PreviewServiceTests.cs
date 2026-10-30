using System.Drawing;
using PrintAssistant.Printing;
using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

public sealed class PreviewServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _spool;

    public PreviewServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PrintAssistantTests", Guid.NewGuid().ToString("N"));
        _spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(_spool);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 忽略清理异常
        }
    }

    [Fact]
    public void Preview_UsesPaperDimensionsAtPreviewDpi()
    {
        var service = new PreviewService(new FakePdfRenderer(), _spool, 10);
        var pdf = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7");

        var outcome = service.Render(pdf, new PaperSpec(100, 150), 0, 0);

        Assert.Equal(150, outcome.Dpi);
        var page = Assert.Single(outcome.Pages);
        Assert.Equal(591, page.WidthPx);
        Assert.Equal(886, page.HeightPx);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, page.Png[..4]);
        Assert.Empty(Directory.GetFiles(_spool, "preview-*.pdf"));
    }

    [Fact]
    public void Preview_ScalesToSmallerPaper()
    {
        var service = new PreviewService(new FakePdfRenderer(), _spool, 10);
        var pdf = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7");

        var outcome = service.Render(pdf, new PaperSpec(76, 130), 0, 0);

        Assert.Equal(150, outcome.Dpi);
        var page = Assert.Single(outcome.Pages);
        Assert.Equal(449, page.WidthPx);
        Assert.Equal(768, page.HeightPx);
    }

    [Fact]
    public void Preview_AppliesOffset()
    {
        var service = new PreviewService(new SolidPdfRenderer(), _spool, 10);
        var pdf = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7");

        var shifted = service.Render(pdf, new PaperSpec(100, 150), 10, 10);

        using var image = Image.FromStream(new MemoryStream(Assert.Single(shifted.Pages).Png));
        using var bitmap = new Bitmap(image);
        Assert.Equal(Color.White.ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(200, 200).ToArgb());
    }

    [Fact]
    public void Preview_WithoutOffsetFillsPaper()
    {
        var service = new PreviewService(new SolidPdfRenderer(), _spool, 10);
        var pdf = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7");

        var outcome = service.Render(pdf, new PaperSpec(100, 150), 0, 0);

        using var image = Image.FromStream(new MemoryStream(Assert.Single(outcome.Pages).Png));
        using var bitmap = new Bitmap(image);
        Assert.Equal(Color.Black.ToArgb(), bitmap.GetPixel(295, 443).ToArgb());
    }

    private sealed class SolidPdfRenderer : IPdfRenderer
    {
        public IReadOnlyList<Bitmap> Render(string pdfPath, int dpi, int maxPages)
        {
            var bitmap = new Bitmap(10, 10);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            return new[] { bitmap };
        }
    }
}
