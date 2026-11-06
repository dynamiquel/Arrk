namespace Arrk.Requests.Tests;

/// <summary>
/// The requests module owns its navigation: the Requests entry is contributed by the module (not the
/// shell) and is gated by <c>requests:enabled</c> so it disappears live when the system is switched
/// off.
/// </summary>
public class RequestsNavigationTests
{
    [Test]
    public async Task ContributesTheRequestsEntry_GatedByConfig()
    {
        RequestsNavigation navigation = new();

        await Assert.That(navigation.Entries).HasSingleItem();
        await Assert.That(navigation.Entries[0].Href).IsEqualTo("/requests");
        await Assert.That(navigation.Entries[0].EnabledConfigPath).IsEqualTo("requests");
    }

}
