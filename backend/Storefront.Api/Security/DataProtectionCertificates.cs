using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Storefront.Api.Security;

/// <summary>A certificate supplied as configuration: a base64-encoded PKCS#12 (.pfx) file and its password.</summary>
public sealed class DataProtectionCertificateSettings
{
    /// <summary>The .pfx file, base64-encoded (what tools/new-data-protection-certificate.cs writes).</summary>
    public string? Pfx { get; set; }

    public string? Password { get; set; }
}

/// <summary>
/// Settings (section "DataProtection") for encrypting the Data Protection key ring at rest.
///
/// The key ring signs every bearer and refresh token and is stored in the database
/// (DataProtectionKeys). Unencrypted, anyone who can read that table can forge sign-ins. With a
/// certificate, each key is encrypted with the certificate's public key before it is written, and
/// only a host holding the private key can use it, so a copy of the database alone is useless.
///
/// Outside Development a certificate is required; the app refuses to start without one, rather than
/// quietly writing readable keys. <see cref="AllowUnencryptedKeys"/> is the explicit opt-out.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary>Encrypts new keys and decrypts existing ones.</summary>
    public DataProtectionCertificateSettings? Certificate { get; set; }

    /// <summary>Decrypt-only: certificates that encrypted existing keys, kept while rotating to a new one.</summary>
    public List<DataProtectionCertificateSettings> PreviousCertificates { get; set; } = [];

    /// <summary>Lets the app run outside Development without a certificate. Keys are then stored readable.</summary>
    public bool AllowUnencryptedKeys { get; set; }
}

/// <summary>The certificates read from <see cref="DataProtectionSettings"/>, or none (keys stored unencrypted).</summary>
public sealed class DataProtectionCertificates
{
    private DataProtectionCertificates(X509Certificate2? current, IReadOnlyList<X509Certificate2> previous)
    {
        Current = current;
        Previous = previous;
    }

    public X509Certificate2? Current { get; }

    public IReadOnlyList<X509Certificate2> Previous { get; }

    /// <summary>Reads and validates the certificates. Messages never include the certificate or its password.</summary>
    public static DataProtectionCertificates Read(IConfiguration configuration, bool isDevelopment)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>()
                       ?? new DataProtectionSettings();
        var previousSettings = settings.PreviousCertificates.Where(c => !string.IsNullOrWhiteSpace(c.Pfx)).ToList();

        if (string.IsNullOrWhiteSpace(settings.Certificate?.Pfx))
        {
            if (previousSettings.Count > 0)
            {
                throw new InvalidOperationException(
                    "DataProtection:PreviousCertificates is set but DataProtection:Certificate:Pfx is not. " +
                    "Previous certificates only decrypt; a current certificate is needed to encrypt new keys.");
            }

            if (isDevelopment || settings.AllowUnencryptedKeys)
            {
                return new DataProtectionCertificates(null, []);
            }

            throw new InvalidOperationException(
                "DataProtection:Certificate:Pfx is required outside Development, so the sign-in keys are encrypted " +
                "in the database. Create one with tools/new-data-protection-certificate.cs " +
                "(Documentation/Deployment.md, \"Encrypting the sign-in keys\"), or set " +
                "DataProtection:AllowUnencryptedKeys=true to run without it.");
        }

        var current = Load(settings.Certificate!, "DataProtection:Certificate");
        var previous = previousSettings
            .Select((c, i) => Load(c, $"DataProtection:PreviousCertificates:{i}"))
            .ToList();
        return new DataProtectionCertificates(current, previous);
    }

    private static X509Certificate2 Load(DataProtectionCertificateSettings settings, string name)
    {
        byte[] pfx;
        try
        {
            pfx = Convert.FromBase64String(settings.Pfx!);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{name}:Pfx is not valid base64.");
        }

        X509Certificate2 certificate;
        try
        {
            // EphemeralKeySet: the private key stays in memory and is never written to a key store on disk.
            certificate = X509CertificateLoader.LoadPkcs12(
                pfx, string.IsNullOrEmpty(settings.Password) ? null : settings.Password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                $"{name} could not be read: the password is wrong, or the value is not a .pfx (PKCS#12) file.");
        }

        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"{name} must include its RSA private key (export it as a .pfx with the private key).");
        }

        if (rsa.KeySize < 2048)
        {
            certificate.Dispose();
            throw new InvalidOperationException($"{name} must use an RSA key of at least 2048 bits.");
        }

        return certificate;
    }
}
