using System.Text.Json;

namespace PrintAssistant.Storage;

public sealed class AppSettings
{
    public int WsPort { get; set; } = 17800;
    public int WssPort { get; set; } = 17801;
    public int DefaultDpi { get; set; } = 203;
    public double PaperWidthMm { get; set; } = 100;
    public double PaperHeightMm { get; set; } = 150;
    public double OffsetXMm { get; set; } = 0;
    public double OffsetYMm { get; set; } = 0;
    public int MaxPdfBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxPages { get; set; } = 10;
    public int MaxCopies { get; set; } = 20;
    public int HistoryLimit { get; set; } = 500;
    public int HistoryDays { get; set; } = 30;
    public List<string> AllowedOrigins { get; set; } = new() { "*" };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonConfig.Default);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时使用默认值
        }

        var defaults = new AppSettings();
        defaults.Save(path);
        return defaults;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonConfig.Indented));
        File.Move(temp, path, true);
    }

    public void Normalize()
    {
        WsPort = WsPort is > 0 and < 65536 ? WsPort : 17800;
        WssPort = WssPort is > 0 and < 65536 ? WssPort : 17801;
        DefaultDpi = DefaultDpi is >= 72 and <= 600 ? DefaultDpi : 203;
        PaperWidthMm = PaperWidthMm is > 0 and <= 300 ? PaperWidthMm : 100;
        PaperHeightMm = PaperHeightMm is > 0 and <= 300 ? PaperHeightMm : 150;
        OffsetXMm = Math.Clamp(OffsetXMm, -50, 50);
        OffsetYMm = Math.Clamp(OffsetYMm, -50, 50);
        MaxPdfBytes = MaxPdfBytes is > 1024 and <= 64 * 1024 * 1024 ? MaxPdfBytes : 10 * 1024 * 1024;
        MaxPages = MaxPages is > 0 and <= 100 ? MaxPages : 10;
        MaxCopies = MaxCopies is > 0 and <= 100 ? MaxCopies : 20;
        HistoryLimit = HistoryLimit is > 0 and <= 10000 ? HistoryLimit : 500;
        HistoryDays = HistoryDays is > 0 and <= 3650 ? HistoryDays : 30;
        AllowedOrigins ??= new List<string> { "*" };
        if (AllowedOrigins.Count == 0)
        {
            AllowedOrigins.Add("*");
        }
    }
}
