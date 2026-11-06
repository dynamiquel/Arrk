namespace Arrk.Auth.Abstractions;

/// <summary>
/// Controls how new accounts come into existence. Arrk is private by default: the public
/// surface is a sign-in page, and accounts are either requested (and approved by an admin) or
/// created by an admin directly.
/// </summary>
public enum RegistrationMode
{
    /// <summary>Anyone may create an account; it is usable immediately.</summary>
    Open = 0,

    /// <summary>Anyone may request an account; it stays unusable until an admin approves it.</summary>
    Approval = 1,

    /// <summary>No self-service sign-up. Only administrators create accounts.</summary>
    AdminOnly = 2
}
