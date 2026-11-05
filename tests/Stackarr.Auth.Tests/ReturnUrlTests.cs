using Stackarr.Auth.Services;

namespace Stackarr.Auth.Tests;

public class ReturnUrlTests
{
    [Test]
    [Arguments("/movies", "/movies")]
    [Arguments("/", "/")]
    [Arguments("", "/")]
    [Arguments(null, "/")]
    [Arguments("//evil.example.com", "/")]
    [Arguments("///evil.example.com", "/")]
    [Arguments("/\\evil.example.com", "/")]
    [Arguments("/%5Cevil.example.com", "/")]
    [Arguments("/%2F%2Fevil.example.com", "/")]
    [Arguments("https://evil.example.com", "/")]
    [Arguments("http://evil.example.com", "/")]
    [Arguments("movies", "/")]
    [Arguments("/movies?year=2024", "/movies?year=2024")]
    [Arguments("/a/b", "/a/b")]
    public async Task NormalizeReturnUrl_OnlyAllowsLocalAbsolutePaths(string? input, string expected)
    {
        await Assert.That(StackarrAuthenticationExtensions.NormalizeReturnUrl(input)).IsEqualTo(expected);
    }
}
