using Microsoft.Extensions.Configuration;
using Storefront.Api.Security;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Configuration;

/// <summary>Reading the certificate that encrypts the sign-in keys. Errors must be clear and must never echo the secrets.</summary>
[Trait("Category", TestCategories.Unit)]
public sealed class DataProtectionCertificatesTests
{
    private static DataProtectionCertificates Read(Dictionary<string, string?> settings, bool isDevelopment = false) =>
        DataProtectionCertificates.Read(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), isDevelopment);

    private static string ReadFails(Dictionary<string, string?> settings, bool isDevelopment = false)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Read(settings, isDevelopment));
        Assert.DoesNotContain(TestCertificates.Password, error.Message);
        foreach (var value in settings.Values.Where(v => v is { Length: > 20 }))
        {
            Assert.DoesNotContain(value!, error.Message);
        }

        return error.Message;
    }

    [Fact]
    public void Outside_development_a_certificate_is_required()
    {
        var message = ReadFails([]);
        Assert.Contains("DataProtection:Certificate:Pfx is required outside Development", message);
        Assert.Contains("AllowUnencryptedKeys", message);
    }

    [Fact]
    public void Development_runs_without_a_certificate()
    {
        var certificates = Read([], isDevelopment: true);
        Assert.Null(certificates.Current);
        Assert.Empty(certificates.Previous);
    }

    [Fact]
    public void Running_unencrypted_outside_development_is_an_explicit_opt_out()
    {
        Assert.Null(Read(new() { ["DataProtection:AllowUnencryptedKeys"] = "true" }).Current);
    }

    [Fact]
    public void Reads_the_current_and_previous_certificates()
    {
        var certificates = Read(TestCertificates.Settings(TestCertificates.A, TestCertificates.B));

        Assert.NotNull(certificates.Current);
        Assert.True(certificates.Current!.HasPrivateKey);
        var previous = Assert.Single(certificates.Previous);
        Assert.NotEqual(certificates.Current.Thumbprint, previous.Thumbprint);
    }

    [Fact]
    public void A_certificate_exported_without_a_password_is_accepted()
    {
        var pfx = TestCertificates.CreatePfx("No password", password: null);
        Assert.NotNull(Read(new() { ["DataProtection:Certificate:Pfx"] = pfx }).Current);
    }

    [Fact]
    public void Previous_certificates_without_a_current_one_are_refused()
    {
        var message = ReadFails(new()
        {
            ["DataProtection:PreviousCertificates:0:Pfx"] = TestCertificates.A,
            ["DataProtection:PreviousCertificates:0:Password"] = TestCertificates.Password,
        }, isDevelopment: true);
        Assert.Contains("PreviousCertificates is set but DataProtection:Certificate:Pfx is not", message);
    }

    [Fact]
    public void A_value_that_is_not_base64_is_refused()
    {
        var message = ReadFails(new() { ["DataProtection:Certificate:Pfx"] = "not base64 !!" });
        Assert.Equal("DataProtection:Certificate:Pfx is not valid base64.", message);
    }

    [Fact]
    public void A_wrong_password_is_refused_without_echoing_it()
    {
        var message = ReadFails(new()
        {
            ["DataProtection:Certificate:Pfx"] = TestCertificates.A,
            ["DataProtection:Certificate:Password"] = "the-wrong-password-123",
        });
        Assert.Contains("could not be read: the password is wrong", message);
        Assert.DoesNotContain("the-wrong-password-123", message);
    }

    [Fact]
    public void A_previous_certificate_with_a_wrong_password_is_named_by_position()
    {
        var settings = TestCertificates.Settings(TestCertificates.A, TestCertificates.B);
        settings["DataProtection:PreviousCertificates:0:Password"] = "wrong";
        Assert.StartsWith("DataProtection:PreviousCertificates:0 could not be read", ReadFails(settings));
    }

    [Fact]
    public void Base64_that_is_not_a_pfx_is_refused()
    {
        var message = ReadFails(new() { ["DataProtection:Certificate:Pfx"] = Convert.ToBase64String("hello"u8.ToArray()) });
        Assert.Contains("not a .pfx (PKCS#12) file", message);
    }

    [Fact]
    public void A_certificate_without_its_private_key_is_refused()
    {
        var pfx = TestCertificates.CreatePfx("Public only", includePrivateKey: false);
        var message = ReadFails(new()
        {
            ["DataProtection:Certificate:Pfx"] = pfx,
            ["DataProtection:Certificate:Password"] = TestCertificates.Password,
        });
        Assert.Contains("must include its RSA private key", message);
    }

    [Fact]
    public void A_short_rsa_key_is_refused()
    {
        var pfx = TestCertificates.CreatePfx("Short key", keySize: 1024);
        var message = ReadFails(new()
        {
            ["DataProtection:Certificate:Pfx"] = pfx,
            ["DataProtection:Certificate:Password"] = TestCertificates.Password,
        });
        Assert.Contains("at least 2048 bits", message);
    }
}
