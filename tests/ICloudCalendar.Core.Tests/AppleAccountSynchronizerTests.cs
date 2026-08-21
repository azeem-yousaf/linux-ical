using ICloudCalendar.Infrastructure.CalDav;
using ICloudCalendar.Infrastructure.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ICloudCalendar.Core.Tests;

public sealed class AppleAccountSynchronizerTests
{
    private readonly IAccountCatalog _accounts = Substitute.For<IAccountCatalog>();
    private readonly ICredentialVault _credentials = Substitute.For<ICredentialVault>();
    private readonly IRemoteCalendarSyncSessionFactory _sessions = Substitute.For<IRemoteCalendarSyncSessionFactory>();
    private readonly IRemoteCalendarSyncSession _session = Substitute.For<IRemoteCalendarSyncSession>();
    private readonly IProjectionMaintenance _maintenance = Substitute.For<IProjectionMaintenance>();
    private readonly IAccountDiscoveryRefresher _discovery = Substitute.For<IAccountDiscoveryRefresher>();

    [Fact]
    public async Task SyncAsyncUsesOneAuthenticatedSessionForAllEnabledCalendars()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        var personal = Calendar("personal", true);
        var disabled = Calendar("birthdays", false);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work, personal, disabled]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldBe([
            new CalendarSyncOutcome("personal", true),
            new CalendarSyncOutcome("work", true)
        ]);
        _sessions.Received(1).Create(account, "secret");
        await _session.Received(1).SyncAsync(work, Arg.Any<CancellationToken>());
        await _session.Received(1).SyncAsync(personal, Arg.Any<CancellationToken>());
        await _session.DidNotReceive().SyncAsync(disabled, Arg.Any<CancellationToken>());
        _session.Received(1).Dispose();
    }

    [Fact]
    public async Task SyncAsyncReturnsSafeFailureWhenCredentialIsMissing()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldBe([new CalendarSyncOutcome(string.Empty, false, "credential_missing")]);
        _sessions.DidNotReceiveWithAnyArgs().Create(default!, default!);
    }

    [Fact]
    public async Task SyncAsyncIsolatesRemoteFailureToAffectedCalendar()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        var personal = Calendar("personal", true);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work, personal]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _session.SyncAsync(work, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new HttpRequestException("Unavailable"));

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldContain(new CalendarSyncOutcome("work", false, "remote_unavailable"));
        result.ShouldContain(new CalendarSyncOutcome("personal", true));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadRequest, "http_400")]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, "http_500")]
    public async Task SyncAsyncPreservesSafeHttpStatusForDiagnostics(
        System.Net.HttpStatusCode statusCode,
        string expectedCode)
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(work.Id, Arg.Any<CancellationToken>()).Returns(false);
        _session.SyncAsync(work, Arg.Any<CancellationToken>()).Returns<Task>(
            _ => throw new HttpRequestException("Remote failure", null, statusCode));

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldBe([new CalendarSyncOutcome("work", false, expectedCode)]);
    }

    [Fact]
    public async Task SyncAsyncClassifiesMalformedCalendarResponseWithoutLeakingDetails()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(work.Id, Arg.Any<CancellationToken>()).Returns(false);
        _session.SyncAsync(work, Arg.Any<CancellationToken>()).Returns<Task>(
            _ => throw new FormatException("Sensitive remote resource detail"));

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldBe([new CalendarSyncOutcome("work", false, "invalid_response")]);
    }

    [Fact]
    public async Task SyncAsyncPreservesSafeProtocolCategoryWithoutLeakingDetails()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(work.Id, Arg.Any<CancellationToken>()).Returns(false);
        _session.SyncAsync(work, Arg.Any<CancellationToken>()).Returns<Task>(
            _ => throw new CalDavDataException("protocol_sync_token_missing", "Sensitive detail"));

        var result = await Sut().SyncAsync(account.Id);

        result.ShouldBe([new CalendarSyncOutcome("work", false, "protocol_sync_token_missing")]);
    }

    [Fact]
    public async Task SyncAsyncMarksDueProjectionOnlyAfterSuccessfulFullSync()
    {
        var account = new CalendarAccount("account-1", "person@icloud.com");
        var work = Calendar("work", true);
        _accounts.GetAccountAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
        _accounts.GetCalendarsAsync(account.Id, Arg.Any<CancellationToken>()).Returns([work]);
        _credentials.RetrieveAsync(account.Id, Arg.Any<CancellationToken>()).Returns("secret");
        _sessions.Create(account, "secret").Returns(_session);
        _maintenance.PrepareIfDueAsync(work.Id, Arg.Any<CancellationToken>()).Returns(true);

        await Sut().SyncAsync(account.Id);

        Received.InOrder(() =>
        {
            _maintenance.PrepareIfDueAsync(work.Id, Arg.Any<CancellationToken>());
            _session.SyncAsync(work, Arg.Any<CancellationToken>());
            _maintenance.MarkCompletedAsync(work.Id, Arg.Any<CancellationToken>());
        });
    }

    private AppleAccountSynchronizer Sut() => new(_accounts, _credentials, _sessions, _maintenance, _discovery);

    private static CalendarSubscription Calendar(string id, bool enabled) => new(
        id,
        "account-1",
        id,
        new Uri($"https://p01-caldav.icloud.com/calendars/{id}/"),
        null,
        enabled);
}
