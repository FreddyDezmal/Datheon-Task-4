// Creates the certificate that encrypts the Storefront API's sign-in keys (ASP.NET Core Data
// Protection) in the database. See Documentation/Deployment.md, "Encrypting the sign-in keys".
//
//   dotnet run tools/new-data-protection-certificate.cs                 (writes to ~/storefront-secrets)
//   dotnet run tools/new-data-protection-certificate.cs -- <folder>     (writes to <folder>)
//
// Writes two files, readable only by you, to a folder outside any git repository:
//   storefront-data-protection.pfx              the certificate and private key, password-protected (your backup)
//   storefront-data-protection.pfx.base64.txt   the same file as base64: the value for DataProtection__Certificate__Pfx
//
// Nothing is added to the Windows certificate store, and nothing secret is printed.

// A plain build (file-based apps default to native AOT, which downloads a compiler this tool doesn't need).
#:property PublishAot=false

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

const int KeySize = 3072;
const int ValidYears = 3;
const string FileName = "storefront-data-protection.pfx";

var folder = Path.GetFullPath(args.Length > 0
    ? args[0]
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "storefront-secrets"));

if (InsideGitRepository(folder) is { } repository)
{
    return Fail($"{folder} is inside the git repository at {repository}. Choose a folder outside it, so the key can't be committed.");
}

var pfxPath = Path.Combine(folder, FileName);
var base64Path = pfxPath + ".base64.txt";
if (File.Exists(pfxPath) || File.Exists(base64Path))
{
    return Fail($"{pfxPath} already exists. Move it away first: replacing the certificate signs everyone out unless you rotate (Deployment.md).");
}

var password = ReadPassword("Choose a password for the certificate (at least 12 characters): ");
if (password.Length < 12)
{
    return Fail("The password must be at least 12 characters.");
}

if (ReadPassword("Type it again: ") != password)
{
    return Fail("The passwords don't match.");
}

using var rsa = RSA.Create(KeySize);
var request = new CertificateRequest("CN=Storefront.Api Data Protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
var now = DateTimeOffset.UtcNow;
using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidYears));

// PBES2 with AES-256, rather than the legacy PKCS#12 encryption.
var pfx = certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Pbes2Aes256Sha256, password);

// Read it back the way the API does, so a file that the API can't use is never handed out.
using (var check = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet))
using (var key = check.GetRSAPrivateKey())
{
    if (key is null)
    {
        return Fail("The exported certificate has no private key. Nothing was written.");
    }
}

Directory.CreateDirectory(folder);
WritePrivate(pfxPath, pfx);
WritePrivate(base64Path, System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(pfx)));

Console.WriteLine();
Console.WriteLine($"Created  {pfxPath}");
Console.WriteLine($"         {base64Path}");
Console.WriteLine($"Thumbprint {certificate.Thumbprint}, expires {certificate.NotAfter.ToUniversalTime():yyyy-MM-dd}.");
Console.WriteLine();
Console.WriteLine("Next (Documentation/Deployment.md, \"Encrypting the sign-in keys\"):");
Console.WriteLine("  1. Set DataProtection__Certificate__Pfx to the contents of the .base64.txt file.");
Console.WriteLine("  2. Set DataProtection__Certificate__Password to the password you just chose.");
Console.WriteLine("  3. Keep the .pfx and the password somewhere safe (a password manager). Never commit them.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string? InsideGitRepository(string folder)
{
    for (var directory = new DirectoryInfo(folder); directory is not null; directory = directory.Parent)
    {
        var git = Path.Combine(directory.FullName, ".git");
        if (Directory.Exists(git) || File.Exists(git))
        {
            return directory.FullName;
        }
    }

    return null;
}

static void WritePrivate(string path, byte[] contents)
{
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
    {
        stream.Write(contents);
    }

    // On Windows the user profile is already private to you; elsewhere, make the file owner-only.
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

static string ReadPassword(string prompt)
{
    Console.Write(prompt);
    if (Console.IsInputRedirected)
    {
        var line = Console.ReadLine() ?? string.Empty;
        Console.WriteLine();
        return line;
    }

    var password = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return password.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Length--;
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
        }
    }
}
