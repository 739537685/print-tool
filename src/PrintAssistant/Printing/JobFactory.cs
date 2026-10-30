using PrintAssistant.Storage;

namespace PrintAssistant.Printing;

public static class JobFactory
{
    public static PrintTask CreateFromRequest(
        string spoolDirectory,
        string clientId,
        string? reqId,
        string printer,
        int copies,
        PaperSpec paper,
        double offsetXMm,
        double offsetYMm,
        byte[] pdf,
        Dictionary<string, string>? meta)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var pdfPath = WritePdf(spoolDirectory, taskId, pdf);
        return new PrintTask
        {
            TaskId = taskId,
            ClientId = clientId,
            ReqId = reqId,
            Status = PrintTaskStatus.Queued,
            Printer = printer,
            Copies = copies,
            Paper = paper,
            OffsetXMm = offsetXMm,
            OffsetYMm = offsetYMm,
            PdfPath = pdfPath,
            Meta = meta,
            CreatedAt = DateTimeOffset.Now
        };
    }

    public static PrintTask CreateReprint(string spoolDirectory, PrintTask original)
    {
        if (!File.Exists(original.PdfPath))
        {
            throw new InvalidOperationException("原始 PDF 已被清理，无法重打");
        }

        var taskId = Guid.NewGuid().ToString("N");
        var target = Path.Combine(spoolDirectory, taskId + ".pdf");
        Directory.CreateDirectory(spoolDirectory);
        File.Copy(original.PdfPath, target, false);

        return new PrintTask
        {
            TaskId = taskId,
            ClientId = null,
            ReqId = original.ReqId,
            Status = PrintTaskStatus.Queued,
            Printer = original.Printer,
            Copies = original.Copies,
            Paper = original.Paper,
            OffsetXMm = original.OffsetXMm,
            OffsetYMm = original.OffsetYMm,
            PdfPath = target,
            Meta = original.Meta == null ? null : new Dictionary<string, string>(original.Meta),
            CreatedAt = DateTimeOffset.Now
        };
    }

    private static string WritePdf(string spoolDirectory, string taskId, byte[] pdf)
    {
        Directory.CreateDirectory(spoolDirectory);
        var pdfPath = Path.Combine(spoolDirectory, taskId + ".pdf");
        File.WriteAllBytes(pdfPath, pdf);
        return pdfPath;
    }
}
