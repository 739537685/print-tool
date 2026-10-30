using PrintAssistant.Printing;
using PrintAssistant.Storage;

namespace PrintAssistant.Server;

public static class ProtocolErrors
{
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string InvalidPdf = "INVALID_PDF";
    public const string PdfPasswordProtected = "PDF_PASSWORD_PROTECTED";
    public const string PrinterNotFound = "PRINTER_NOT_FOUND";
    public const string PrinterOffline = "PRINTER_OFFLINE";
    public const string PrintFailed = "PRINT_FAILED";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed class PrintException : Exception
{
    public PrintException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class ClientMessage
{
    public string Type { get; set; } = "";
    public int? Version { get; set; }
    public string? Token { get; set; }
    public string? Code { get; set; }
    public string? ClientName { get; set; }
    public string? ReqId { get; set; }
    public string? Id { get; set; }
    public string? TaskId { get; set; }
    public string? Printer { get; set; }
    public int? Copies { get; set; }
    public PaperSpec? Paper { get; set; }
    public string? Pdf { get; set; }
    public Dictionary<string, string>? Meta { get; set; }
    public int? Limit { get; set; }
    public double? OffsetXMm { get; set; }
    public double? OffsetYMm { get; set; }
}

public sealed record NormalizedPrint(
    byte[] Pdf,
    string ClientId,
    int Copies,
    PaperSpec Paper,
    Dictionary<string, string>? Meta,
    double OffsetXMm,
    double OffsetYMm);

public sealed record NormalizedPreview(
    byte[] Pdf,
    PaperSpec Paper,
    double OffsetXMm,
    double OffsetYMm);

public sealed record PrintValidation(bool Ok, NormalizedPrint? Value, string? Code, string? Message)
{
    public static PrintValidation Success(NormalizedPrint value) => new(true, value, null, null);
    public static PrintValidation Failure(string code, string message) => new(false, null, code, message);
}

public sealed record PreviewValidation(bool Ok, NormalizedPreview? Value, string? Code, string? Message)
{
    public static PreviewValidation Success(NormalizedPreview value) => new(true, value, null, null);
    public static PreviewValidation Failure(string code, string message) => new(false, null, code, message);
}

public static class ProtocolValidation
{
    public static PrintValidation ValidatePrint(ClientMessage message, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return PrintValidation.Failure(ProtocolErrors.BadRequest, "缺少任务 id");
        }

        if (message.Id.Length > 128)
        {
            return PrintValidation.Failure(ProtocolErrors.BadRequest, "任务 id 过长");
        }

        var pdf = ReadPdf(message, settings);
        if (pdf.Pdf == null)
        {
            return PrintValidation.Failure(pdf.Code!, pdf.Message!);
        }

        var copies = message.Copies ?? 1;
        if (copies < 1 || copies > settings.MaxCopies)
        {
            return PrintValidation.Failure(ProtocolErrors.BadRequest, $"份数必须在 1 到 {settings.MaxCopies} 之间");
        }

        var paper = ReadPaper(message, settings);
        if (paper.Paper == null)
        {
            return PrintValidation.Failure(paper.Code!, paper.Message!);
        }

        var offset = ReadOffsets(message, settings);
        if (offset.Code != null)
        {
            return PrintValidation.Failure(offset.Code, offset.Message!);
        }

        return PrintValidation.Success(new NormalizedPrint(
            pdf.Pdf,
            message.Id,
            copies,
            paper.Paper,
            message.Meta,
            offset.X,
            offset.Y));
    }

    public static PreviewValidation ValidatePreview(ClientMessage message, AppSettings settings)
    {
        var pdf = ReadPdf(message, settings);
        if (pdf.Pdf == null)
        {
            return PreviewValidation.Failure(pdf.Code!, pdf.Message!);
        }

        var paper = ReadPaper(message, settings);
        if (paper.Paper == null)
        {
            return PreviewValidation.Failure(paper.Code!, paper.Message!);
        }

        var offset = ReadOffsets(message, settings);
        if (offset.Code != null)
        {
            return PreviewValidation.Failure(offset.Code, offset.Message!);
        }

        return PreviewValidation.Success(new NormalizedPreview(pdf.Pdf, paper.Paper, offset.X, offset.Y));
    }

    private static (byte[]? Pdf, string? Code, string? Message) ReadPdf(ClientMessage message, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(message.Pdf))
        {
            return (null, ProtocolErrors.BadRequest, "缺少 pdf 数据");
        }

        var estimatedBytes = (long)message.Pdf.Length / 4 * 3;
        if (estimatedBytes > settings.MaxPdfBytes + 4L * 1024 * 1024)
        {
            return (null, ProtocolErrors.PayloadTooLarge, "PDF 超过大小限制");
        }

        byte[] pdf;
        try
        {
            pdf = Convert.FromBase64String(message.Pdf);
        }
        catch (FormatException)
        {
            return (null, ProtocolErrors.BadRequest, "pdf 不是合法的 base64");
        }

        if (pdf.Length == 0)
        {
            return (null, ProtocolErrors.BadRequest, "pdf 为空");
        }

        if (pdf.Length > settings.MaxPdfBytes)
        {
            return (null, ProtocolErrors.PayloadTooLarge, $"PDF 超过 {settings.MaxPdfBytes} 字节限制");
        }

        if (!LooksLikePdf(pdf))
        {
            return (null, ProtocolErrors.InvalidPdf, "pdf 文件头不正确");
        }

        return (pdf, null, null);
    }

    private static (PaperSpec? Paper, string? Code, string? Message) ReadPaper(ClientMessage message, AppSettings settings)
    {
        var paper = message.Paper ?? new PaperSpec(settings.PaperWidthMm, settings.PaperHeightMm);
        if (paper.WidthMm is <= 10 or > 300 || paper.HeightMm is <= 10 or > 300)
        {
            return (null, ProtocolErrors.BadRequest, "纸张尺寸不合法");
        }

        return (paper, null, null);
    }

    private static (double X, double Y, string? Code, string? Message) ReadOffsets(
        ClientMessage message,
        AppSettings settings)
    {
        var x = message.OffsetXMm ?? settings.OffsetXMm;
        var y = message.OffsetYMm ?? settings.OffsetYMm;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 50 || Math.Abs(y) > 50)
        {
            return (0, 0, ProtocolErrors.BadRequest, "偏移必须在 -50 到 50 mm 之间");
        }

        return (x, y, null, null);
    }

    private static bool LooksLikePdf(byte[] bytes)
    {
        if (bytes.Length < 5)
        {
            return false;
        }

        var limit = Math.Min(bytes.Length - 4, 16);
        for (var i = 0; i <= limit; i++)
        {
            if (bytes[i] == (byte)'%' &&
                bytes[i + 1] == (byte)'P' &&
                bytes[i + 2] == (byte)'D' &&
                bytes[i + 3] == (byte)'F' &&
                bytes[i + 4] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }
}

public sealed record PairOkResult(string Token, string HelperVersion)
{
    public string Type => "pair_ok";
}

public sealed record HelloOkResult(int Version, string HelperVersion, IReadOnlyList<PrinterInfo> Printers)
{
    public string Type => "hello_ok";
}

public sealed record PrintersResult(string? ReqId, IReadOnlyList<PrinterInfo> Printers)
{
    public string Type => "printers";
}

public sealed record AcceptedResult(string? Id, string? ReqId, string TaskId, bool Duplicate = false)
{
    public string Type => "accepted";
}

public sealed record ResultMessage(
    string? Id,
    string TaskId,
    string Status,
    int? Pages,
    long? Ms,
    IReadOnlyList<string> Warnings,
    string? Error,
    string? Message)
{
    public string Type => "result";
}

public sealed record TaskView(
    string TaskId,
    string? ClientId,
    string Status,
    string Printer,
    int Copies,
    int? Pages,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    string? Message,
    IReadOnlyList<string> Warnings,
    Dictionary<string, string>? Meta)
{
    public static TaskView FromTask(PrintTask task) => new(
        task.TaskId,
        task.ClientId,
        task.Status,
        task.Printer,
        task.Copies,
        task.Pages,
        task.CreatedAt,
        task.FinishedAt,
        task.Error,
        task.Message,
        task.Warnings.ToList(),
        task.Meta);
}

public sealed record PreviewPage(int WidthPx, int HeightPx, string Png);

public sealed record PreviewResult(
    string? ReqId,
    int Dpi,
    double PaperWidthMm,
    double PaperHeightMm,
    double OffsetXMm,
    double OffsetYMm,
    IReadOnlyList<PreviewPage> Pages)
{
    public string Type => "preview";
}

public sealed record HistoryResult(string? ReqId, IReadOnlyList<TaskView> Tasks)
{
    public string Type => "history";
}

public sealed record ErrorResult(string? ReqId, string Code, string Message)
{
    public string Type => "error";
}

public sealed record PongResult
{
    public string Type => "pong";
}
