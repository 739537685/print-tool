using PrintAssistant.Printing;
using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

public sealed class PrintQueueTests : IDisposable
{
    private readonly string _root;
    private readonly string _spool;
    private readonly TaskRepository _repository;
    private readonly AppSettings _settings = new();

    public PrintQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PrintAssistantTests", Guid.NewGuid().ToString("N"));
        _spool = Path.Combine(_root, "spool");
        Directory.CreateDirectory(_spool);
        _repository = new TaskRepository(Path.Combine(_root, "tasks.jsonl"), _spool, 100, 30);
        _repository.Initialize();
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

    private PrintTask CreateTask(
        string printer,
        string fileName,
        string clientId,
        double? offsetXMm = null,
        double? offsetYMm = null)
    {
        var path = Path.Combine(_spool, fileName);
        File.WriteAllText(path, "%PDF-1.7");
        var task = new PrintTask
        {
            ClientId = clientId,
            Printer = printer,
            Copies = 1,
            OffsetXMm = offsetXMm,
            OffsetYMm = offsetYMm,
            PdfPath = path
        };
        _repository.Add(task);
        return task;
    }

    [Fact]
    public void SuccessfulTask_IsMarkedDone()
    {
        var printer = new FakeLabelPrinter();
        using var queue = new PrintQueue(_repository, new FakePdfRenderer(), printer, _settings);
        var task = CreateTask("Printer A", "ok.pdf", "c1");

        queue.Process(task);

        Assert.Equal(PrintTaskStatus.Done, task.Status);
        Assert.Equal(1, task.Pages);
        Assert.Single(printer.PrintedPrinters);
        Assert.Equal("Printer A", printer.PrintedPrinters[0]);
    }

    [Fact]
    public void TaskOffsets_ArePassedToPrinter()
    {
        var printer = new FakeLabelPrinter();
        using var queue = new PrintQueue(_repository, new FakePdfRenderer(), printer, _settings);
        var task = CreateTask("Printer A", "offset.pdf", "c1", 1.5, -2.25);

        queue.Process(task);

        Assert.Equal(1.5, printer.LastOffsetXMm);
        Assert.Equal(-2.25, printer.LastOffsetYMm);
    }

    [Fact]
    public void RenderFailure_IsMarkedFailedAndDoesNotThrow()
    {
        var printer = new FakeLabelPrinter();
        using var queue = new PrintQueue(_repository, new FakePdfRenderer(), printer, _settings);
        var task = CreateTask("Printer A", "bad.pdf", "c1");

        var exception = Record.Exception(() => queue.Process(task));

        Assert.Null(exception);
        Assert.Equal(PrintTaskStatus.Failed, task.Status);
        Assert.Equal("INVALID_PDF", task.Error);
        Assert.Empty(printer.PrintedPrinters);
    }

    [Fact]
    public async Task QueuedTasks_AreProcessedInOrder()
    {
        var first = CreateTask("Printer A", "one.pdf", "c1");
        var second = CreateTask("Printer B", "two.pdf", "c2");
        var printer = new FakeLabelPrinter();
        using var queue = new PrintQueue(_repository, new FakePdfRenderer(), printer, _settings);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        queue.TaskFinished += _ =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                completion.TrySetResult();
            }
        };

        await queue.StartAsync();
        queue.Enqueue(first);
        queue.Enqueue(second);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "Printer A", "Printer B" }, printer.PrintedPrinters);
        Assert.Equal(PrintTaskStatus.Done, first.Status);
        Assert.Equal(PrintTaskStatus.Done, second.Status);
    }
}