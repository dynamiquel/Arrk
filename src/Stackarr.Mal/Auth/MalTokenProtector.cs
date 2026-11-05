using Microsoft.AspNetCore.DataProtection;

namespace Stackarr.Mal.Auth;

/// <summary>Encrypts MAL access/refresh tokens before they are stored in SQLite.</summary>
public sealed class MalTokenProtector(IDataProtectionProvider provider)
{
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
