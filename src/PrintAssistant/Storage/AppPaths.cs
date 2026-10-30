namespace PrintAssistant.Storage;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrintAssistant");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string TokensFile => Path.Combine(Root, "tokens.json");
    public static string TasksFile => Path.Combine(Root, "tasks.jsonl");
    public static string SpoolDir => Path.Combine(Root, "spool");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string CertsDir => Path.Combine(Root, "certs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SpoolDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CertsDir);
    }
}
