using System.Diagnostics;
using System.Threading.Channels;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Printing;

public sealed class PrintQueue : IDisposable
{
    private readonly Channel<PrintTask> _channel;
    private readonly TaskRepository _repository;
    private readonly IPdfRenderer _renderer;
    private readonly ILabelPrinter _printer;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public PrintQueue(
        TaskRepository repository,
        IPdfRenderer renderer,
        ILabelPrinter printer,
        AppSettings settings)
    {
        _repository = repository;
        _renderer = renderer;
        _printer = printer;
        _settings = settings;
        _channel = Channel.CreateUnbounded<PrintTask>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public event Action<PrintTask>? TaskFinished;

    public int Pending => _channel.Reader.Count;

    public void Enqueue(PrintTask task)
    {
        if (!_channel.Writer.TryWrite(task))
        {
            throw new PrintException(ProtocolErrors.InternalError, "打印队列已关闭");
        }
    }

    public Task StartAsync()
    {
        _worker ??= Task.Run(() => ProcessLoopAsync(_stop.Token));
        return Task.CompletedTask;
    }

    public void Process(PrintTask task)
    {
        task.Status = PrintTaskStatus.Printing;
        task.StartedAt = DateTimeOffset.Now;
        _repository.Update(task);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var pages = _renderer.Render(task.PdfPath, _settings.DefaultDpi, _settings.MaxPages);
            try
            {
                var outcome = _printer.Print(
                    pages,
                    task.Printer,
                    task.Copies,
                    task.Paper,
                    task.OffsetXMm,
                    task.OffsetYMm,
                    _settings);
                task.Pages = pages.Count;
                task.Status = PrintTaskStatus.Done;
                task.Error = null;
                task.Message = null;
                foreach (var warning in outcome.Warnings)
                {
                    if (!task.Warnings.Contains(warning))
                    {
                        task.Warnings.Add(warning);
                    }
                }
            }
            finally
            {
                foreach (var page in pages)
                {
                    page.Dispose();
                }
            }
        }
        catch (PrintException ex)
        {
            task.Status = PrintTaskStatus.Failed;
            task.Error = ex.Code;
            task.Message = ex.Message;
        }
        catch (Exception ex)
        {
            task.Status = PrintTaskStatus.Failed;
            task.Error = ProtocolErrors.InternalError;
            task.Message = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            task.DurationMs = stopwatch.ElapsedMilliseconds;
            task.FinishedAt = DateTimeOffset.Now;
            _repository.Update(task);

            try
            {
                TaskFinished?.Invoke(task);
            }
            catch
            {
                // 忽略订阅者异常
            }
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var task in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                Process(task);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 忽略退出等待异常
        }

        _stop.Dispose();
    }
}
