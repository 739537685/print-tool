using PrintAssistant.Server;

namespace PrintAssistant.Tests;

public sealed class AuthServiceTests : IDisposable
{
    private readonly string _root;

    public AuthServiceTests()
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
    public void CorrectPairCode_IssuesTokenThatCanBeValidated()
    {
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));

        var paired = auth.TryPair(auth.PairCode, "web", out var token, out var error);

        Assert.True(paired);
        Assert.Null(error);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(auth.TryValidateToken(token, out var clientName));
        Assert.Equal("web", clientName);
    }

    [Fact]
    public void WrongPairCode_IsRejected()
    {
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));

        var paired = auth.TryPair("000000", "web", out var token, out var error);

        Assert.False(paired);
        Assert.Null(token);
        Assert.Equal(ProtocolErrors.Unauthorized, error);
    }

    [Fact]
    public void RefreshedCode_InvalidatesPreviousCode()
    {
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));
        var oldCode = auth.PairCode;
        var newCode = auth.RefreshPairCode();

        Assert.NotEqual(oldCode, newCode);
        Assert.False(auth.TryPair(oldCode, "web", out _, out _));
    }

    [Fact]
    public void MoreThanFiveFailures_TriggersRateLimit()
    {
        var auth = new AuthService(Path.Combine(_root, "tokens.json"));
        for (var i = 0; i < 5; i++)
        {
            Assert.False(auth.TryPair("999999", "web", out _, out _));
        }

        var paired = auth.TryPair(auth.PairCode, "web", out _, out var error);

        Assert.False(paired);
        Assert.Equal(ProtocolErrors.RateLimited, error);
    }

    [Fact]
    public void TokenPersistsAcrossInstances()
    {
        var file = Path.Combine(_root, "tokens.json");
        var auth = new AuthService(file);
        Assert.True(auth.TryPair(auth.PairCode, "web", out var token, out _));

        var reloaded = new AuthService(file);

        Assert.True(reloaded.TryValidateToken(token, out _));
    }
}
