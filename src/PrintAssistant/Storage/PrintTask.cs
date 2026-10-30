namespace PrintAssistant.Storage;

public static class PrintTaskStatus
{
    public const string Queued = "queued";
    public const string Printing = "printing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
}

public sealed class PrintTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ClientId { get; set; }
    public string? ReqId { get; set; }
    public string Status { get; set; } = PrintTaskStatus.Queued;
    public string Printer { get; set; } = "";
    public int Copies { get; set; } = 1;
    public PaperSpec Paper { get; set; } = new(100, 150);
    public double? OffsetXMm { get; set; }
    public double? OffsetYMm { get; set; }
    public string PdfPath { get; set; } = "";
    public Dictionary<string, string>? Meta { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? Pages { get; set; }
    public long? DurationMs { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed record PaperSpec(double WidthMm, double HeightMm);
