using System.Drawing.Printing;
using PrintAssistant.Server;

namespace PrintAssistant.Printing;

public sealed record PaperInfo(string Name, double WidthMm, double HeightMm);

public sealed record PrinterInfo(string Name, bool IsDefault, string Status, IReadOnlyList<PaperInfo> Papers);

public sealed class PrinterCatalog
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(15);
    private readonly Func<IEnumerable<string>> _installed;
    private readonly object _gate = new();
    private IReadOnlyList<PrinterInfo>? _cached;
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;

    public PrinterCatalog()
        : this(() => PrinterSettings.InstalledPrinters.Cast<string>())
    {
    }

    public PrinterCatalog(Func<IEnumerable<string>> installed)
    {
        _installed = installed;
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
            _cacheExpiresAt = DateTimeOffset.MinValue;
        }
    }

    public IReadOnlyList<PrinterInfo> GetPrinters(bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (!forceRefresh && _cached != null && DateTimeOffset.Now < _cacheExpiresAt)
            {
                return _cached;
            }

            var result = LoadPrintersCore();
            _cached = result;
            _cacheExpiresAt = DateTimeOffset.Now.Add(CacheDuration);
            return result;
        }
    }

    private IReadOnlyList<PrinterInfo> LoadPrintersCore()
    {
        var result = new List<PrinterInfo>();
        string? defaultName = null;
        try
        {
            defaultName = new PrinterSettings().PrinterName;
        }
        catch
        {
            // 忽略默认打印机读取失败
        }

        List<string> names;
        try
        {
            names = _installed().Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }
        catch
        {
            return result;
        }

        foreach (var name in names)
        {
            var papers = new List<PaperInfo>();
            var status = "ready";
            try
            {
                var settings = new PrinterSettings { PrinterName = name };
                if (!settings.IsValid)
                {
                    status = "unavailable";
                }

                foreach (PaperSize paper in settings.PaperSizes)
                {
                    papers.Add(new PaperInfo(
                        paper.PaperName ?? "未命名",
                        Math.Round(PaperMath.HundredthsInchToMm(paper.Width), 1),
                        Math.Round(PaperMath.HundredthsInchToMm(paper.Height), 1)));
                    if (papers.Count >= 100)
                    {
                        break;
                    }
                }
            }
            catch
            {
                status = "unavailable";
            }

            result.Add(new PrinterInfo(
                name,
                string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
                status,
                papers));
        }

        return result;
    }

    public string ResolvePrinterName(string? requested)
    {
        List<string> installed;
        try
        {
            installed = _installed().Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }
        catch (Exception ex)
        {
            throw new PrintException(ProtocolErrors.PrinterNotFound, $"无法读取打印机列表：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(requested))
        {
            string? defaultName = null;
            try
            {
                defaultName = new PrinterSettings().PrinterName;
            }
            catch
            {
                // 忽略
            }

            var match = installed.FirstOrDefault(n =>
                string.Equals(n, defaultName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new PrintException(ProtocolErrors.PrinterNotFound, "未找到默认打印机");
            }

            return match;
        }

        var resolved = installed.FirstOrDefault(n =>
            string.Equals(n, requested, StringComparison.OrdinalIgnoreCase));
        if (resolved == null)
        {
            throw new PrintException(ProtocolErrors.PrinterNotFound, $"未找到打印机：{requested}");
        }

        return resolved;
    }
}
