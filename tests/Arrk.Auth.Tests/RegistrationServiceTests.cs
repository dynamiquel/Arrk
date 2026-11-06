using Arrk.Auth.Services;
using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Tests;

/// <summary>
/// Self-service sign-up creates a real placeholder account. In approval mode it gets the
/// <see cref="UserRole.Pending"/> role, which grants no access until an admin promotes it.
/// </summary>
public class RegistrationServiceTests
{
    [Test]
    public async Task ApprovalMode_CreatesPlaceholderAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        RegistrationOutcome outcome = await host.Registration.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(outcome.RequiresApproval).IsTrue();
        await Assert.That(outcome.User.Role).IsEqualTo(UserRole.Pending);

        ArrkUser? user = await host.Users.GetByUsernameAsync("newbie");
        await Assert.That(user).IsNotNull();
        await Assert.That(user!.Role).IsEqualTo(UserRole.Pending);
        await Assert.That(user.IsActive).IsTrue();

        IReadOnlyList<ArrkUser> pending = await host.Users.GetUsersInRoleAsync(ArrkRoles.Pending);
        await Assert.That(pending.Any(u => u.Username == "newbie")).IsTrue();
    }

    [Test]
    public async Task ApprovalMode_PendingCredentialsAreValidButRoleBlocksAccess()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Registration.RegisterAsync("newbie", "hunter2pass");

        ArrkUser? authenticated = await host.Users.AuthenticateAsync("newbie", "hunter2pass");

        await Assert.That(authenticated).IsNotNull();
        await Assert.That(authenticated!.Role).IsEqualTo(UserRole.Pending);
    }

    [Test]
    public async Task ApprovalMode_ChangingRoleToUserActivatesTheAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        RegistrationOutcome signup = await host.Registration.RegisterAsync("newbie", "hunter2pass");

        await host.Users.SetRoleAsync(signup.User.Id, UserRole.User);

        ArrkUser? user = await host.Users.GetByUsernameAsync("newbie");
        await Assert.That(user!.Role).IsEqualTo(UserRole.User);
        await Assert.That(await host.Users.AuthenticateAsync("newbie", "hunter2pass")).IsNotNull();
    }

    [Test]
    public async Task OpenMode_CreatesNormalUserImmediately()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "Open");

        RegistrationOutcome outcome = await host.Registration.RegisterAsync("newbie", "hunter2pass");

        await Assert.That(outcome.RequiresApproval).IsFalse();
        await Assert.That(outcome.User.Role).IsEqualTo(UserRole.User);
        await Assert.That(await host.Users.GetUsersInRoleAsync(ArrkRoles.Pending)).IsEmpty();
    }

    [Test]
    public async Task AdminOnlyMode_RejectsSelfServiceSignUp()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:registrationMode"] = "AdminOnly");

        await Assert.That(async () => await host.Registration.RegisterAsync("newbie", "hunter2pass"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PasswordLoginDisabled_RejectsSelfServiceSignUp()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowPasswordLogin"] = "false");

        await Assert.That(async () => await host.Registration.RegisterAsync("newbie", "hunter2pass"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LocalSignUpDisabled_RejectsSelfServiceSignUpButKeepsPasswordLogin()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync(settings =>
            settings["auth:allowLocalSignUp"] = "false");

        await Assert.That(async () => await host.Registration.RegisterAsync("newbie", "hunter2pass"))
            .Throws<InvalidOperationException>();

        // Existing accounts can still sign in with a password.
        await host.Users.CreateAsync("existing", "hunter2pass", UserRole.User);
        await Assert.That(await host.Users.AuthenticateAsync("existing", "hunter2pass")).IsNotNull();
    }

    [Test]
    public async Task Register_RejectsExistingUsernameIgnoringCase()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await Assert.That(async () => await host.Registration.RegisterAsync("ALICE", "hunter2pass"))
            .Throws<InvalidOperationException>();

        // A pending account also reserves its username.
        await host.Registration.RegisterAsync("newbie", "hunter2pass");
        await Assert.That(async () => await host.Registration.RegisterAsync("NEWBIE", "hunter3pass"))
            .Throws<InvalidOperationException>();
    }
}
