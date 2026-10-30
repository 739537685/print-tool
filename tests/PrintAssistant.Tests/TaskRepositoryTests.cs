using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

public sealed class TaskRepositoryTests : IDisposable
{
    private readonly string _root;
    private readonly string _spool;

    public TaskRepositoryTests()
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

    private TaskRepository CreateRepository(int limit = 10, int days = 30)
    {
        var repository = new TaskRepository(
            Path.Combine(_root, "tasks.jsonl"),
            _spool,
            limit,
            days);
        repository.Initialize();
        return repository;
    }

    private PrintTask CreateTask(string clientId, DateTimeOffset? createdAt = null)
    {
        var task = new PrintTask
        {
            ClientId = clientId,
            Status = PrintTaskStatus.Queued,
            Printer = "Printer A",
            PdfPath = Path.Combine(_spool, Guid.NewGuid().ToString("N") + ".pdf"),
            CreatedAt = createdAt ?? DateTimeOffset.Now
        };
        File.WriteAllText(task.PdfPath, "%PDF-1.7");
        return task;
    }

    [Fact]
    public void AddedTask_CanBeFoundByIdAndClientId()
    {
        var repository = CreateRepository();
        var task = CreateTask("client-1");

        repository.Add(task);

        Assert.Same(task, repository.Find(task.TaskId));
        Assert.Same(task, repository.FindByClientId("client-1"));
    }

    [Fact]
    public void TasksPersistAcrossRestart()
    {
        var repository = CreateRepository();
        var task = CreateTask("client-1");
        repository.Add(task);
        task.Status = PrintTaskStatus.Done;
        repository.Update(task);

        var reloaded = CreateRepository();

        var loaded = reloaded.Find(task.TaskId);
        Assert.NotNull(loaded);
        Assert.Equal(PrintTaskStatus.Done, loaded!.Status);
        Assert.Equal("client-1", loaded.ClientId);
    }

    [Fact]
    public void UnfinishedTask_BecomesInterruptedAfterRestart()
    {
        var repository = CreateRepository();
        var task = CreateTask("client-1");
        repository.Add(task);

        var reloaded = CreateRepository();

        var loaded = reloaded.Find(task.TaskId);
        Assert.Equal(PrintTaskStatus.Interrupted, loaded!.Status);
        Assert.Equal("INTERRUPTED", loaded.Error);
    }

    [Fact]
    public void HistoryLimit_RemovesOldestTaskAndPdf()
    {
        var repository = CreateRepository(limit: 2);
        var oldest = CreateTask("c1", DateTimeOffset.Now.AddMinutes(-3));
        var middle = CreateTask("c2", DateTimeOffset.Now.AddMinutes(-2));
        var newest = CreateTask("c3", DateTimeOffset.Now.AddMinutes(-1));

        repository.Add(oldest);
        repository.Add(middle);
        repository.Add(newest);

        Assert.Null(repository.Find(oldest.TaskId));
        Assert.Equal(2, repository.Recent(10).Count);
        Assert.False(File.Exists(oldest.PdfPath));
    }

    [Fact]
    public void TaskUpdates_AreAppendedAndReloadLatestState()
    {
        var repository = CreateRepository(limit: 50);
        var task = CreateTask("c-fast");
        repository.Add(task);

        task.Status = PrintTaskStatus.Printing;
        repository.Update(task);

        task.Status = PrintTaskStatus.Done;
        task.Pages = 3;
        repository.Update(task);

        // 重新加载验证最新状态
        var reloaded = CreateRepository(limit: 50);
        var loaded = reloaded.Find(task.TaskId);
        Assert.NotNull(loaded);
        Assert.Equal(PrintTaskStatus.Done, loaded!.Status);
        Assert.Equal(3, loaded.Pages);
    }
}
