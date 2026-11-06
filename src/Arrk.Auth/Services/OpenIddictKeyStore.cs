using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Arrk.Auth.Services;

/// <summary>Signing and encryption keys for the Arrk OpenID Connect provider.</summary>
public sealed class OpenIddictCertificates
{
    public required X509Certificate2 Signing { get; init; }
    public required X509Certificate2 Encryption { get; init; }
}

/// <summary>
/// Loads or creates the certificates OpenIddict uses to sign and encrypt tokens. They are
/// persisted under the Arrk config directory so tokens remain valid across restarts.
/// </summary>
public static class OpenIddictKeyStore
{
    // On-disk contract: existing signing/encryption PFX files use this password.
    const string Password = "stackarr-openiddict-key";

    public static OpenIddictCertificates Ensure(string configDirectory)
    {
        string directory = Path.Combine(configDirectory, "openiddict");
        Directory.CreateDirectory(directory);

        return new OpenIddictCertificates
        {
            Signing = LoadOrCreate(Path.Combine(directory, "signing.pfx"), "CN=Arrk OIDC Signing"),
            Encryption = LoadOrCreate(Path.Combine(directory, "encryption.pfx"), "CN=Arrk OIDC Encryption")
        };
    }

    static X509Certificate2 LoadOrCreate(string path, string subject)
    {
        if (File.Exists(path))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(path, Password, X509KeyStorageFlags.Exportable);
            }
            catch
            {
                // Corrupt/old key material: regenerate below.
            }
        }

        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        byte[] pfx = certificate.Export(X509ContentType.Pfx, Password);
        File.WriteAllBytes(path, pfx);

        return X509CertificateLoader.LoadPkcs12(pfx, Password, X509KeyStorageFlags.Exportable);
    }
}
