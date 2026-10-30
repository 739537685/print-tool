using System.Drawing;
using System.Drawing.Printing;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Printing;

public sealed record PrintOutcome(int Pages, IReadOnlyList<string> Warnings);

public interface ILabelPrinter
{
    PrintOutcome Print(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        int copies,
        PaperSpec paper,
        double? offsetXMm,
        double? offsetYMm,
        AppSettings settings);
}

public sealed class LabelPrinter : ILabelPrinter
{
    public PrintOutcome Print(
        IReadOnlyList<Bitmap> pages,
        string printerName,
        int copies,
        PaperSpec paper,
        double? offsetXMm,
        double? offsetYMm,
        AppSettings settings)
    {
        if (pages.Count == 0)
        {
            throw new PrintException(ProtocolErrors.InvalidPdf, "没有可打印页面");
        }

        var printerSettings = new PrinterSettings { PrinterName = printerName };
        if (!printerSettings.IsValid)
        {
            throw new PrintException(ProtocolErrors.PrinterNotFound, $"打印机不存在或不可用：{printerName}");
        }

        var warnings = new List<string>();
        var pageSettings = new PageSettings(printerSettings)
        {
            Margins = new Margins(0, 0, 0, 0),
            Landscape = false,
            Color = false
        };

        var width = PaperMath.MmToHundredthsInch(paper.WidthMm);
        var height = PaperMath.MmToHundredthsInch(paper.HeightMm);
        var matchedPaper = FindPaperSize(printerSettings, width, height);
        var usingCustomPaper = matchedPaper == null;
        pageSettings.PaperSize = matchedPaper ?? new PaperSize("PrintAssistant", width, height);
        if (usingCustomPaper)
        {
            warnings.Add("CUSTOM_PAPER");
        }

        using var document = new PrintDocument
        {
            PrinterSettings = printerSettings,
            PrintController = new StandardPrintController(),
            OriginAtMargins = false,
            DocumentName = "电子面单"
        };
        document.DefaultPageSettings = pageSettings;

        var pageIndex = 0;
        document.PrintPage += (_, e) =>
        {
            if (e.Graphics == null)
            {
                return;
            }

            e.Graphics.PageUnit = GraphicsUnit.Inch;
            var pageWidth = e.PageBounds.Width / 100f;
            var pageHeight = e.PageBounds.Height / 100f;
            var offsetX = PaperMath.MmToInch(offsetXMm ?? settings.OffsetXMm);
            var offsetY = PaperMath.MmToInch(offsetYMm ?? settings.OffsetYMm);
            e.Graphics.DrawImage(
                pages[pageIndex],
                new RectangleF(offsetX, offsetY, pageWidth, pageHeight));

            pageIndex++;
            e.HasMorePages = pageIndex < pages.Count;
        };

        try
        {
            for (var copy = 0; copy < copies; copy++)
            {
                pageIndex = 0;
                try
                {
                    document.Print();
                }
                catch (PrintException)
                {
                    throw;
                }
                catch (Exception ex) when (usingCustomPaper)
                {
                    usingCustomPaper = false;
                    warnings.Add("PAPER_SIZE_FALLBACK");
                    try
                    {
                        pageSettings.PaperSize = printerSettings.DefaultPageSettings.PaperSize;
                        document.DefaultPageSettings.PaperSize = pageSettings.PaperSize;
                    }
                    catch
                    {
                        // 保持驱动默认设置
                    }

                    pageIndex = 0;
                    try
                    {
                        document.Print();
                    }
                    catch (Exception inner)
                    {
                        throw ClassifyPrintException(inner, ex);
                    }
                }
                catch (Exception ex)
                {
                    throw ClassifyPrintException(ex);
                }
            }
        }
        finally
        {
            pageIndex = 0;
        }

        return new PrintOutcome(pages.Count, warnings);
    }

    private static PrintException ClassifyPrintException(Exception ex, Exception? initial = null)
    {
        if (ex is PrintException pe)
        {
            return pe;
        }

        if (ex is InvalidPrinterException ipe)
        {
            return new PrintException(ProtocolErrors.PrinterNotFound, $"打印机不可用：{ipe.Message}");
        }

        var msg = ex.Message ?? "";
        if (msg.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("脱机", StringComparison.OrdinalIgnoreCase))
        {
            return new PrintException(ProtocolErrors.PrinterOffline, $"打印机处于脱机状态：{msg}");
        }

        var detail = initial == null ? msg : $"{msg}（首次错误：{initial.Message}）";
        return new PrintException(ProtocolErrors.PrintFailed, $"打印失败：{detail}");
    }

    private static PaperSize? FindPaperSize(PrinterSettings settings, int width, int height)
    {
        try
        {
            foreach (PaperSize paper in settings.PaperSizes)
            {
                if (Math.Abs(paper.Width - width) <= 2 && Math.Abs(paper.Height - height) <= 2)
                {
                    return paper;
                }
            }
        }
        catch
        {
            // 驱动未提供纸型列表
        }

        return null;
    }
}
