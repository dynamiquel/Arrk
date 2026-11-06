using Microsoft.AspNetCore.DataProtection;

namespace Arrk.Mal.Auth;

/// <summary>Encrypts MAL access/refresh tokens before they are stored in SQLite.</summary>
public sealed class MalTokenProtector(IDataProtectionProvider provider)
{
    // Persisted tokens must remain decryptable after the application rename.
    readonly IDataProtector _protector = provider.CreateProtector("Stackarr.Mal.Tokens");

    public string Protect(string value) => string.IsNullOrEmpty(value) ? "" : _protector.Protect(value);

    public string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        try
        {
            return _protector.Unprotect(value);
        }
        catch
        {
            return "";
        }
    }
}
