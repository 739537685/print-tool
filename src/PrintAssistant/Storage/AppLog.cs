namespace PrintAssistant.Storage;

public static class AppLog
{
    private static readonly object Gate = new();
    private static string _directory = AppPaths.LogsDir;

    public static event Action<string>? LogWritten;

    public static void Initialize(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        Cleanup();
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响业务
            }
        }

        try
        {
            LogWritten?.Invoke(line);
        }
        catch
        {
            // 忽略订阅者异常
        }
    }

    private static void Cleanup()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 忽略清理异常
        }
    }
}
