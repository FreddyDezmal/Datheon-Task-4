using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>Throwaway self-signed certificates, in the form the app reads them: a base64 .pfx and its password.</summary>
internal static class TestCertificates
{
    public const string Password = "test-certificate-password";

    private static readonly Lazy<string> SharedA = new(() => CreatePfx("Storefront test A"));
    private static readonly Lazy<string> SharedB = new(() => CreatePfx("Storefront test B"));

    /// <summary>Two distinct certificates, created once per test run (RSA key generation is slow).</summary>
    public static string A => SharedA.Value;

    public static string B => SharedB.Value;

    public static string CreatePfx(string subject, int keySize = 2048, string? password = Password, bool includePrivateKey = true)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        if (!includePrivateKey)
        {
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            return Convert.ToBase64String(publicOnly.Export(X509ContentType.Pfx, password));
        }

        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password));
    }

    /// <summary>App settings that encrypt the key ring with <paramref name="current"/>, and decrypt with <paramref name="previous"/> too.</summary>
    public static Dictionary<string, string?> Settings(string current, params string[] previous)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataProtection:Certificate:Pfx"] = current,
            ["DataProtection:Certificate:Password"] = Password,
        };
        for (var i = 0; i < previous.Length; i++)
        {
            settings[$"DataProtection:PreviousCertificates:{i}:Pfx"] = previous[i];
            settings[$"DataProtection:PreviousCertificates:{i}:Password"] = Password;
        }

        return settings;
    }
}
