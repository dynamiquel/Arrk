using System.Security.Cryptography;
using System.Text;
using Stackarr.Core.Playback;

namespace Stackarr.Core.Tests.Playback;

/// <summary>
/// Covers the signed stream tokens that let AirPlay receivers (which never send the browser's
/// cookies) fetch playback through Stackarr's stream proxies. A token must be scoped and expiring,
/// and tampering with any part of it must be rejected.
/// </summary>
public class StreamTokenServiceTests
{
    static StreamTokenService Service() => new(RandomNumberGenerator.GetBytes(32));

    [Test]
    public async Task Create_ThenValidate_RoundTripsScopeAndSubject()
    {
        StreamTokenService tokens = Service();

        string token = tokens.Create("media", "session-123", TimeSpan.FromHours(1));

        bool valid = tokens.TryValidate(token, "media", out string subject, out DateTimeOffset expiresAt);
        await Assert.That(valid).IsTrue();
        await Assert.That(subject).IsEqualTo("session-123");
        await Assert.That(expiresAt).IsGreaterThan(DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task Validate_RejectsWrongScope()
    {
        StreamTokenService tokens = Service();

        string token = tokens.Create("media", "session-123", TimeSpan.FromHours(1));

        await Assert.That(tokens.TryValidate(token, "music", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Validate_RejectsExpiredToken()
    {
        StreamTokenService tokens = Service();

        string token = tokens.Create("media", "session-123", TimeSpan.FromSeconds(-1));

        await Assert.That(tokens.TryValidate(token, "media", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Validate_RejectsTamperedPayload()
    {
        StreamTokenService tokens = Service();
        string token = tokens.Create("media", "session-123", TimeSpan.FromHours(1));

        // Flip the last character of the payload segment.
        int separator = token.IndexOf('.');
        char last = token[separator - 1];
        string tampered = token[..(separator - 1)] + (last == 'A' ? 'B' : 'A') + token[separator..];

        await Assert.That(tokens.TryValidate(tampered, "media", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Validate_RejectsTokenSignedWithAnotherKey()
    {
        StreamTokenService issuer = Service();
        StreamTokenService verifier = Service();

        string token = issuer.Create("media", "session-123", TimeSpan.FromHours(1));

        await Assert.That(verifier.TryValidate(token, "media", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Validate_RejectsGarbage()
    {
        StreamTokenService tokens = Service();

        await Assert.That(tokens.TryValidate(null, "media", out _, out _)).IsFalse();
        await Assert.That(tokens.TryValidate("", "media", out _, out _)).IsFalse();
        await Assert.That(tokens.TryValidate("not-a-token", "media", out _, out _)).IsFalse();
        await Assert.That(tokens.TryValidate("a.b", "media", out _, out _)).IsFalse();
    }

    [Test]
    public async Task Token_IsUrlSafe()
    {
        StreamTokenService tokens = Service();

        string token = tokens.Create("media", "session-123", TimeSpan.FromHours(1));

        await Assert.That(token).DoesNotContain("/");
        await Assert.That(token).DoesNotContain("+");
        await Assert.That(token).DoesNotContain("=");
    }

    [Test]
    public async Task Load_PersistsAndReusesTheKeyAcrossInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "stackarr-stream-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            StreamTokenService first = StreamTokenService.Load(directory);
            string token = first.Create("media", "session-123", TimeSpan.FromHours(1));

            // A fresh instance loaded from the same directory must verify the earlier token, which
            // is what keeps an in-flight cast working across a restart.
            StreamTokenService second = StreamTokenService.Load(directory);

            await Assert.That(second.TryValidate(token, "media", out string subject, out _)).IsTrue();
            await Assert.That(subject).IsEqualTo("session-123");
            await Assert.That(File.Exists(Path.Combine(directory, "stream-token.key"))).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Load_RegeneratesWhenTheKeyFileIsCorrupt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "stackarr-stream-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "stream-token.key"), "not base64 !!!");

            StreamTokenService tokens = StreamTokenService.Load(directory);
            string token = tokens.Create("media", "session-123", TimeSpan.FromHours(1));

            await Assert.That(tokens.TryValidate(token, "media", out _, out _)).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Constructor_RejectsShortKeys()
    {
        await Assert.That(() => new StreamTokenService(Encoding.UTF8.GetBytes("short")))
            .Throws<ArgumentException>();
    }
}
