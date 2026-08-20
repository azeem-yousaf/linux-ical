using ICloudCalendar.Infrastructure.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class SecretToolCredentialVaultTests
{
    private readonly ISecretToolRunner _runner = Substitute.For<ISecretToolRunner>();

    [Fact]
    public async Task StoreAsyncSendsSecretOnlyThroughStandardInput()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SecretToolResult(0, string.Empty, string.Empty));
        var vault = new SecretToolCredentialVault(_runner);

        await vault.StoreAsync("account-1", "app-specific-secret");

        await _runner.Received().RunAsync(
            Arg.Is<IReadOnlyList<string>>(arguments =>
                arguments.Contains("account-1") && !arguments.Contains("app-specific-secret")),
            "app-specific-secret",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAsyncTrimsOnlyLineEndingFromSecret()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(new SecretToolResult(0, "  secret value  \n", string.Empty));

        var secret = await new SecretToolCredentialVault(_runner).RetrieveAsync("account-1");

        secret.ShouldBe("  secret value  ");
    }

    [Fact]
    public async Task RetrieveAsyncReturnsNullWhenCredentialDoesNotExist()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), null, Arg.Any<CancellationToken>())
            .Returns(new SecretToolResult(1, string.Empty, string.Empty));

        (await new SecretToolCredentialVault(_runner).RetrieveAsync("missing")).ShouldBeNull();
    }

    [Fact]
    public async Task StoreAsyncDoesNotIncludeSecretInFailureMessage()
    {
        _runner.RunAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SecretToolResult(2, string.Empty, "Secret Service is locked"));

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new SecretToolCredentialVault(_runner).StoreAsync("account-1", "never-log-me"));

        exception.Message.ShouldNotContain("never-log-me");
        exception.Message.ShouldContain("locked");
    }
}
