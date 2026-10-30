using System.Text;
using System.Text.Json;

namespace PrintAssistant.Storage;

public sealed class TaskRepository
{
    private readonly string _tasksFile;
    private readonly string _spoolDirectory;
    private readonly int _limit;
    private readonly int _days;
    private readonly object _gate = new();
    private readonly List<PrintTask> _tasks = new();
    private readonly Dictionary<string, PrintTask> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PrintTask> _byClientId = new(StringComparer.Ordinal);
    private int _appendsSinceCompaction;

    public TaskRepository(string tasksFile, string spoolDirectory, int limit, int days)
    {
        _tasksFile = tasksFile;
        _spoolDirectory = spoolDirectory;
        _limit = Math.Max(1, limit);
        _days = Math.Max(1, days);
    }

    public void Initialize()
    {
        lock (_gate)
        {
            _tasks.Clear();
            _byId.Clear();
            _byClientId.Clear();

            if (File.Exists(_tasksFile))
            {
                foreach (var line in File.ReadLines(_tasksFile, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var task = JsonSerializer.Deserialize<PrintTask>(line, JsonConfig.Default);
                        if (task != null && !string.IsNullOrWhiteSpace(task.TaskId))
                        {
                            AddToMemory(task);
                        }
                    }
                    catch
                    {
                        // 跳过损坏的记录
                    }
                }
            }

            var now = DateTimeOffset.Now;
            foreach (var task in _tasks)
            {
                if (task.Status is PrintTaskStatus.Queued or PrintTaskStatus.Printing)
                {
                    task.Status = PrintTaskStatus.Interrupted;
                    task.FinishedAt = now;
                    task.Error = "INTERRUPTED";
                    task.Message = "助手重启，任务被中断";
                }
            }

            TrimCore(now);
            SaveAllCore();
            _appendsSinceCompaction = 0;
        }

        CleanupOrphanedSpoolFilesAsync();
    }

    private void CleanupOrphanedSpoolFilesAsync()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_spoolDirectory))
                {
                    return;
                }

                HashSet<string> knownPdfs;
                lock (_gate)
                {
                    knownPdfs = _tasks
                        .Where(t => !string.IsNullOrEmpty(t.PdfPath))
                        .Select(t => Path.GetFullPath(t.PdfPath))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }

                var now = DateTime.UtcNow;
                var files = Directory.EnumerateFiles(_spoolDirectory, "*.pdf");
                foreach (var file in files)
                {
                    try
                    {
                        var fullPath = Path.GetFullPath(file);
                        if (!knownPdfs.Contains(fullPath))
                        {
                            var lastWrite = File.GetLastWriteTimeUtc(file);
                            if (now - lastWrite > TimeSpan.FromHours(24))
                            {
                                File.Delete(file);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略单个文件清理失败
                    }
                }
            }
            catch
            {
                // 忽略清理异常
            }
        });
    }

    public PrintTask? Find(string taskId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(taskId, out var task) ? task : null;
        }
    }

    public PrintTask? FindByClientId(string clientId)
    {
        lock (_gate)
        {
            return _byClientId.TryGetValue(clientId, out var task) ? task : null;
        }
    }

    public void Add(PrintTask task)
    {
        lock (_gate)
        {
            AddToMemory(task);
            var trimmedCount = TrimCore(DateTimeOffset.Now);
            if (trimmedCount > 0 || _appendsSinceCompaction >= 100)
            {
                SaveAllCore();
                _appendsSinceCompaction = 0;
            }
            else
            {
                AppendTaskCore(task);
                _appendsSinceCompaction++;
            }
        }
    }

    public void Update(PrintTask task)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(task.TaskId))
            {
                AddToMemory(task);
            }

            if (_appendsSinceCompaction >= 100)
            {
                SaveAllCore();
                _appendsSinceCompaction = 0;
            }
            else
            {
                AppendTaskCore(task);
                _appendsSinceCompaction++;
            }
        }
    }

    public IReadOnlyList<PrintTask> Recent(int limit)
    {
        lock (_gate)
        {
            return _tasks
                .OrderByDescending(t => t.CreatedAt)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
    }

    private void AddToMemory(PrintTask task)
    {
        if (_byId.TryGetValue(task.TaskId, out var existing))
        {
            _tasks.Remove(existing);
        }

        _tasks.Add(task);
        _byId[task.TaskId] = task;
        if (!string.IsNullOrWhiteSpace(task.ClientId))
        {
            _byClientId[task.ClientId] = task;
        }
    }

    private int TrimCore(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-_days);
        var keep = _tasks
            .OrderByDescending(t => t.CreatedAt)
            .Where(t => t.CreatedAt >= cutoff)
            .Take(_limit)
            .ToHashSet();

        var removed = _tasks.Where(t => !keep.Contains(t)).ToList();
        foreach (var task in removed)
        {
            _tasks.Remove(task);
            _byId.Remove(task.TaskId);
            if (!string.IsNullOrWhiteSpace(task.ClientId) &&
                _byClientId.TryGetValue(task.ClientId, out var mapped) &&
                ReferenceEquals(mapped, task))
            {
                _byClientId.Remove(task.ClientId);
            }

            TryDeletePdf(task);
        }

        return removed.Count;
    }

    private void AppendTaskCore(PrintTask task)
    {
        try
        {
            var directory = Path.GetDirectoryName(_tasksFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = JsonSerializer.Serialize(task, JsonConfig.Default) + Environment.NewLine;
            File.AppendAllText(_tasksFile, line, Encoding.UTF8);
        }
        catch
        {
            SaveAllCore();
            _appendsSinceCompaction = 0;
        }
    }

    private void TryDeletePdf(PrintTask task)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(task.PdfPath) && File.Exists(task.PdfPath))
            {
                File.Delete(task.PdfPath);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private void SaveAllCore()
    {
        try
        {
            Directory.CreateDirectory(_spoolDirectory);
            var directory = Path.GetDirectoryName(_tasksFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            foreach (var task in _tasks)
            {
                builder.Append(JsonSerializer.Serialize(task, JsonConfig.Default)).Append('\n');
            }

            var temp = _tasksFile + ".tmp";
            File.WriteAllText(temp, builder.ToString(), Encoding.UTF8);
            File.Move(temp, _tasksFile, true);
            _appendsSinceCompaction = 0;
        }
        catch
        {
            // 持久化失败不阻塞打印
        }
    }
}
