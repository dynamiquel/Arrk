using Microsoft.AspNetCore.Identity;
using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Tests;

/// <summary>
/// Exercises the Identity-backed account service against a real SQLite database.
/// </summary>
public class UserServiceTests
{
    [Test]
    public async Task CreateAsync_WithPassword_HashesAndAssignsRole()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User, "Alice A");

        await Assert.That(created.Id).IsGreaterThan(0);
        await Assert.That(created.Username).IsEqualTo("alice");
        await Assert.That(created.DisplayName).IsEqualTo("alice"); // display name tracks the username
        await Assert.That(created.PasswordHash).IsNotNull();
        await Assert.That(created.PasswordHash!).DoesNotContain("hunter2pass");
        await Assert.That(await host.Users.GetRolesAsync(created)).IsEquivalentTo(new[] { ArrkRoles.User });
        await Assert.That(await host.Users.AuthenticateAsync("alice", "hunter2pass")).IsNotNull();
    }

    [Test]
    public async Task CreateAdmin_AssignsBothIdentityRolesAndIncludesAdminInUserQueries()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser admin = await host.Users.CreateAsync("boss", "hunter2pass", UserRole.Admin);

        await Assert.That(await host.Users.GetRolesAsync(admin))
            .IsEquivalentTo(new[] { ArrkRoles.Admin, ArrkRoles.User });
        await Assert.That(await host.Users.IsInRoleAsync(admin, ArrkRoles.User)).IsTrue();
        await Assert.That((await host.Users.GetUsersInRoleAsync(ArrkRoles.User)).Any(u => u.Id == admin.Id)).IsTrue();
        await Assert.That((await host.Users.GetByIdAsync(admin.Id))!.Role).IsEqualTo(UserRole.Admin);
        await Assert.That((await host.Users.GetSessionStateAsync(admin.Id))!.Role).IsEqualTo(UserRole.Admin);
    }

    [Test]
    public async Task SetRole_PromotionsAndDemotionsReplaceAllMemberships()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser user = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.Pending);
        await Assert.That(await host.Users.GetRolesAsync(user)).IsEquivalentTo(new[] { ArrkRoles.Pending });

        user = await host.Users.SetRoleAsync(user.Id, UserRole.Admin);
        await Assert.That(user.Role).IsEqualTo(UserRole.Admin);
        await Assert.That(await host.Users.GetRolesAsync(user))
            .IsEquivalentTo(new[] { ArrkRoles.Admin, ArrkRoles.User });

        user = await host.Users.SetRoleAsync(user.Id, UserRole.User);
        await Assert.That(user.Role).IsEqualTo(UserRole.User);
        await Assert.That(await host.Users.GetRolesAsync(user)).IsEquivalentTo(new[] { ArrkRoles.User });

        await host.Users.SetRoleAsync(user.Id, UserRole.Admin);
        user = await host.Users.SetRoleAsync(user.Id, UserRole.Pending);
        await Assert.That(user.Role).IsEqualTo(UserRole.Pending);
        await Assert.That(await host.Users.GetRolesAsync(user)).IsEquivalentTo(new[] { ArrkRoles.Pending });
        await Assert.That((await host.Users.GetUsersInRoleAsync(ArrkRoles.User)).Any(u => u.Id == user.Id)).IsFalse();
    }

    [Test]
    public async Task CreateAsync_WithoutPassword_CreatesPasswordlessAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ArrkUser created = await host.Users.CreateAsync("nopass", null, UserRole.User);

        await Assert.That(created.PasswordHash).IsNull();
        await Assert.That(await host.Users.AuthenticateAsync("nopass", "anything")).IsNull();
    }

    [Test]
    public async Task CreateAsync_RejectsDuplicateUsernameIgnoringCase()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await Assert.That(async () => await host.Users.CreateAsync("ALICE", "another-pass", UserRole.User))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task CreateAsync_RejectsInvalidUsername()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await Assert.That(async () => await host.Users.CreateAsync("ab", "hunter2pass", UserRole.User))
            .Throws<ArgumentException>();
        await Assert.That(async () => await host.Users.CreateAsync("bad name", "hunter2pass", UserRole.User))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task CreateAsync_RejectsShortPassword()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        await Assert.That(async () => await host.Users.CreateAsync("alice", "x", UserRole.User))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AuthenticateAsync_WrongPasswordAndUnknownUser_ReturnNull()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await Assert.That(await host.Users.AuthenticateAsync("alice", "wrong")).IsNull();
        await Assert.That(await host.Users.AuthenticateAsync("nobody", "hunter2pass")).IsNull();
    }

    [Test]
    public async Task AuthenticateAsync_RecordsLastLogin()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);
        await Assert.That(created.LastLoginAt).IsNull();

        await host.Users.AuthenticateAsync("alice", "hunter2pass");

        ArrkUser reloaded = (await host.Users.GetByIdAsync(created.Id))!;
        await Assert.That(reloaded.LastLoginAt).IsNotNull();
    }

    [Test]
    public async Task AuthenticateAsync_WhenDisabled_ReturnsNull()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);
        await host.Users.SetActiveAsync(created.Id, false);

        await Assert.That(await host.Users.AuthenticateAsync("alice", "hunter2pass")).IsNull();
    }

    [Test]
    public async Task ChangePasswordAsync_RequiresCurrentPassword()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await Assert.That(async () => await host.Users.ChangePasswordAsync(created.Id, "wrong", "newpass1"))
            .Throws<InvalidOperationException>();

        await host.Users.ChangePasswordAsync(created.Id, "hunter2pass", "newpass1");

        await Assert.That(await host.Users.AuthenticateAsync("alice", "hunter2pass")).IsNull();
        await Assert.That(await host.Users.AuthenticateAsync("alice", "newpass1")).IsNotNull();
    }

    [Test]
    public async Task ResetPasswordAsync_DoesNotRequireCurrentPassword()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await host.Users.ResetPasswordAsync(created.Id, "resetpass");

        await Assert.That(await host.Users.AuthenticateAsync("alice", "resetpass")).IsNotNull();
    }

    [Test]
    public async Task UpdateProfileAsync_ChangesUsernameAndDisplayName()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await host.Users.UpdateProfileAsync(created.Id, "alicia", "Alicia");

        await Assert.That(await host.Users.AuthenticateAsync("alicia", "hunter2pass")).IsNotNull();
        await Assert.That((await host.Users.GetByIdAsync(created.Id))!.DisplayName).IsEqualTo("alicia");
    }

    [Test]
    public async Task SetRoleAndActiveAndAvatar_Persist()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await host.Users.SetRoleAsync(created.Id, UserRole.Admin);
        await host.Users.SetActiveAsync(created.Id, false);
        byte[] avatar = [1, 2, 3, 4];
        await host.Users.SetAvatarAsync(created.Id, avatar, "image/png");

        ArrkUser reloaded = (await host.Users.GetByIdAsync(created.Id))!;
        await Assert.That(reloaded.Role).IsEqualTo(UserRole.Admin);
        await Assert.That(reloaded.IsActive).IsFalse();
        await Assert.That(reloaded.Avatar).IsEquivalentTo(avatar);
        await Assert.That(reloaded.AvatarContentType).IsEqualTo("image/png");
    }

    [Test]
    public async Task DeleteAsync_RemovesTheAccount()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        ArrkUser created = await host.Users.CreateAsync("alice", "hunter2pass", UserRole.User);

        await host.Users.DeleteAsync(created.Id);

        await Assert.That(await host.Users.GetByIdAsync(created.Id)).IsNull();
    }

    [Test]
    [Arguments(UserRole.User)]
    [Arguments(UserRole.Admin)]
    [Arguments(UserRole.Pending)]
    public async Task GetOrCreateExternalUser_CreatesOnceAndLinksLogin(UserRole role)
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ArrkUser? first = await host.Users.GetOrCreateExternalUserAsync(
            "discord", "sub-1", "alice", "Alice", avatarUrl: null, newUserRole: role, createIfMissing: true);
        ArrkUser? second = await host.Users.GetOrCreateExternalUserAsync(
            "discord", "sub-1", "alice", "Alice", avatarUrl: null, createIfMissing: true);

        await Assert.That(first).IsNotNull();
        await Assert.That(second!.Id).IsEqualTo(first!.Id);
        await Assert.That(second.Role).IsEqualTo(role);
        string[] expectedRoles = role == UserRole.Admin
            ? [ArrkRoles.Admin, ArrkRoles.User]
            : [Services.UserService.RoleName(role)];
        await Assert.That(await host.Users.GetRolesAsync(second)).IsEquivalentTo(expectedRoles);
        await Assert.That((await host.Users.GetExternalLoginsAsync(first.Id)).Any(l => l.LoginProvider == "discord")).IsTrue();
    }

    [Test]
    public async Task GetOrCreateExternalUser_WhenCreationDisabled_ReturnsNull()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();

        ArrkUser? result = await host.Users.GetOrCreateExternalUserAsync(
            "discord", "unknown", "nobody", null, null, createIfMissing: false);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetAllAsync_PopulatesRole()
    {
        await using AuthTestHost host = await AuthTestHost.CreateAsync();
        await host.Users.CreateAsync("boss", "hunter2pass", UserRole.Admin);

        IReadOnlyList<ArrkUser> all = await host.Users.GetAllAsync();

        await Assert.That(all.Any(u => u.Username == "boss" && u.Role == UserRole.Admin)).IsTrue();
    }
}
