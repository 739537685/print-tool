using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PrintAssistant.Storage;

namespace PrintAssistant.Server;

public sealed class AuthService
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private const int MaxAttemptsPerWindow = 5;

    private readonly string _tokensFile;
    private readonly object _gate = new();
    private readonly Dictionary<string, TokenRecord> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _attempts = new();
    private string _pairCode = GeneratePairCode();

    public AuthService(string tokensFile)
    {
        _tokensFile = tokensFile;
        LoadTokens();
    }

    public string PairCode
    {
        get
        {
            lock (_gate)
            {
                return _pairCode;
            }
        }
    }

    public int PairedClients
    {
        get
        {
            lock (_gate)
            {
                return _tokens.Count;
            }
        }
    }

    public string RefreshPairCode()
    {
        lock (_gate)
        {
            _pairCode = GeneratePairCode();
            return _pairCode;
        }
    }

    public bool TryPair(string? code, string? clientName, out string? token, out string? error)
    {
        token = null;
        error = null;

        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            while (_attempts.Count > 0 && now - _attempts.Peek() > AttemptWindow)
            {
                _attempts.Dequeue();
            }

            if (_attempts.Count >= MaxAttemptsPerWindow)
            {
                error = ProtocolErrors.RateLimited;
                return false;
            }

            _attempts.Enqueue(now);

            if (!FixedEquals(code?.Trim() ?? "", _pairCode))
            {
                error = ProtocolErrors.Unauthorized;
                return false;
            }

            _attempts.Clear();
            token = GenerateToken();
            var name = string.IsNullOrWhiteSpace(clientName) ? "web" : clientName.Trim();
            if (name.Length > 64)
            {
                name = name[..64];
            }

            _tokens[HashToken(token)] = new TokenRecord
            {
                ClientName = name,
                CreatedAt = now,
                LastUsedAt = now
            };
            SaveTokens();
            return true;
        }
    }

    public bool TryValidateToken(string? token, out string clientName)
    {
        clientName = "";
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_tokens.TryGetValue(HashToken(token), out var record))
            {
                return false;
            }

            record.LastUsedAt = DateTimeOffset.Now;
            clientName = record.ClientName;
            SaveTokens();
            return true;
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _tokens.Clear();
            SaveTokens();
        }
    }

    private void LoadTokens()
    {
        try
        {
            if (!File.Exists(_tokensFile))
            {
                return;
            }

            var records = JsonSerializer.Deserialize<Dictionary<string, TokenRecord>>(
                File.ReadAllText(_tokensFile),
                JsonConfig.Default);
            if (records == null)
            {
                return;
            }

            foreach (var pair in records)
            {
                _tokens[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // 损坏则从空开始
        }
    }

    private void SaveTokens()
    {
        try
        {
            var directory = Path.GetDirectoryName(_tokensFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _tokensFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_tokens, JsonConfig.Indented));
            File.Move(temp, _tokensFile, true);
        }
        catch
        {
            // 忽略持久化失败
        }
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string GeneratePairCode()
    {
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    private static string GenerateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    public sealed class TokenRecord
    {
        public string ClientName { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastUsedAt { get; set; }
    }
}
