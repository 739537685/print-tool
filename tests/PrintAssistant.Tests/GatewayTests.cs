using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PrintAssistant.Printing;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

public sealed class GatewayTests : IDisposable
{
    private readonly string _root;

    public GatewayTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PrintAssistantTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    [Fact]
    public async Task PairThenRejectInvalidPdfAndUnknownPrinter()
    {
        var settings = new AppSettings
        {
            WsPort = GetFreePort(),
            WssPort = GetFreePort()
        };
        var repository = new TaskRepository(Path.Combine(_root, "tasks.jsonl"), Path.Combine(_root, "spool"), 50, 30);
        repository.Initialize();
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));
        var queue = new PrintQueue(repository, new FakePdfRenderer(), new FakeLabelPrinter(), settings);
        var preview = new PreviewService(new FakePdfRenderer(), Path.Combine(_root, "spool"), settings.MaxPages);
        var gateway = new Gateway(
            settings,
            auth,
            repository,
            new PrinterCatalog(() => Array.Empty<string>()),
            queue,
            new TlsCertificateService(),
            preview);

        try
        {
            await gateway.StartAsync();
            using var socket = new ClientWebSocket();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.WsPort}/ws"), timeout.Token);

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "printers"
            }), timeout.Token);
            var unauthorized = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("error", unauthorized.GetProperty("type").GetString());
            Assert.Equal(ProtocolErrors.Unauthorized, unauthorized.GetProperty("code").GetString());

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "pair",
                code = auth.PairCode,
                clientName = "test"
            }), timeout.Token);
            var paired = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("pair_ok", paired.GetProperty("type").GetString());
            var token = paired.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(token));

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "print",
                id = "print-1",
                printer = "Printer A",
                pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a pdf"))
            }), timeout.Token);
            var invalidPdf = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("error", invalidPdf.GetProperty("type").GetString());
            Assert.Equal(ProtocolErrors.InvalidPdf, invalidPdf.GetProperty("code").GetString());

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "preview",
                pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a pdf"))
            }), timeout.Token);
            var invalidPreview = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("error", invalidPreview.GetProperty("type").GetString());
            Assert.Equal(ProtocolErrors.InvalidPdf, invalidPreview.GetProperty("code").GetString());

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "preview",
                pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n%%EOF")),
                paper = new { widthMm = 100, heightMm = 150 },
                offsetXMm = 1.5,
                offsetYMm = -0.5
            }), timeout.Token);
            var previewResponse = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("preview", previewResponse.GetProperty("type").GetString());
            Assert.Equal(150, previewResponse.GetProperty("dpi").GetInt32());
            Assert.Equal(1.5, previewResponse.GetProperty("offsetXMm").GetDouble());
            Assert.Equal(-0.5, previewResponse.GetProperty("offsetYMm").GetDouble());
            var previewPages = previewResponse.GetProperty("pages");
            Assert.Equal(1, previewPages.GetArrayLength());
            Assert.Equal(591, previewPages[0].GetProperty("widthPx").GetInt32());
            Assert.Equal(886, previewPages[0].GetProperty("heightPx").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(previewPages[0].GetProperty("png").GetString()));

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "preview",
                pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n%%EOF")),
                offsetXMm = 51
            }), timeout.Token);
            var badOffset = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("error", badOffset.GetProperty("type").GetString());
            Assert.Equal(ProtocolErrors.BadRequest, badOffset.GetProperty("code").GetString());

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "print",
                id = "print-2",
                printer = "Printer A",
                pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n%%EOF"))
            }), timeout.Token);
            var printerMissing = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("error", printerMissing.GetProperty("type").GetString());
            Assert.Equal(ProtocolErrors.PrinterNotFound, printerMissing.GetProperty("code").GetString());

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "hello",
                token
            }), timeout.Token);
            var hello = await ReceiveAsync(socket, timeout.Token);
            Assert.Equal("hello_ok", hello.GetProperty("type").GetString());
        }
        finally
        {
            await gateway.DisposeAsync();
            queue.Dispose();
        }
    }

    [Fact]
    public async Task OptionsAndPnaRequests_ReturnCorrectHeaders()
    {
        var settings = new AppSettings
        {
            WsPort = GetFreePort(),
            WssPort = GetFreePort()
        };
        var repository = new TaskRepository(Path.Combine(_root, "tasks.jsonl"), Path.Combine(_root, "spool"), 50, 30);
        repository.Initialize();
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));
        var queue = new PrintQueue(repository, new FakePdfRenderer(), new FakeLabelPrinter(), settings);
        var preview = new PreviewService(new FakePdfRenderer(), Path.Combine(_root, "spool"), settings.MaxPages);
        var gateway = new Gateway(
            settings,
            auth,
            repository,
            new PrinterCatalog(() => Array.Empty<string>()),
            queue,
            new TlsCertificateService(),
            preview);

        try
        {
            await gateway.StartAsync();
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{settings.WsPort}/");
            request.Headers.Add("Origin", "https://shop.example.com");
            request.Headers.Add("Access-Control-Request-Private-Network", "true");

            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(response.Headers.Contains("Access-Control-Allow-Private-Network"));
            Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").First());
            Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.Equal("https://shop.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").First());
        }
        finally
        {
            await gateway.DisposeAsync();
            queue.Dispose();
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
