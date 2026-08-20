using ICloudCalendar.Infrastructure.CalDav;
using ICloudCalendar.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AppleAccountManagerTests
{
    [Fact]
    public async Task DisconnectAsyncRemovesCredentialBeforeLocalAccount()
    {
        var vault = Substitute.For<ICredentialVault>();
        var accounts = Substitute.For<IAccountCatalog>();
        var manager = new AppleAccountManager(vault, accounts);

        await manager.DisconnectAsync("account-1");

        Received.InOrder(() =>
        {
            vault.DeleteAsync("account-1", Arg.Any<CancellationToken>());
            accounts.DeleteAsync("account-1", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task DisconnectAsyncKeepsLocalAccountInertWhenKeyringCannotDeleteSecret()
    {
        var vault = Substitute.For<ICredentialVault>();
        var accounts = Substitute.For<IAccountCatalog>();
        vault.DeleteAsync("account-1", Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("keyring locked"));
        var manager = new AppleAccountManager(vault, accounts);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisconnectAsync("account-1"));

        await accounts.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }
}
