using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Arrk.Auth.Services;

namespace Arrk.Auth.Tests;

public class OpenIddictKeyStoreTests
{
    [Test]
    public async Task Ensure_LoadsLegacyCertificates_WithoutReplacingFilesOrPrivateKeys()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"arrk-legacy-keys-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "openiddict"));
        try
        {
            using RSA signingKey = RSA.Create(2048);
            using RSA encryptionKey = RSA.Create(2048);
            CertificateRequest signingRequest = new("CN=Stackarr OIDC Signing", signingKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            CertificateRequest encryptionRequest = new("CN=Stackarr OIDC Encryption", encryptionKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 signing = signingRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            using X509Certificate2 encryption = encryptionRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            byte[] signingPfx = signing.Export(X509ContentType.Pfx, "stackarr-openiddict-key");
            byte[] encryptionPfx = encryption.Export(X509ContentType.Pfx, "stackarr-openiddict-key");
            string signingPath = Path.Combine(directory, "openiddict", "signing.pfx");
            string encryptionPath = Path.Combine(directory, "openiddict", "encryption.pfx");
            File.WriteAllBytes(signingPath, signingPfx);
            File.WriteAllBytes(encryptionPath, encryptionPfx);

            OpenIddictCertificates loaded = OpenIddictKeyStore.Ensure(directory);
            using X509Certificate2 loadedSigning = loaded.Signing;
            using X509Certificate2 loadedEncryption = loaded.Encryption;

            await Assert.That(loadedSigning.Thumbprint).IsEqualTo(signing.Thumbprint);
            await Assert.That(loadedEncryption.Thumbprint).IsEqualTo(encryption.Thumbprint);
            await Assert.That(File.ReadAllBytes(signingPath).SequenceEqual(signingPfx)).IsTrue();
            await Assert.That(File.ReadAllBytes(encryptionPath).SequenceEqual(encryptionPfx)).IsTrue();

            byte[] payload = RandomNumberGenerator.GetBytes(32);
            using RSA loadedSigningKey = loadedSigning.GetRSAPrivateKey()!;
            using RSA loadedEncryptionKey = loadedEncryption.GetRSAPrivateKey()!;
            byte[] signature = loadedSigningKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            await Assert.That(signingKey.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)).IsTrue();
            byte[] encrypted = encryptionKey.Encrypt(payload, RSAEncryptionPadding.OaepSHA256);
            await Assert.That(loadedEncryptionKey.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA256)
                .SequenceEqual(payload)).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Ensure_NewCertificates_AreReusedOnNextLoad()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"arrk-new-keys-{Guid.NewGuid():N}");
        try
        {
            OpenIddictCertificates first = OpenIddictKeyStore.Ensure(directory);
            using X509Certificate2 firstSigning = first.Signing;
            using X509Certificate2 firstEncryption = first.Encryption;
            OpenIddictCertificates second = OpenIddictKeyStore.Ensure(directory);
            using X509Certificate2 secondSigning = second.Signing;
            using X509Certificate2 secondEncryption = second.Encryption;

            await Assert.That(secondSigning.Thumbprint).IsEqualTo(firstSigning.Thumbprint);
            await Assert.That(secondEncryption.Thumbprint).IsEqualTo(firstEncryption.Thumbprint);
            await Assert.That(secondSigning.HasPrivateKey).IsTrue();
            await Assert.That(secondEncryption.HasPrivateKey).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
