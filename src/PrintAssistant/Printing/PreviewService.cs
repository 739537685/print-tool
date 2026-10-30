using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PrintAssistant.Storage;

namespace PrintAssistant.Printing;

public sealed record PreviewImage(int WidthPx, int HeightPx, byte[] Png);

public sealed record PreviewOutcome(int Dpi, IReadOnlyList<PreviewImage> Pages);

public sealed class PreviewService
{
    private const int MaxPreviewDpi = 150;
    private const int MinPreviewDpi = 72;
    private const int MaxPreviewPixels = 1000;

    private readonly IPdfRenderer _renderer;
    private readonly string _spoolDirectory;
    private readonly int _maxPages;

    public PreviewService(IPdfRenderer renderer, string spoolDirectory, int maxPages)
    {
        _renderer = renderer;
        _spoolDirectory = spoolDirectory;
        _maxPages = maxPages;
    }

    public PreviewOutcome Render(byte[] pdf, PaperSpec paper, double offsetXMm, double offsetYMm)
    {
        var (dpi, widthPx, heightPx) = CalculateCanvas(paper);
        Directory.CreateDirectory(_spoolDirectory);
        var tempPath = Path.Combine(_spoolDirectory, "preview-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(tempPath, pdf);
        try
        {
            var rendered = _renderer.Render(tempPath, dpi, _maxPages);
            try
            {
                var pages = new List<PreviewImage>(rendered.Count);
                var pixelsPerMm = dpi / 25.4;
                foreach (var page in rendered)
                {
                    using var canvas = new Bitmap(widthPx, heightPx, PixelFormat.Format24bppRgb);
                    canvas.SetResolution(dpi, dpi);
                    using (var graphics = Graphics.FromImage(canvas))
                    {
                        graphics.Clear(Color.White);
                        graphics.CompositingMode = CompositingMode.SourceOver;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        graphics.SmoothingMode = SmoothingMode.None;
                        graphics.DrawImage(
                            page,
                            new RectangleF(
                                (float)(offsetXMm * pixelsPerMm),
                                (float)(offsetYMm * pixelsPerMm),
                                widthPx,
                                heightPx));
                    }

                    using var stream = new MemoryStream();
                    canvas.Save(stream, ImageFormat.Png);
                    pages.Add(new PreviewImage(widthPx, heightPx, stream.ToArray()));
                }

                return new PreviewOutcome(dpi, pages);
            }
            finally
            {
                foreach (var page in rendered)
                {
                    page.Dispose();
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清理失败不影响预览结果
            }
        }
    }

    private static (int Dpi, int WidthPx, int HeightPx) CalculateCanvas(PaperSpec paper)
    {
        var maxInches = Math.Max(paper.WidthMm, paper.HeightMm) / 25.4;
        var dpi = (int)Math.Floor(Math.Min(MaxPreviewDpi, MaxPreviewPixels / maxInches));
        dpi = Math.Clamp(dpi, MinPreviewDpi, MaxPreviewDpi);
        var widthPx = Math.Max(1, (int)Math.Round(paper.WidthMm / 25.4 * dpi));
        var heightPx = Math.Max(1, (int)Math.Round(paper.HeightMm / 25.4 * dpi));
        return (dpi, widthPx, heightPx);
    }
}
