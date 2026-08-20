using ICloudCalendar.Infrastructure.CalDav;
using ICloudCalendar.Infrastructure.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AppleCalendarOnboardingTests
{
    private readonly IAppleCalendarProbe _probe = Substitute.For<IAppleCalendarProbe>();
    private readonly ICredentialVault _vault = Substitute.For<ICredentialVault>();
    private readonly IAccountCatalog _accounts = Substitute.For<IAccountCatalog>();
    private readonly IAccountSynchronizer _synchronizer = Substitute.For<IAccountSynchronizer>();
    private readonly IAccountDiscoveryRefresher _discovery = Substitute.For<IAccountDiscoveryRefresher>();

    [Fact]
    public async Task ConnectAsyncValidatesBeforePersistingCredential()
    {
        var calendar = new DiscoveredCalendar(
            "work", "Work", new Uri("https://p01-caldav.icloud.com/calendars/work/"), "#00FF00", "token-1");
        _probe.DiscoverAsync("person@icloud.com", "app-password", Arg.Any<CancellationToken>())
            .Returns([calendar]);
        _synchronizer.SyncAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await Sut().ConnectAsync(" person@icloud.com ", "app-password");

        result.UserName.ShouldBe("person@icloud.com");
        result.AccountId.Length.ShouldBe(20);
        result.Calendars.ShouldBe([calendar]);
        Received.InOrder(() =>
        {
            _probe.DiscoverAsync("person@icloud.com", "app-password", Arg.Any<CancellationToken>());
            _vault.StoreAsync(result.AccountId, "app-password", Arg.Any<CancellationToken>());
            _accounts.SaveAsync(
                Arg.Is<CalendarAccount>(account => account.Id == result.AccountId),
                Arg.Any<IReadOnlyList<CalendarSubscription>>(),
                Arg.Any<CancellationToken>());
            _discovery.MarkCurrent(result.AccountId);
            _synchronizer.SyncAsync(result.AccountId, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ConnectAsyncDoesNotPersistCredentialWhenAuthenticationFails()
    {
        _probe.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DiscoveredCalendar>>(_ => throw new HttpRequestException(
                "Unauthorized", null, System.Net.HttpStatusCode.Unauthorized));

        await Should.ThrowAsync<HttpRequestException>(() => Sut().ConnectAsync("person@icloud.com", "wrong"));

        await _vault.DidNotReceiveWithAnyArgs().StoreAsync(default!, default!, default);
    }

    [Fact]
    public async Task ConnectAsyncUsesStableCaseInsensitiveAccountIdentifier()
    {
        _probe.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _synchronizer.SyncAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        var first = await Sut().ConnectAsync("Person@iCloud.com", "secret");
        var second = await Sut().ConnectAsync("person@icloud.com", "secret");

        first.AccountId.ShouldBe(second.AccountId);
    }

    [Fact]
    public async Task ConnectAsyncRemovesCredentialWhenMetadataCannotBeSaved()
    {
        _probe.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _accounts.SaveAsync(
                Arg.Any<CalendarAccount>(),
                Arg.Any<IReadOnlyList<CalendarSubscription>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(() => Sut().ConnectAsync("person@icloud.com", "secret"));

        await _vault.Received().DeleteAsync(Arg.Any<string>(), CancellationToken.None);
        await _synchronizer.DidNotReceiveWithAnyArgs().SyncAsync(default!, default);
    }

    [Fact]
    public async Task ConnectAsyncRestoresExistingCredentialWhenMetadataUpdateFails()
    {
        _probe.DiscoverAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _vault.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("previous-secret");
        _accounts.SaveAsync(
                Arg.Any<CalendarAccount>(),
                Arg.Any<IReadOnlyList<CalendarSubscription>>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Sut().ConnectAsync("person@icloud.com", "replacement-secret"));

        Received.InOrder(() =>
        {
            _vault.StoreAsync(Arg.Any<string>(), "replacement-secret", Arg.Any<CancellationToken>());
            _accounts.SaveAsync(
                Arg.Any<CalendarAccount>(),
                Arg.Any<IReadOnlyList<CalendarSubscription>>(),
                Arg.Any<CancellationToken>());
            _vault.StoreAsync(Arg.Any<string>(), "previous-secret", CancellationToken.None);
        });
        await _vault.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
    }

    private AppleCalendarOnboarding Sut() => new(_probe, _vault, _accounts, _synchronizer, _discovery);
}
