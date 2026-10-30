using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PrintAssistant.Server;

public sealed class TlsCertificateService
{
    private const string CertFileName = "server.pfx";
    private const string SubjectName = "CN=PrintAssistant Local";
    private readonly Action<string>? _log;

    public TlsCertificateService(Action<string>? log = null)
    {
        _log = log;
    }

    public X509Certificate2? Certificate { get; private set; }
    public bool Trusted { get; private set; }
    public string? Error { get; private set; }

    public void Ensure(string certDirectory)
    {
        try
        {
            Directory.CreateDirectory(certDirectory);
            var pfxPath = Path.Combine(certDirectory, CertFileName);
            var certificate = LoadOrCreate(pfxPath);
            Certificate = certificate;

            try
            {
                Trusted = EnsureTrusted(certificate);
                if (Trusted)
                {
                    _log?.Invoke("本地 HTTPS 证书已就绪，可从 https 页面连接 wss://127.0.0.1");
                }
            }
            catch (Exception ex)
            {
                Trusted = false;
                _log?.Invoke($"证书信任失败，wss 可能被浏览器拦截：{ex.Message}");
            }

            Error = null;
        }
        catch (Exception ex)
        {
            Certificate = null;
            Trusted = false;
            Error = ex.Message;
            _log?.Invoke($"TLS 证书准备失败，仅启用 ws：{ex.Message}");
        }
    }

    private X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = new X509Certificate2(pfxPath);
                if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
                {
                    return existing;
                }

                existing.Dispose();
            }
            catch
            {
                // 证书损坏，重新生成
            }
        }

        using var created = CreateSelfSigned();
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pfx));
        _log?.Invoke("已生成本地自签 HTTPS 证书");
        return new X509Certificate2(pfxPath);
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            false));

        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("localhost");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        alternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2));
    }

    private static bool EnsureTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
        if (existing.Count > 0)
        {
            return true;
        }

        using var publicCertificate = new X509Certificate2(certificate.Export(X509ContentType.Cert));
        store.Add(publicCertificate);
        return true;
    }
}
