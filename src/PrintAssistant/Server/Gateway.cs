using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintAssistant.Printing;
using PrintAssistant.Storage;

namespace PrintAssistant.Server;

public sealed class Gateway : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly AuthService _auth;
    private readonly TaskRepository _repository;
    private readonly PrinterCatalog _printers;
    private readonly PrintQueue _queue;
    private readonly TlsCertificateService _tls;
    private readonly PreviewService? _preview;
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Connection>> _waiters = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WebApplication? _application;

    public Gateway(
        AppSettings settings,
        AuthService auth,
        TaskRepository repository,
        PrinterCatalog printers,
        PrintQueue queue,
        TlsCertificateService tls,
        PreviewService? preview = null)
    {
        _settings = settings;
        _auth = auth;
        _repository = repository;
        _printers = printers;
        _queue = queue;
        _tls = tls;
        _preview = preview;
        _queue.TaskFinished += OnTaskFinished;
    }

    public bool IsRunning => _application != null;
    public bool WssEnabled => _tls.Certificate != null;
    public string? LastError { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ApplicationName = "PrintAssistant",
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Listen(IPAddress.Loopback, _settings.WsPort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                });

                if (_tls.Certificate != null)
                {
                    options.Listen(IPAddress.Loopback, _settings.WssPort, listen =>
                    {
                        listen.Protocols = HttpProtocols.Http1;
                        listen.UseHttps(_tls.Certificate);
                    });
                }
            });

            var application = builder.Build();
            application.Use(async (httpContext, next) =>
            {
                if (httpContext.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                {
                    httpContext.Response.Headers.Append("Access-Control-Allow-Private-Network", "true");
                }

                var origin = httpContext.Request.Headers.Origin.ToString();
                if (!string.IsNullOrEmpty(origin) && IsOriginAllowed(origin))
                {
                    httpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);
                    httpContext.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    httpContext.Response.Headers.Append("Access-Control-Allow-Headers", "*");
                }

                if (HttpMethods.IsOptions(httpContext.Request.Method))
                {
                    httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                await next();
            });
            application.UseWebSockets(new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });
            application.MapGet("/", () => Results.Text("PrintAssistant OK", "text/plain", Encoding.UTF8));
            application.Map("/ws", HandleWebSocketAsync);

            await application.StartAsync(_lifetime.Token);
            _application = application;
            LastError = null;
            AppLog.Info($"WebSocket 监听：ws://127.0.0.1:{_settings.WsPort}" +
                        (WssEnabled ? $" / wss://127.0.0.1:{_settings.WssPort}" : "（wss 未启用）"));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error($"WebSocket 启动失败：{ex.Message}");
            throw;
        }
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket required");
            return;
        }

        if (!IsOriginAllowed(context.Request.Headers.Origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new Connection(socket);
        _connections[connection.Id] = connection;

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            var receiveTask = ReceiveLoopAsync(connection, connectionCts.Token);
            var authTimeout = Task.Delay(TimeSpan.FromSeconds(10), connectionCts.Token);
            var first = await Task.WhenAny(receiveTask, authTimeout);
            if (first == authTimeout && !connection.Authenticated)
            {
                await TrySendAsync(connection, new ErrorResult(null, ProtocolErrors.Unauthorized, "配对超时"), connectionCts.Token);
                connectionCts.Cancel();
            }

            await receiveTask;
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            AppLog.Error($"WebSocket 连接异常：{ex.Message}");
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            RemoveConnectionWaiters(connection);
            connection.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var messageBuffer = new MemoryStream();
        var maxMessageBytes = (long)_settings.MaxPdfBytes * 4 / 3 + 256 * 1024;

        while (connection.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch
                {
                    // 忽略关闭异常
                }

                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                await TrySendAsync(connection, new ErrorResult(null, ProtocolErrors.BadRequest, "仅支持文本帧"), cancellationToken);
                break;
            }

            if (messageBuffer.Length + result.Count > maxMessageBytes)
            {
                await TrySendAsync(connection, new ErrorResult(null, ProtocolErrors.PayloadTooLarge, "消息超过大小限制"), cancellationToken);
                break;
            }

            messageBuffer.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
            messageBuffer.SetLength(0);

            if (!CheckRateLimit(connection))
            {
                await TrySendAsync(connection, new ErrorResult(null, ProtocolErrors.RateLimited, "请求过于频繁"), cancellationToken);
                break;
            }

            try
            {
                await DispatchAsync(connection, text, cancellationToken);
            }
            catch (PrintException ex)
            {
                await TrySendAsync(connection, new ErrorResult(null, ex.Code, ex.Message), cancellationToken);
            }
            catch (Exception ex)
            {
                AppLog.Error($"处理消息失败：{ex.Message}");
                await TrySendAsync(connection, new ErrorResult(null, ProtocolErrors.InternalError, ex.Message), cancellationToken);
            }
        }
    }

    private async Task DispatchAsync(Connection connection, string text, CancellationToken cancellationToken)
    {
        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClientMessage>(text, JsonConfig.Default);
        }
        catch (JsonException)
        {
            await connection.SendAsync(new ErrorResult(null, ProtocolErrors.BadRequest, "消息不是合法 JSON"), cancellationToken);
            return;
        }

        if (message == null || string.IsNullOrWhiteSpace(message.Type))
        {
            await connection.SendAsync(new ErrorResult(null, ProtocolErrors.BadRequest, "缺少消息类型"), cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case "pair":
                await HandlePairAsync(connection, message, cancellationToken);
                return;

            case "hello":
                await HandleHelloAsync(connection, message, cancellationToken);
                return;
        }

        if (!connection.Authenticated)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.Unauthorized, "请先配对或发送 hello"),
                cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case "printers":
                await connection.SendAsync(new PrintersResult(message.ReqId, _printers.GetPrinters()), cancellationToken);
                break;

            case "print":
                await HandlePrintAsync(connection, message, cancellationToken);
                break;

            case "preview":
                await HandlePreviewAsync(connection, message, cancellationToken);
                break;

            case "history":
                var views = _repository.Recent(message.Limit ?? 50)
                    .Select(TaskView.FromTask)
                    .ToList();
                await connection.SendAsync(new HistoryResult(message.ReqId, views), cancellationToken);
                break;

            case "reprint":
                await HandleReprintAsync(connection, message, cancellationToken);
                break;

            case "ping":
                await connection.SendAsync(new PongResult(), cancellationToken);
                break;

            default:
                await connection.SendAsync(
                    new ErrorResult(message.ReqId, ProtocolErrors.BadRequest, $"未知消息类型：{message.Type}"),
                    cancellationToken);
                break;
        }
    }

    private async Task HandlePairAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken)
    {
        if (!_auth.TryPair(message.Code, message.ClientName, out var token, out var error))
        {
            var detail = error == ProtocolErrors.RateLimited ? "尝试次数过多，请稍后再试" : "配对码错误";
            await connection.SendAsync(
                new ErrorResult(message.ReqId, error ?? ProtocolErrors.Unauthorized, detail),
                cancellationToken);
            return;
        }

        connection.Authenticated = true;
        connection.ClientName = message.ClientName ?? "web";
        await connection.SendAsync(new PairOkResult(token!, AppInfo.Version), cancellationToken);
        AppLog.Info($"网页已配对：{connection.ClientName}");
    }

    private async Task HandleHelloAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken)
    {
        if (!_auth.TryValidateToken(message.Token, out var clientName))
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.Unauthorized, "token 无效或已撤销"),
                cancellationToken);
            return;
        }

        connection.Authenticated = true;
        connection.ClientName = clientName;
        await connection.SendAsync(
            new HelloOkResult(1, AppInfo.Version, _printers.GetPrinters()),
            cancellationToken);
    }

    private async Task HandlePrintAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken)
    {
        var validation = ProtocolValidation.ValidatePrint(message, _settings);
        if (!validation.Ok || validation.Value == null)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, validation.Code ?? ProtocolErrors.BadRequest, validation.Message ?? "请求不合法"),
                cancellationToken);
            return;
        }

        var request = validation.Value;
        var existing = _repository.FindByClientId(request.ClientId);
        if (existing != null)
        {
            await connection.SendAsync(
                new AcceptedResult(message.Id!, message.ReqId, existing.TaskId, true),
                cancellationToken);

            if (existing.Status is PrintTaskStatus.Done or PrintTaskStatus.Failed or PrintTaskStatus.Interrupted)
            {
                await connection.SendAsync(ToResult(existing), cancellationToken);
            }
            else
            {
                RegisterWaiter(existing.TaskId, connection);
            }

            return;
        }

        string printerName;
        try
        {
            printerName = _printers.ResolvePrinterName(message.Printer);
        }
        catch (PrintException ex)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ex.Code, ex.Message),
                cancellationToken);
            return;
        }

        PrintTask task;
        try
        {
            task = JobFactory.CreateFromRequest(
                AppPaths.SpoolDir,
                request.ClientId,
                message.ReqId,
                printerName,
                request.Copies,
                request.Paper,
                request.OffsetXMm,
                request.OffsetYMm,
                request.Pdf,
                request.Meta);
        }
        catch (Exception ex)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.InternalError, $"保存 PDF 失败：{ex.Message}"),
                cancellationToken);
            return;
        }

        _repository.Add(task);
        RegisterWaiter(task.TaskId, connection);
        await connection.SendAsync(
            new AcceptedResult(request.ClientId, message.ReqId, task.TaskId),
            cancellationToken);
        _queue.Enqueue(task);
        AppLog.Info($"收到打印任务 {task.TaskId}，打印机 {task.Printer}，{task.Copies} 份");
    }

    private async Task HandlePreviewAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken)
    {
        if (_preview == null)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.InternalError, "预览功能未启用"),
                cancellationToken);
            return;
        }

        var validation = ProtocolValidation.ValidatePreview(message, _settings);
        if (!validation.Ok || validation.Value == null)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, validation.Code ?? ProtocolErrors.BadRequest, validation.Message ?? "请求不合法"),
                cancellationToken);
            return;
        }

        var request = validation.Value;
        PreviewOutcome outcome;
        try
        {
            outcome = _preview.Render(request.Pdf, request.Paper, request.OffsetXMm, request.OffsetYMm);
        }
        catch (PrintException ex)
        {
            await connection.SendAsync(new ErrorResult(message.ReqId, ex.Code, ex.Message), cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error($"生成预览失败：{ex.Message}");
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.InternalError, $"生成预览失败：{ex.Message}"),
                cancellationToken);
            return;
        }

        var pages = outcome.Pages
            .Select(page => new PreviewPage(page.WidthPx, page.HeightPx, Convert.ToBase64String(page.Png)))
            .ToList();
        await connection.SendAsync(
            new PreviewResult(
                message.ReqId,
                outcome.Dpi,
                request.Paper.WidthMm,
                request.Paper.HeightMm,
                request.OffsetXMm,
                request.OffsetYMm,
                pages),
            cancellationToken);
    }

    private async Task HandleReprintAsync(Connection connection, ClientMessage message, CancellationToken cancellationToken)
    {
        var original = _repository.Find(message.TaskId ?? "");
        if (original == null)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.BadRequest, "任务不存在"),
                cancellationToken);
            return;
        }

        string printerName;
        try
        {
            printerName = _printers.ResolvePrinterName(original.Printer);
        }
        catch (PrintException ex)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ex.Code, ex.Message),
                cancellationToken);
            return;
        }

        PrintTask task;
        try
        {
            task = JobFactory.CreateReprint(AppPaths.SpoolDir, original);
            task.Printer = printerName;
            task.ReqId = message.ReqId;
        }
        catch (Exception ex)
        {
            await connection.SendAsync(
                new ErrorResult(message.ReqId, ProtocolErrors.BadRequest, ex.Message),
                cancellationToken);
            return;
        }

        _repository.Add(task);
        RegisterWaiter(task.TaskId, connection);
        await connection.SendAsync(
            new AcceptedResult(null, message.ReqId, task.TaskId),
            cancellationToken);
        _queue.Enqueue(task);
        AppLog.Info($"重打任务 {task.TaskId}（原任务 {original.TaskId}）");
    }

    private void RegisterWaiter(string taskId, Connection connection)
    {
        var bag = _waiters.GetOrAdd(taskId, _ => new ConcurrentDictionary<Guid, Connection>());
        bag[connection.Id] = connection;
    }

    private void RemoveConnectionWaiters(Connection connection)
    {
        foreach (var pair in _waiters)
        {
            pair.Value.TryRemove(connection.Id, out _);
            if (pair.Value.IsEmpty)
            {
                _waiters.TryRemove(pair.Key, out _);
            }
        }
    }

    private void OnTaskFinished(PrintTask task)
    {
        if (!_waiters.TryRemove(task.TaskId, out var waiters))
        {
            return;
        }

        foreach (var connection in waiters.Values)
        {
            _ = SendResultAsync(connection, task);
        }
    }

    private async Task SendResultAsync(Connection connection, PrintTask task)
    {
        try
        {
            await connection.SendAsync(ToResult(task), _lifetime.Token);
        }
        catch
        {
            // 客户端已断开，结果仍可从 history 查询
        }
    }

    private static ResultMessage ToResult(PrintTask task)
    {
        return new ResultMessage(
            task.ClientId,
            task.TaskId,
            task.Status,
            task.Pages,
            task.DurationMs,
            task.Warnings.ToList(),
            task.Error,
            task.Message);
    }

    private static async Task TrySendAsync(Connection connection, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(payload, cancellationToken);
        }
        catch
        {
            // 连接已关闭
        }
    }

    private bool IsOriginAllowed(string? origin)
    {
        var allowed = _settings.AllowedOrigins;
        if (allowed.Count == 0 || allowed.Contains("*", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        return allowed.Any(item => string.Equals(item, origin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CheckRateLimit(Connection connection)
    {
        var now = DateTimeOffset.Now;
        if (now - connection.WindowStart > TimeSpan.FromSeconds(10))
        {
            connection.WindowStart = now;
            connection.MessageCount = 0;
        }

        connection.MessageCount++;
        return connection.MessageCount <= 60;
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        if (_application != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _application.StopAsync(timeout.Token);
            }
            catch
            {
                // 忽略关闭异常
            }
        }

        foreach (var connection in _connections.Values)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                if (connection.Socket.State == WebSocketState.Open)
                {
                    await connection.Socket.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "assistant shutting down",
                        timeout.Token);
                }
            }
            catch
            {
                // 忽略关闭异常
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.TaskFinished -= OnTaskFinished;
        await StopAsync();
        if (_application != null)
        {
            await _application.DisposeAsync();
            _application = null;
        }

        _lifetime.Dispose();
    }

    private sealed class Connection : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Connection(WebSocket socket)
        {
            Socket = socket;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        public bool Authenticated { get; set; }
        public string ClientName { get; set; } = "";
        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.Now;
        public int MessageCount { get; set; }

        public async Task SendAsync(object payload, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(payload, JsonConfig.Default);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State == WebSocketState.Open)
                {
                    await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            Socket.Dispose();
        }
    }
}
