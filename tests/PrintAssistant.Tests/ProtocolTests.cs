using System.Text;
using PrintAssistant.Server;
using PrintAssistant.Storage;

namespace PrintAssistant.Tests;

public sealed class ProtocolTests
{
    private static ClientMessage ValidMessage()
    {
        return new ClientMessage
        {
            Type = "print",
            Id = "client-1",
            Pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF")),
            Copies = 1,
            Paper = new PaperSpec(100, 150),
            Printer = "Printer A"
        };
    }

    private static ClientMessage ValidPreview()
    {
        return new ClientMessage
        {
            Type = "preview",
            Pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF")),
            Paper = new PaperSpec(76, 130),
            OffsetXMm = 1.5,
            OffsetYMm = -0.5
        };
    }

    [Fact]
    public void ValidPrint_IsAccepted()
    {
        var result = ProtocolValidation.ValidatePrint(ValidMessage(), new AppSettings());

        Assert.True(result.Ok);
        Assert.NotNull(result.Value);
        Assert.Equal("client-1", result.Value!.ClientId);
        Assert.Equal(1, result.Value.Copies);
    }

    [Fact]
    public void PrintOffsets_AreAccepted()
    {
        var message = ValidMessage();
        message.OffsetXMm = 2.5;
        message.OffsetYMm = -3;

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.True(result.Ok);
        Assert.Equal(2.5, result.Value!.OffsetXMm);
        Assert.Equal(-3, result.Value.OffsetYMm);
    }

    [Fact]
    public void PrintOffsets_DefaultToSettings()
    {
        var settings = new AppSettings { OffsetXMm = 1.25, OffsetYMm = -2.5 };

        var result = ProtocolValidation.ValidatePrint(ValidMessage(), settings);

        Assert.True(result.Ok);
        Assert.Equal(1.25, result.Value!.OffsetXMm);
        Assert.Equal(-2.5, result.Value.OffsetYMm);
    }

    [Fact]
    public void InvalidPrintOffset_ReturnsBadRequest()
    {
        var message = ValidMessage();
        message.OffsetXMm = 50.1;

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.BadRequest, result.Code);
    }

    [Fact]
    public void ValidPreview_IsAcceptedWithoutId()
    {
        var result = ProtocolValidation.ValidatePreview(ValidPreview(), new AppSettings());

        Assert.True(result.Ok);
        Assert.NotNull(result.Value);
        Assert.Equal(76, result.Value!.Paper.WidthMm);
        Assert.Equal(130, result.Value.Paper.HeightMm);
        Assert.Equal(1.5, result.Value.OffsetXMm);
        Assert.Equal(-0.5, result.Value.OffsetYMm);
    }

    [Fact]
    public void PreviewMissingPdf_ReturnsBadRequest()
    {
        var message = ValidPreview();
        message.Pdf = null;

        var result = ProtocolValidation.ValidatePreview(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.BadRequest, result.Code);
    }

    [Fact]
    public void PreviewInvalidPdf_ReturnsInvalidPdf()
    {
        var message = ValidPreview();
        message.Pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));

        var result = ProtocolValidation.ValidatePreview(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.InvalidPdf, result.Code);
    }

    [Fact]
    public void MissingId_ReturnsBadRequest()
    {
        var message = ValidMessage();
        message.Id = "";

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.BadRequest, result.Code);
    }

    [Fact]
    public void InvalidBase64_ReturnsBadRequest()
    {
        var message = ValidMessage();
        message.Pdf = "not base64 !!!";

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.BadRequest, result.Code);
    }

    [Fact]
    public void NonPdfPayload_ReturnsInvalidPdf()
    {
        var message = ValidMessage();
        message.Pdf = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello world"));

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.InvalidPdf, result.Code);
    }

    [Fact]
    public void OversizedPayload_ReturnsPayloadTooLarge()
    {
        var message = ValidMessage();
        var payload = new byte[4096];
        Encoding.ASCII.GetBytes("%PDF-1.7").CopyTo(payload, 0);
        message.Pdf = Convert.ToBase64String(payload);
        var settings = new AppSettings { MaxPdfBytes = 1024 };

        var result = ProtocolValidation.ValidatePrint(message, settings);

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.PayloadTooLarge, result.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void InvalidCopies_ReturnsBadRequest(int copies)
    {
        var message = ValidMessage();
        message.Copies = copies;

        var result = ProtocolValidation.ValidatePrint(message, new AppSettings());

        Assert.False(result.Ok);
        Assert.Equal(ProtocolErrors.BadRequest, result.Code);
    }

    [Fact]
    public void MissingPaper_UsesSettingsDefaults()
    {
        var message = ValidMessage();
        message.Paper = null;
        var settings = new AppSettings { PaperWidthMm = 110, PaperHeightMm = 160 };

        var result = ProtocolValidation.ValidatePrint(message, settings);

        Assert.True(result.Ok);
        Assert.Equal(110, result.Value!.Paper.WidthMm);
        Assert.Equal(160, result.Value.Paper.HeightMm);
    }
}
