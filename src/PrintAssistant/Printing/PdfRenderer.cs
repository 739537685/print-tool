using System.Drawing;
using System.Drawing.Imaging;
using PDFtoImage;
using PrintAssistant.Server;
using SkiaSharp;

namespace PrintAssistant.Printing;

public interface IPdfRenderer
{
    IReadOnlyList<Bitmap> Render(string pdfPath, int dpi, int maxPages);
}

public sealed class PdfRenderer : IPdfRenderer
{
    private static readonly object RenderGate = new();

    public IReadOnlyList<Bitmap> Render(string pdfPath, int dpi, int maxPages)
    {
        lock (RenderGate)
        {
            return RenderCore(pdfPath, dpi, maxPages);
        }
    }

    private static IReadOnlyList<Bitmap> RenderCore(string pdfPath, int dpi, int maxPages)
    {
        try
        {
            int pageCount;
            using (var countStream = File.OpenRead(pdfPath))
            {
                pageCount = Conversion.GetPageCount(countStream, false, null);
            }

            if (pageCount <= 0)
            {
                throw new PrintException(ProtocolErrors.InvalidPdf, "PDF 没有可打印页面");
            }

            if (pageCount > maxPages)
            {
                throw new PrintException(ProtocolErrors.InvalidPdf, $"PDF 页数 {pageCount} 超过上限 {maxPages}");
            }

            var pages = new List<Bitmap>(pageCount);
            try
            {
                using var stream = File.OpenRead(pdfPath);
                var options = new RenderOptions { Dpi = dpi };
                foreach (var source in Conversion.ToImages(stream, false, null, options))
                {
                    using (source)
                    {
                        pages.Add(ToBitmap(source));
                    }
                }
            }
            catch
            {
                foreach (var page in pages)
                {
                    page.Dispose();
                }

                throw;
            }

            if (pages.Count == 0)
            {
                throw new PrintException(ProtocolErrors.InvalidPdf, "PDF 没有可打印页面");
            }

            return pages;
        }
        catch (PrintException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("encrypt", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("security handler", StringComparison.OrdinalIgnoreCase))
            {
                throw new PrintException(ProtocolErrors.PdfPasswordProtected, "PDF 已被密码加密保护，无法直接打印");
            }

            throw new PrintException(ProtocolErrors.InvalidPdf, $"PDF 解析失败：{ex.Message}");
        }
    }

    private static Bitmap ToBitmap(SKBitmap source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var info = new SKImageInfo(
                bitmap.Width,
                bitmap.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            var pixmap = source.PeekPixels();
            if (pixmap != null && pixmap.ReadPixels(info, data.Scan0, data.Stride))
            {
                bitmap.UnlockBits(data);
                return bitmap;
            }
        }
        catch
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
            throw;
        }

        bitmap.UnlockBits(data);
        bitmap.Dispose();

        // 降级兜底兼容
        using var image = SKImage.FromBitmap(source);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = encoded.AsStream();
        using var loaded = new Bitmap(stream);
        return new Bitmap(loaded);
    }
}
