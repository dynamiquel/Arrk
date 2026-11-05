using Stackarr.Bazarr;

namespace Stackarr.Bazarr.Tests;

public class BazarrApiKeyTests
{
    [Test]
    public async Task ParseApiKey_ReadsOnlyTheAuthSection()
    {
        const string yaml = """
        ---
        auth:
          apikey: 30077fa82688038a03c4d0c90150b098
          password: ''
          type: null
          username: ''
        radarr:
          apikey: ''
        sonarr:
          apikey: ''
        """;

        string? key = BazarrApiKeyStore.ParseApiKey(yaml);

        await Assert.That(key).IsEqualTo("30077fa82688038a03c4d0c90150b098");
    }

    [Test]
    public async Task ParseApiKey_ReturnsNullWhenAuthHasNoKey()
    {
        const string yaml = """
        auth:
          apikey: ''
          type: null
        """;

        await Assert.That(BazarrApiKeyStore.ParseApiKey(yaml)).IsNull();
    }
}
